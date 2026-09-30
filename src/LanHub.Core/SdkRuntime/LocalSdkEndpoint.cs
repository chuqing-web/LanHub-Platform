using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using LanHub.Core.Models;
using LanHub.Core.Protocol;

namespace LanHub.Core.SdkRuntime;

/// <summary>
/// Localhost SDK endpoint shared by host and client launchers.
/// </summary>
public sealed class LocalSdkEndpoint : IAsyncDisposable
{
    private readonly int _port;
    private readonly Func<string, string, bool> _validateToken;
    private readonly Func<SdkOutbound, Task> _onGameSend;
    private readonly ConcurrentDictionary<string, NetworkStream> _peers = new();
    private readonly ConcurrentDictionary<string, int> _unreliableQueued = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private Func<List<PlayerInfo>>? _roster;
    private Func<WireEnvelope>? _sessionFactory;

    public string? SessionId { get; set; }
    public string? HostPlayerId { get; set; }
    public string? GameId { get; set; }

    public LocalSdkEndpoint(
        int port,
        Func<string, string, bool> validateToken,
        Func<SdkOutbound, Task> onGameSend)
    {
        _port = port;
        _validateToken = validateToken;
        _onGameSend = onGameSend;
    }

    public void SetRosterProvider(Func<List<PlayerInfo>> roster) => _roster = roster;
    public void SetSessionProvider(Func<WireEnvelope> session) => _sessionFactory = session;

