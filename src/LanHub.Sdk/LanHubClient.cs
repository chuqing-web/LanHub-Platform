using System.Net.Sockets;
using System.Text;
using LanHub.Core.Models;
using LanHub.Core.Protocol;

namespace LanHub.Sdk;

/// <summary>
/// Game-facing LanHub SDK client (session + messaging surface).
/// </summary>
public sealed class LanHubClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private Task? _heartbeat;
    private List<PlayerInfo> _players = [];
    private long _sequence;
    private long _lastPongSequence;
    private DateTime _lastPongUtc = DateTime.UtcNow;
    private ConnectParams? _params;
    private int _reconnectAttempt;
    private bool _disposed;
    private int _disconnectGate; // 0 = idle, 1 = handling

    public string PlayerId { get; private set; } = "";
    public string GameId { get; private set; } = "";
    public LanHubSessionInfo Session { get; private set; } = new();
    public LanHubConnectionState State { get; private set; } = LanHubConnectionState.Disconnected;
    public bool IsConnected => State == LanHubConnectionState.Connected;
    public bool AutoReconnect { get; set; } = true;
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(3);
    public TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(12);
    public int MaxReconnectAttempts { get; set; } = 8;

    public event Action<LanHubMessage>? MessageReceived;
    public event Action<IReadOnlyList<PlayerInfo>>? RosterUpdated;
    public event Action<PlayerInfo>? PlayerJoined;
    public event Action<PlayerInfo>? PlayerLeft;
    public event Action<LanHubSessionInfo>? SessionUpdated;
    public event Action<LanHubConnectionState>? StateChanged;
    public event Action? Connected;
    public event Action<LanHubErrorCode, string>? Disconnected;
    public event Action<LanHubErrorCode, string>? Error;

    // Back-compat simple handlers
    public event Action<string, byte[]>? MessageReceivedSimple;

    public IReadOnlyList<PlayerInfo> Players => _players;

    private sealed record ConnectParams(int SdkPort, string Token, string GameId, string PlayerId);

    public static async Task<LanHubClient> ConnectFromEnvironmentAsync(CancellationToken ct = default)
    {
        var token = Environment.GetEnvironmentVariable("LANHUB_TOKEN")
                    ?? throw new LanHubException(LanHubErrorCode.InvalidArgument, "缺少 LANHUB_TOKEN");
        var gameId = Environment.GetEnvironmentVariable("LANHUB_GAME_ID")
                     ?? throw new LanHubException(LanHubErrorCode.InvalidArgument, "缺少 LANHUB_GAME_ID");
        var playerId = Environment.GetEnvironmentVariable("LANHUB_PLAYER_ID")
                       ?? throw new LanHubException(LanHubErrorCode.InvalidArgument, "缺少 LANHUB_PLAYER_ID");
        var portText = Environment.GetEnvironmentVariable("LANHUB_SDK_PORT") ?? "37812";
        if (!int.TryParse(portText, out var port)) port = 37812;

        var client = new LanHubClient();
        await client.ConnectAsync(port, token, gameId, playerId, ct).ConfigureAwait(false);
        return client;
    }

    public async Task ConnectAsync(int sdkPort, string token, string gameId, string playerId, CancellationToken ct = default)
    {
        _params = new ConnectParams(sdkPort, token, gameId, playerId);
        GameId = gameId;
        PlayerId = playerId;
        await ConnectInternalAsync(ct, isReconnect: false).ConfigureAwait(false);
    }

    public async Task SendToAllAsync(
        byte[] payload,
        string channel = LanHubChannel.Reliable,
        bool? reliable = null,
        CancellationToken ct = default)
    {
        await SendCoreAsync(null, payload, channel, reliable, ct).ConfigureAwait(false);
    }

    public async Task SendToAsync(
        string targetPlayerId,
        byte[] payload,
        string channel = LanHubChannel.Reliable,
        bool? reliable = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(targetPlayerId))
            throw new LanHubException(LanHubErrorCode.InvalidArgument, "targetPlayerId 为空");
        await SendCoreAsync(targetPlayerId, payload, channel, reliable, ct).ConfigureAwait(false);
    }

    public Task SendTextAsync(string text, string channel = LanHubChannel.Reliable, CancellationToken ct = default) =>
        SendToAllAsync(Encoding.UTF8.GetBytes(text), channel, reliable: true, ct);

    public Task SendUnreliableToAllAsync(byte[] payload, CancellationToken ct = default) =>
        SendToAllAsync(payload, LanHubChannel.Unreliable, reliable: false, ct);

    public PlayerInfo? GetPlayer(string playerId) =>
        _players.FirstOrDefault(p => p.PlayerId == playerId);

    public bool IsHost => Session.IsHost;

    private async Task SendCoreAsync(
        string? targetPlayerId,
        byte[] payload,
        string channel,
        bool? reliable,
        CancellationToken ct)
    {
        EnsureConnected();
        if (payload.Length > Session.MaxPayloadBytes)
            throw new LanHubException(LanHubErrorCode.PayloadTooLarge,
                $"消息 {payload.Length} 字节超过上限 {Session.MaxPayloadBytes}");

        var isReliable = reliable ?? !string.Equals(channel, LanHubChannel.Unreliable, StringComparison.OrdinalIgnoreCase);
        var seq = Interlocked.Increment(ref _sequence);

        try
        {
            await WriteEnvelopeAsync(new WireEnvelope
            {
                Type = string.IsNullOrEmpty(targetPlayerId) ? WireTypes.SdkBroadcast : WireTypes.SdkRelay,
                PlayerId = PlayerId,
                TargetPlayerId = targetPlayerId,
                PayloadBase64 = Convert.ToBase64String(payload),
                Channel = channel,
                Reliable = isReliable,
                Sequence = seq
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RaiseError(LanHubErrorCode.SendFailed, ex.Message);
            throw new LanHubException(LanHubErrorCode.SendFailed, ex.Message);
        }
    }

    private async Task ConnectInternalAsync(CancellationToken ct, bool isReconnect)
    {
        if (_params == null)
            throw new LanHubException(LanHubErrorCode.InvalidArgument, "未设置连接参数");

        SetState(isReconnect ? LanHubConnectionState.Reconnecting : LanHubConnectionState.Connecting);
        await TeardownSocketAsync().ConfigureAwait(false);

        try
        {
            _tcp = new TcpClient();
            await _tcp.ConnectAsync("127.0.0.1", _params.SdkPort, ct).ConfigureAwait(false);
            _stream = _tcp.GetStream();

            await FrameCodec.WriteJsonAsync(_stream, new WireEnvelope
            {
                Type = WireTypes.SdkHello,
                LaunchToken = _params.Token,
                GameId = _params.GameId,
                PlayerId = _params.PlayerId
            }, ct).ConfigureAwait(false);

            var ok = await FrameCodec.ReadJsonAsync<WireEnvelope>(_stream, ct).ConfigureAwait(false);
            if (ok?.Type == WireTypes.Reject)
            {
                var code = ParseCode(ok.ErrorCode, LanHubErrorCode.InvalidToken);
                throw new LanHubException(code, ok.Error ?? "SDK 握手失败");
            }
            if (ok?.Type != WireTypes.SdkOk)
                throw new LanHubException(LanHubErrorCode.HandshakeFailed, "SDK 握手失败");

            ApplySessionFrom(ok);
            var previous = _players;
            _players = ok.Players ?? [];
            EmitRosterDiff(previous, _players);
            RosterUpdated?.Invoke(_players);

            _lastPongUtc = DateTime.UtcNow;
            _reconnectAttempt = 0;
            SetState(LanHubConnectionState.Connected);
            Connected?.Invoke();

            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => ReadLoopAsync(_cts.Token));
            _heartbeat = Task.Run(() => HeartbeatLoopAsync(_cts.Token));
        }
        catch (LanHubException)
        {
            SetState(LanHubConnectionState.Failed);
            throw;
        }
        catch (Exception ex)
        {
            SetState(LanHubConnectionState.Failed);
            throw new LanHubException(LanHubErrorCode.HandshakeFailed, ex.Message);
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _stream != null)
            {
                var msg = await FrameCodec.ReadJsonAsync<WireEnvelope>(_stream, ct).ConfigureAwait(false);
                if (msg == null) break;
                HandleIncoming(msg);
            }

            await HandleDisconnectAsync(LanHubErrorCode.SessionEnded, "连接关闭").ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            await HandleDisconnectAsync(LanHubErrorCode.Unknown, ex.Message).ConfigureAwait(false);
        }
    }

    private void HandleIncoming(WireEnvelope msg)
    {
        switch (msg.Type)
        {
            case WireTypes.SdkPong:
                _lastPongUtc = DateTime.UtcNow;
                if (msg.Sequence != null) _lastPongSequence = msg.Sequence.Value;
                break;

            case WireTypes.SdkRoster:
            case WireTypes.SdkSession:
            {
                if (msg.Type == WireTypes.SdkSession || msg.HostPlayerId != null || msg.SessionId != null)
                    ApplySessionFrom(msg);
                if (msg.Players != null)
                {
                    var previous = _players;
                    _players = msg.Players;
                    EmitRosterDiff(previous, _players);
                    RosterUpdated?.Invoke(_players);
                }
                break;
            }

            case WireTypes.SdkError:
            {
                var code = ParseCode(msg.ErrorCode, LanHubErrorCode.Unknown);
                RaiseError(code, msg.Error ?? "SDK 错误");
                break;
            }

            case WireTypes.SdkBroadcast:
            case WireTypes.SdkRelay:
            {
                var payload = string.IsNullOrEmpty(msg.PayloadBase64)
                    ? Array.Empty<byte>()
                    : Convert.FromBase64String(msg.PayloadBase64);
                var channel = string.IsNullOrWhiteSpace(msg.Channel) ? LanHubChannel.Reliable : msg.Channel!;
                var reliable = msg.Reliable ?? !string.Equals(channel, LanHubChannel.Unreliable, StringComparison.OrdinalIgnoreCase);
                var m = new LanHubMessage
                {
                    FromPlayerId = msg.PlayerId ?? "",
                    TargetPlayerId = msg.TargetPlayerId,
                    Channel = channel,
                    Reliable = reliable,
                    Sequence = msg.Sequence ?? 0,
                    Payload = payload,
                    Transport = msg.Transport ?? (reliable ? "tcp" : "udp-p2p")
                };
                MessageReceived?.Invoke(m);
                MessageReceivedSimple?.Invoke(m.FromPlayerId, m.Payload);
                break;
            }
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HeartbeatInterval, ct).ConfigureAwait(false);
                if (State != LanHubConnectionState.Connected || _stream == null) continue;

                var seq = Interlocked.Increment(ref _sequence);
                await WriteEnvelopeAsync(new WireEnvelope
                {
                    Type = WireTypes.SdkPing,
                    Sequence = seq
                }, ct).ConfigureAwait(false);

                if (DateTime.UtcNow - _lastPongUtc > HeartbeatTimeout)
                {
                    await HandleDisconnectAsync(LanHubErrorCode.TimedOut, "心跳超时").ConfigureAwait(false);
                    return;
                }
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                // ignore transient heartbeat write errors; read loop will notice
            }
        }
    }

    private async Task HandleDisconnectAsync(LanHubErrorCode code, string reason)
    {
        if (_disposed) return;
        if (Interlocked.CompareExchange(ref _disconnectGate, 1, 0) != 0)
            return;

        try
        {
            if (State == LanHubConnectionState.Disconnected)
                return;

            Disconnected?.Invoke(code, reason);
            await TeardownSocketAsync().ConfigureAwait(false);

            if (!AutoReconnect || _disposed || _params == null)
            {
                SetState(LanHubConnectionState.Disconnected);
                return;
            }

            SetState(LanHubConnectionState.Reconnecting);
            RaiseError(LanHubErrorCode.Reconnecting, reason);

            while (!_disposed && AutoReconnect && _reconnectAttempt < MaxReconnectAttempts)
            {
                _reconnectAttempt++;
                var delay = TimeSpan.FromMilliseconds(Math.Min(8000, 400 * Math.Pow(2, _reconnectAttempt - 1)));
                try
                {
                    await Task.Delay(delay).ConfigureAwait(false);
                    await ConnectInternalAsync(CancellationToken.None, isReconnect: true).ConfigureAwait(false);
                    return;
                }
                catch
                {
                    // try again
                }
            }

            SetState(LanHubConnectionState.Failed);
            RaiseError(LanHubErrorCode.TimedOut, "重连失败");
        }
        finally
        {
            Interlocked.Exchange(ref _disconnectGate, 0);
        }
    }

            private void ApplySessionFrom(WireEnvelope msg)
    {
        Session = new LanHubSessionInfo
        {
            SessionId = msg.SessionId ?? Session.SessionId,
            GameId = msg.GameId ?? GameId,
            LocalPlayerId = PlayerId,
            HostPlayerId = msg.HostPlayerId ?? Session.HostPlayerId,
            MaxPayloadBytes = msg.MaxPayloadBytes ?? Session.MaxPayloadBytes,
            Transport = msg.Transport ?? Session.Transport,
            GameUdpPort = msg.GameUdpPort ?? Session.GameUdpPort,
            Peers = (msg.Peers ?? [])
                .Select(p => new LanHubPeer
                {
                    PlayerId = p.PlayerId,
                    IpAddress = p.IpAddress,
                    UdpPort = p.UdpPort
                })
                .ToArray()
        };
        SessionUpdated?.Invoke(Session);
    }

    private void EmitRosterDiff(List<PlayerInfo> previous, List<PlayerInfo> next)
    {
        var prevMap = previous.ToDictionary(p => p.PlayerId);
        var nextMap = next.ToDictionary(p => p.PlayerId);
        foreach (var p in next)
        {
            if (!prevMap.ContainsKey(p.PlayerId))
                PlayerJoined?.Invoke(p);
        }
        foreach (var p in previous)
        {
            if (!nextMap.ContainsKey(p.PlayerId))
                PlayerLeft?.Invoke(p);
        }
    }

    private async Task WriteEnvelopeAsync(WireEnvelope envelope, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var stream = _stream;
            if (stream == null)
                throw new LanHubException(LanHubErrorCode.NotConnected, "SDK 未连接");
            await FrameCodec.WriteJsonAsync(stream, envelope, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void EnsureConnected()
    {
        if (State != LanHubConnectionState.Connected || _stream == null)
            throw new LanHubException(LanHubErrorCode.NotConnected, "SDK 未连接");
    }

    private void SetState(LanHubConnectionState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(state);
    }

    private void RaiseError(LanHubErrorCode code, string message) => Error?.Invoke(code, message);

    private static LanHubErrorCode ParseCode(string? code, LanHubErrorCode fallback)
    {
        if (int.TryParse(code, out var n) && Enum.IsDefined(typeof(LanHubErrorCode), n))
            return (LanHubErrorCode)n;
        return fallback;
    }

    private async Task TeardownSocketAsync()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try
        {
            if (_loop != null) await Task.WhenAny(_loop, Task.Delay(200)).ConfigureAwait(false);
            if (_heartbeat != null) await Task.WhenAny(_heartbeat, Task.Delay(200)).ConfigureAwait(false);
        }
        catch { /* ignore */ }

        _stream?.Dispose();
        _tcp?.Close();
        _stream = null;
        _tcp = null;
        _cts?.Dispose();
        _cts = null;
        _loop = null;
        _heartbeat = null;
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        AutoReconnect = false;
        await TeardownSocketAsync().ConfigureAwait(false);
        SetState(LanHubConnectionState.Disconnected);
    }
}
