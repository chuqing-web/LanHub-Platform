using System.Net;
using System.Net.Sockets;
using LanHub.Core.Crypto;
using LanHub.Core.Localization;
using LanHub.Core.Models;
using LanHub.Core.Net;
using LanHub.Core.Protocol;
using LanHub.Core.SdkRuntime;

namespace LanHub.Core.Room;

public sealed class RoomClientService : IAsyncDisposable
{
    private readonly AppSettings _settings;
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private byte[]? _sessionKey;
    private CancellationTokenSource? _cts;
    private Task? _readLoop;
    private LocalSdkEndpoint? _sdk;
    private UdpGameTransport? _udp;
    private List<PlayerInfo> _roster = [];
    private List<PeerEndpoint> _peers = [];
    private string _hostPlayerId = "";

    public string? PlayerId { get; private set; }
    public string? LaunchToken { get; private set; }
    public string? SelectedGameId { get; private set; }
    public string? SessionId { get; private set; }
    public int SdkPort { get; private set; }
    public bool IsReady { get; private set; }

    public event Action? RosterChanged;
    public event Action<string>? StatusChanged;
    public event Action<string, string>? StartReceived;
    public event Action? Kicked;
    public event Action? Dissolved;
    public event Action? GameBound;

    public RoomClientService(AppSettings settings) => _settings = settings;

    public IReadOnlyList<PlayerInfo> GetRoster() => _roster;
    public IReadOnlyList<PeerEndpoint> GetPeers() => _peers;

    public async Task JoinAsync(string hostIp, int tcpPort, string displayName, string roomCode, CancellationToken ct = default)
    {
        await DisposeAsync().ConfigureAwait(false);

        // 始终使用本机 SDK 端口；主机 Welcome 里的 SdkPort 不能用于本机监听
        SdkPort = _settings.SdkPort;

        _tcp = new TcpClient();
        await _tcp.ConnectAsync(hostIp, tcpPort, ct).ConfigureAwait(false);
        _stream = _tcp.GetStream();

        var challenge = await FrameCodec.ReadJsonAsync<WireEnvelope>(_stream, ct).ConfigureAwait(false);
        if (challenge?.Type == WireTypes.Reject)
            throw new InvalidOperationException(challenge.Error ?? "被拒绝");
        if (challenge?.Type != WireTypes.Challenge ||
            string.IsNullOrEmpty(challenge.SaltBase64) ||
            string.IsNullOrEmpty(challenge.NonceBase64))
            throw new InvalidOperationException("无效的主机握手");

        var salt = SessionCrypto.FromBase64(challenge.SaltBase64);
        var nonce = SessionCrypto.FromBase64(challenge.NonceBase64);
        var proof = SessionCrypto.ComputeJoinProof(roomCode, salt, nonce);
        _sessionKey = SessionCrypto.DeriveSessionKey(roomCode, salt);

        await FrameCodec.WriteJsonAsync(_stream, new WireEnvelope
        {
            Type = WireTypes.Join,
            DisplayName = displayName,
            ProofBase64 = SessionCrypto.ToBase64(proof)
        }, ct).ConfigureAwait(false);

        var welcome = await ReadWelcomeOrRejectAsync(_stream, _sessionKey, ct).ConfigureAwait(false);
        PlayerId = welcome.PlayerId;
        _roster = welcome.Players ?? [];
        SessionId = welcome.SessionId;
        _hostPlayerId = welcome.HostPlayerId ?? "";
        _peers = welcome.Peers ?? [];
        SelectedGameId = welcome.GameId;
        LaunchToken = welcome.LaunchToken;

        await StartUdpAndAnnounceAsync(ct).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(LaunchToken) && !string.IsNullOrEmpty(PlayerId))
            await EnsureSdkAsync(SessionId, _hostPlayerId).ConfigureAwait(false);

        RosterChanged?.Invoke();
        if (!string.IsNullOrEmpty(SelectedGameId))
            StatusChanged?.Invoke(Loc.Tf("client.core.joined_bound", SelectedGameId));
        else
            StatusChanged?.Invoke(Loc.Tf("client.core.joined", _udp?.LocalIp, _udp?.LocalPort));