    public async Task StartAsync()
    {
        if (_listener != null) return;
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, _port);
        _listener.Start();
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        await Task.CompletedTask;
    }

    public async Task PushRosterAsync()
    {
        var players = _roster?.Invoke() ?? [];
        var envelope = new WireEnvelope
        {
            Type = WireTypes.SdkRoster,
            Players = players,
            SessionId = SessionId,
            HostPlayerId = HostPlayerId
        };
        await BroadcastToPeersAsync(envelope).ConfigureAwait(false);
    }

    public async Task PushSessionAsync()
    {
        var envelope = _sessionFactory?.Invoke() ?? new WireEnvelope
        {
            Type = WireTypes.SdkSession,
            SessionId = SessionId,
            HostPlayerId = HostPlayerId,
            GameId = GameId,
            MaxPayloadBytes = SdkLimits.MaxPayloadBytes,
            Players = _roster?.Invoke() ?? []
        };
        envelope.Type = WireTypes.SdkSession;
        await BroadcastToPeersAsync(envelope).ConfigureAwait(false);
    }

    public async Task DeliverToLocalGameAsync(
        string fromPlayerId,
        string? targetPlayerId,
        byte[] payload,
        string? channel = null,
        bool reliable = true,
        long sequence = 0,
        string? transport = null)
    {
        var ch = string.IsNullOrWhiteSpace(channel) ? "reliable" : channel!;
        var envelope = new WireEnvelope
        {
            Type = string.IsNullOrEmpty(targetPlayerId) ? WireTypes.SdkBroadcast : WireTypes.SdkRelay,
            PlayerId = fromPlayerId,
            TargetPlayerId = targetPlayerId,
            PayloadBase64 = Convert.ToBase64String(payload),
            Channel = ch,
            Reliable = reliable,
            Sequence = sequence,
            Transport = transport ?? (reliable ? "tcp" : "udp-p2p")
        };

        foreach (var (playerId, stream) in _peers)
        {
            var want =
                string.IsNullOrEmpty(targetPlayerId)
                    ? playerId != fromPlayerId
                    : playerId == targetPlayerId;

            if (!want) continue;

            if (!reliable && !TryAdmitUnreliable(playerId))
                continue;

            try { await FrameCodec.WriteJsonAsync(stream, envelope).ConfigureAwait(false); }
            catch { /* ignore */ }
            finally
            {
                if (!reliable) ReleaseUnreliable(playerId);
            }
        }
    }

    private bool TryAdmitUnreliable(string playerId)
    {
        var n = _unreliableQueued.AddOrUpdate(playerId, 1, (_, v) => v + 1);
        if (n <= SdkLimits.UnreliableQueueSoftLimit) return true;
        _unreliableQueued.AddOrUpdate(playerId, 0, (_, v) => Math.Max(0, v - 1));
        return false;
    }

    private void ReleaseUnreliable(string playerId) =>
        _unreliableQueued.AddOrUpdate(playerId, 0, (_, v) => Math.Max(0, v - 1));

    private async Task BroadcastToPeersAsync(WireEnvelope envelope)
    {
        foreach (var stream in _peers.Values)
        {
            try { await FrameCodec.WriteJsonAsync(stream, envelope).ConfigureAwait(false); }
            catch { /* ignore */ }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var tcp = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = Task.Run(() => HandleAsync(tcp, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch { /* ignore */ }
        }
    }

    private async Task HandleAsync(TcpClient tcp, CancellationToken ct)
    {
        using var client = tcp;
        var stream = client.GetStream();
        string? playerId = null;
        try
        {
            var hello = await FrameCodec.ReadJsonAsync<WireEnvelope>(stream, ct).ConfigureAwait(false);
            if (hello?.Type != WireTypes.SdkHello ||
                string.IsNullOrWhiteSpace(hello.LaunchToken) ||
                string.IsNullOrWhiteSpace(hello.GameId) ||
                string.IsNullOrWhiteSpace(hello.PlayerId))
            {
                await FrameCodec.WriteJsonAsync(stream, new WireEnvelope
                {
                    Type = WireTypes.Reject,
                    ErrorCode = "1002",
                    Error = "无效 SDK 握手"
                }, ct).ConfigureAwait(false);
                return;
            }

            if (!_validateToken(hello.LaunchToken, hello.GameId))
            {
                await FrameCodec.WriteJsonAsync(stream, new WireEnvelope
                {
                    Type = WireTypes.Reject,
                    ErrorCode = "1003",
                    Error = "会话令牌无效"
                }, ct).ConfigureAwait(false);
                return;
            }

            playerId = hello.PlayerId!;
            _peers[playerId] = stream;

            var players = _roster?.Invoke() ?? [];
            await FrameCodec.WriteJsonAsync(stream, new WireEnvelope
            {
                Type = WireTypes.SdkOk,
                Players = players,
                SessionId = SessionId,
                HostPlayerId = HostPlayerId,
                GameId = GameId ?? hello.GameId,
                MaxPayloadBytes = SdkLimits.MaxPayloadBytes,
                PlayerId = playerId
            }, ct).ConfigureAwait(false);

            while (!ct.IsCancellationRequested)
            {
                var msg = await FrameCodec.ReadJsonAsync<WireEnvelope>(stream, ct).ConfigureAwait(false);
                if (msg == null) break;

                if (msg.Type == WireTypes.SdkPing)
                {
                    await FrameCodec.WriteJsonAsync(stream, new WireEnvelope
                    {
                        Type = WireTypes.SdkPong,
                        Sequence = msg.Sequence
                    }, ct).ConfigureAwait(false);
                    continue;
                }

                if (msg.Type is not (WireTypes.SdkRelay or WireTypes.SdkBroadcast))
                    continue;

                var payload = string.IsNullOrEmpty(msg.PayloadBase64)
                    ? Array.Empty<byte>()
                    : Convert.FromBase64String(msg.PayloadBase64);

                if (payload.Length > SdkLimits.MaxPayloadBytes)
                {
                    await FrameCodec.WriteJsonAsync(stream, new WireEnvelope
                    {
                        Type = WireTypes.SdkError,
                        ErrorCode = "1004",
                        Error = $"消息超过上限 {SdkLimits.MaxPayloadBytes} 字节"
                    }, ct).ConfigureAwait(false);
                    continue;
                }

                var channel = string.IsNullOrWhiteSpace(msg.Channel) ? "reliable" : msg.Channel!;
                var reliable = msg.Reliable ?? !string.Equals(channel, "unreliable", StringComparison.OrdinalIgnoreCase);

                await _onGameSend(new SdkOutbound(
                    playerId,
                    msg.TargetPlayerId,
                    payload,
                    channel,
                    reliable,
                    msg.Sequence ?? 0)).ConfigureAwait(false);
            }
        }
        catch
        {
            // disconnect
        }
        finally
        {
            if (playerId != null)
                _peers.TryRemove(playerId, out var _removed);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        _listener?.Stop();
        _cts?.Dispose();
        _listener = null;
        await Task.CompletedTask;
    }
}