        _cts = new CancellationTokenSource();
        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));
    }

    public async Task SetReadyAsync(bool ready)
    {
        IsReady = ready;
        var self = _roster.FirstOrDefault(p => p.PlayerId == PlayerId);
        if (self != null)
            self.IsReady = ready;
        RosterChanged?.Invoke();
        await SendAsync(new WireEnvelope { Type = WireTypes.Ready, IsReady = ready }).ConfigureAwait(false);
    }

    public async Task LeaveAsync()
    {
        try { await SendAsync(new WireEnvelope { Type = WireTypes.Leave }).ConfigureAwait(false); }
        catch { /* ignore */ }
        await DisposeAsync().ConfigureAwait(false);
    }

    private async Task StartUdpAndAnnounceAsync(CancellationToken ct)
    {
        if (_sessionKey == null || string.IsNullOrEmpty(PlayerId)) return;

        _udp = new UdpGameTransport();
        _udp.Configure(PlayerId, _hostPlayerId, _sessionKey);
        _udp.Start(_settings.GameUdpPort);
        _udp.SetPeerTable(_peers);
        _udp.PacketReceived += OnUdpPacketReceived;

        var localIp = (_tcp?.Client.LocalEndPoint as IPEndPoint)?.Address.ToString()
                      ?? _udp.LocalIp;

        await SendAsync(new WireEnvelope
        {
            Type = WireTypes.PeerAnnounce,
            PlayerId = PlayerId,
            IpAddress = localIp,
            GameUdpPort = _udp.LocalPort
        }).ConfigureAwait(false);
    }

    private void OnUdpPacketReceived(string from, string? to, byte[] payload, long seq)
    {
        _ = _sdk?.DeliverToLocalGameAsync(from, to, payload, "unreliable", reliable: false, sequence: seq, transport: "udp-p2p");
    }

    private async Task SendAsync(WireEnvelope msg)
    {
        if (_stream == null || _sessionKey == null) throw new InvalidOperationException("未连接");
        await FrameCodec.WriteEncryptedAsync(_stream, _sessionKey, msg).ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _stream != null && _sessionKey != null)
            {
                var msg = await FrameCodec.ReadEncryptedAsync<WireEnvelope>(_stream, _sessionKey, ct).ConfigureAwait(false);
                if (msg == null) break;
                await HandleAsync(msg).ConfigureAwait(false);
            }
        }
        catch
        {
            StatusChanged?.Invoke(Loc.T("client.core.disconnected"));
        }
    }

    private async Task HandleAsync(WireEnvelope msg)
    {
        switch (msg.Type)
        {
            case WireTypes.Roster:
                _roster = msg.Players ?? [];
                RosterChanged?.Invoke();
                if (_sdk != null)
                {
                    if (!string.IsNullOrEmpty(msg.SessionId)) _sdk.SessionId = msg.SessionId;
                    if (!string.IsNullOrEmpty(msg.HostPlayerId)) _sdk.HostPlayerId = msg.HostPlayerId;
                    await _sdk.PushRosterAsync().ConfigureAwait(false);
                }
                break;
            case WireTypes.PeerTable:
                _peers = msg.Peers ?? [];
                if (!string.IsNullOrEmpty(msg.HostPlayerId)) _hostPlayerId = msg.HostPlayerId;
                _udp?.Configure(PlayerId ?? "", _hostPlayerId, _sessionKey!);
                _udp?.SetPeerTable(_peers);
                if (_sdk != null)
                    await _sdk.PushSessionAsync().ConfigureAwait(false);
                break;
            case WireTypes.GameBind:
            case WireTypes.Start:
                await ApplyGameBindAsync(msg).ConfigureAwait(false);
                if (msg.Type == WireTypes.Start
                    && !string.IsNullOrEmpty(SelectedGameId)
                    && !string.IsNullOrEmpty(LaunchToken))
                {
                    StartReceived?.Invoke(SelectedGameId, LaunchToken);
                }
                break;
            case WireTypes.Kick:
                Kicked?.Invoke();
                await DisposeAsync().ConfigureAwait(false);
                break;
            case WireTypes.Dissolve:
                Dissolved?.Invoke();
                await DisposeAsync().ConfigureAwait(false);
                break;
            case WireTypes.SdkBroadcast:
            case WireTypes.SdkRelay:
            {
                if (_sdk == null) await EnsureSdkAsync().ConfigureAwait(false);
                var payload = string.IsNullOrEmpty(msg.PayloadBase64)
                    ? Array.Empty<byte>()
                    : Convert.FromBase64String(msg.PayloadBase64);
                var channel = string.IsNullOrWhiteSpace(msg.Channel) ? "reliable" : msg.Channel!;
                var reliable = msg.Reliable ?? !string.Equals(channel, "unreliable", StringComparison.OrdinalIgnoreCase);
                await _sdk!.DeliverToLocalGameAsync(
                    msg.PlayerId ?? "",
                    msg.TargetPlayerId,
                    payload,
                    channel,
                    reliable,
                    msg.Sequence ?? 0,
                    msg.Transport).ConfigureAwait(false);
                break;
            }
        }
    }

    private async Task ApplyGameBindAsync(WireEnvelope msg)
    {
        if (!string.IsNullOrEmpty(msg.GameId))
            SelectedGameId = msg.GameId;
        if (!string.IsNullOrEmpty(msg.LaunchToken))
            LaunchToken = msg.LaunchToken;
        _roster = msg.Players ?? _roster;
        if (msg.Peers != null) _peers = msg.Peers;
        if (!string.IsNullOrEmpty(msg.HostPlayerId)) _hostPlayerId = msg.HostPlayerId;
        SessionId = msg.SessionId ?? SessionId;
        _udp?.SetPeerTable(_peers);

        await EnsureSdkAsync(msg.SessionId, msg.HostPlayerId).ConfigureAwait(false);
        if (_sdk != null)
        {
            _sdk.GameId = SelectedGameId;
            _sdk.SessionId = SessionId;
            _sdk.HostPlayerId = _hostPlayerId;
            await _sdk.PushSessionAsync().ConfigureAwait(false);
            await _sdk.PushRosterAsync().ConfigureAwait(false);
        }

        StatusChanged?.Invoke(string.IsNullOrEmpty(SelectedGameId)
            ? Loc.T("client.core.session_updated")
            : Loc.Tf("client.core.host_bound", SelectedGameId));
        GameBound?.Invoke();
    }

    private async Task EnsureSdkAsync(string? sessionId = null, string? hostPlayerId = null)
    {
        if (string.IsNullOrEmpty(LaunchToken) || string.IsNullOrEmpty(PlayerId))
            return;

        if (_sdk != null)
        {
            if (!string.IsNullOrEmpty(sessionId)) _sdk.SessionId = sessionId;
            if (!string.IsNullOrEmpty(hostPlayerId)) _sdk.HostPlayerId = hostPlayerId;
            if (!string.IsNullOrEmpty(SelectedGameId)) _sdk.GameId = SelectedGameId;
            return;
        }

        _sdk = new LocalSdkEndpoint(
            SdkPort,
            (t, g) =>
                !string.IsNullOrEmpty(LaunchToken)
                && t == LaunchToken
                && (string.Equals(g, "probe", StringComparison.OrdinalIgnoreCase)
                    || GameIdMatcher.EqualsLoose(SelectedGameId, g)
                    || GameIdMatcher.IsLikelyEphemeralGameId(SelectedGameId)),
            async outbound =>
            {
                if (!outbound.Reliable)
                {
                    await _sdk!.DeliverToLocalGameAsync(
                        outbound.FromPlayerId,
                        outbound.TargetPlayerId,
                        outbound.Payload,
                        outbound.Channel,
                        false,
                        outbound.Sequence).ConfigureAwait(false);

                    var udpOk = _udp != null
                                && HasReachableUdpPeer(outbound.TargetPlayerId)
                                && outbound.Payload.Length <= ProtocolConstants.MaxUdpGamePayload;
                    if (udpOk)
                    {
                        await _udp!.SendUnreliableAsync(outbound.TargetPlayerId, outbound.Payload, outbound.Sequence)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await SendAsync(new WireEnvelope
                        {
                            Type = string.IsNullOrEmpty(outbound.TargetPlayerId) ? WireTypes.SdkBroadcast : WireTypes.SdkRelay,
                            PlayerId = outbound.FromPlayerId,
                            TargetPlayerId = outbound.TargetPlayerId,
                            PayloadBase64 = Convert.ToBase64String(outbound.Payload),
                            Channel = outbound.Channel,
                            Reliable = false,
                            Sequence = outbound.Sequence,
                            Transport = "tcp"
                        }).ConfigureAwait(false);
                    }
                    return;
                }

                await SendAsync(new WireEnvelope
                {
                    Type = string.IsNullOrEmpty(outbound.TargetPlayerId) ? WireTypes.SdkBroadcast : WireTypes.SdkRelay,
                    PlayerId = outbound.FromPlayerId,
                    TargetPlayerId = outbound.TargetPlayerId,
                    PayloadBase64 = Convert.ToBase64String(outbound.Payload),
                    Channel = outbound.Channel,
                    Reliable = true,
                    Sequence = outbound.Sequence,
                    Transport = "tcp"
                }).ConfigureAwait(false);
            });
        _sdk.SessionId = sessionId ?? SessionId;
        _sdk.HostPlayerId = hostPlayerId ?? _hostPlayerId;
        _sdk.GameId = SelectedGameId;
        _sdk.SetRosterProvider(() => _roster.ToList());
        _sdk.SetSessionProvider(() => new WireEnvelope
        {
            Type = WireTypes.SdkSession,
            SessionId = _sdk.SessionId,
            HostPlayerId = _sdk.HostPlayerId,
            GameId = SelectedGameId,
            MaxPayloadBytes = SdkLimits.MaxPayloadBytes,
            Players = _roster.ToList(),
            Peers = _peers.ToList(),
            Transport = "udp-p2p",
            GameUdpPort = _udp?.LocalPort
        });
        await _sdk.StartAsync().ConfigureAwait(false);
        await _sdk.PushSessionAsync().ConfigureAwait(false);
        await _sdk.PushRosterAsync().ConfigureAwait(false);
    }

    private bool HasReachableUdpPeer(string? targetPlayerId)
    {
        if (_udp == null || string.IsNullOrEmpty(PlayerId)) return false;
        var peers = _udp.GetPeers();
        if (string.IsNullOrEmpty(targetPlayerId))
            return peers.Any(p => p.PlayerId != PlayerId);
        return peers.Any(p => p.PlayerId == targetPlayerId && p.PlayerId != PlayerId);
    }

    private static async Task<WireEnvelope> ReadWelcomeOrRejectAsync(NetworkStream stream, byte[] sessionKey, CancellationToken ct)
    {
        var lenBuf = new byte[4];
        await ReadExactAsync(stream, lenBuf, ct).ConfigureAwait(false);
        var len = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(lenBuf);
        var payload = new byte[len];
        await ReadExactAsync(stream, payload, ct).ConfigureAwait(false);

        try
        {
            var maybe = FrameCodec.FromJson<WireEnvelope>(payload);
            if (maybe?.Type == WireTypes.Reject)
                throw new InvalidOperationException(maybe.Error ?? "加入被拒绝");
        }
        catch (InvalidOperationException) { throw; }
        catch { /* not plain json */ }

        var plain = SessionCrypto.Open(sessionKey, payload);
        var welcome = FrameCodec.FromJson<WireEnvelope>(plain)
                      ?? throw new InvalidOperationException("欢迎包无效");
        if (welcome.Type != WireTypes.Welcome)
            throw new InvalidOperationException(welcome.Error ?? "加入失败");
        return welcome;
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        if (_sdk != null) await _sdk.DisposeAsync().ConfigureAwait(false);
        if (_udp != null) await _udp.DisposeAsync().ConfigureAwait(false);
        _sdk = null;
        _udp = null;
        _stream?.Dispose();
        _tcp?.Close();
        _stream = null;
        _tcp = null;
        _cts?.Dispose();
        _cts = null;
        PlayerId = null;
        LaunchToken = null;
        SelectedGameId = null;
        SessionId = null;
        IsReady = false;
        _hostPlayerId = "";
        _roster = [];
        _peers = [];
    }
}
