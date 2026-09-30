using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using LanHub.Core.Crypto;
using LanHub.Core.Discovery;
using LanHub.Core.Localization;
using LanHub.Core.Models;
using LanHub.Core.Net;
using LanHub.Core.Protocol;
using LanHub.Core.SdkRuntime;

namespace LanHub.Core.Room;

public sealed class RoomHostService : IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly ConcurrentDictionary<string, Member> _members = new();
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _joinAttempts = new();
    private readonly ConcurrentDictionary<string, PeerEndpoint> _peers = new();
    private readonly SemaphoreSlim _joinGate = new(1, 1);

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private UdpBeaconService? _beacon;
    private LocalSdkEndpoint? _sdk;
    private UdpGameTransport? _udp;
    private byte[]? _sessionKey;

    public string RoomCode { get; private set; } = "";
    public string SessionId { get; private set; } = "";
    public string HostPlayerId { get; } = Guid.NewGuid().ToString("N");
    public string? SelectedGameId { get; set; }
    public string? LaunchToken { get; private set; }
    public bool Started { get; private set; }
    public byte[] RoomSalt { get; private set; } = [];

    public event Action? RosterChanged;
    public event Action<string>? StatusChanged;

    public RoomHostService(AppSettings settings) => _settings = settings;

    public async Task StartAsync(string displayName)
    {
        if (_listener != null) return;

        RoomCode = SessionCrypto.CreateRoomCode();
        SessionId = Guid.NewGuid().ToString("N");
        LaunchToken = Guid.NewGuid().ToString("N");
        RoomSalt = SessionCrypto.CreateSalt();
        _sessionKey = SessionCrypto.DeriveSessionKey(RoomCode, RoomSalt);

        _members[HostPlayerId] = new Member
        {
            Info = new PlayerInfo
            {
                PlayerId = HostPlayerId,
                DisplayName = displayName,
                IsHost = true,
                IsReady = true
            }
        };

        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, _settings.TcpPort);
        _listener.Start();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));

        _beacon = new UdpBeaconService(_settings) { HostName = displayName, Joinable = true };
        _beacon.Start();

        _udp = new UdpGameTransport();
        _udp.Configure(HostPlayerId, HostPlayerId, _sessionKey);
        _udp.Start(_settings.GameUdpPort);
        _peers[HostPlayerId] = new PeerEndpoint
        {
            PlayerId = HostPlayerId,
            IpAddress = _udp.LocalIp,
            UdpPort = _udp.LocalPort
        };
        _udp.SetPeerTable(_peers.Values);
        _udp.PacketReceived += OnUdpPacketReceived;

        _sdk = new LocalSdkEndpoint(
            _settings.SdkPort,
            ValidateSdkToken,
            async outbound => await OnLocalGameSendAsync(outbound).ConfigureAwait(false));
        _sdk.SessionId = SessionId;
        _sdk.HostPlayerId = HostPlayerId;
        _sdk.SetRosterProvider(() => GetRoster().ToList());
        _sdk.SetSessionProvider(BuildSdkSessionEnvelope);
        await _sdk.StartAsync().ConfigureAwait(false);

        StatusChanged?.Invoke(Loc.Tf("host.core.opened", RoomCode, _udp.LocalIp, _udp.LocalPort));
        RosterChanged?.Invoke();
    }

    public IReadOnlyList<PlayerInfo> GetRoster() =>
        _members.Values.Select(m => m.Info).OrderByDescending(p => p.IsHost).ThenBy(p => p.DisplayName).ToList();

    public IReadOnlyList<PeerEndpoint> GetPeers() => _peers.Values.ToList();

    public async Task KickAsync(string playerId)
    {
        if (playerId == HostPlayerId) return;
        if (!_members.TryRemove(playerId, out var member)) return;
        _peers.TryRemove(playerId, out _);

        try
        {
            if (member.Stream != null && member.SessionKey != null)
                await FrameCodec.WriteEncryptedAsync(member.Stream, member.SessionKey, new WireEnvelope { Type = WireTypes.Kick }).ConfigureAwait(false);
        }
        catch { /* ignore */ }

        member.Cts?.Cancel();
        member.Tcp?.Close();
        RosterChanged?.Invoke();
        await BroadcastRosterAsync().ConfigureAwait(false);
        await BroadcastPeerTableAsync().ConfigureAwait(false);
    }

    public async Task PrepareGameSessionAsync(string gameId)
    {
        if (string.IsNullOrWhiteSpace(gameId))
            throw new ArgumentException("gameId 为空", nameof(gameId));

        SelectedGameId = gameId;
        LaunchToken ??= Guid.NewGuid().ToString("N");
        if (_sdk != null)
        {
            _sdk.GameId = gameId;
            _sdk.SessionId = SessionId;
            _sdk.HostPlayerId = HostPlayerId;
            await _sdk.PushSessionAsync().ConfigureAwait(false);
            await _sdk.PushRosterAsync().ConfigureAwait(false);
        }

        await BroadcastEncryptedAsync(new WireEnvelope
        {
            Type = WireTypes.GameBind,
            GameId = gameId,
            LaunchToken = LaunchToken,
            SessionId = SessionId,
            HostPlayerId = HostPlayerId,
            Players = GetRoster().ToList(),
            Peers = GetPeers().ToList(),
            Transport = "udp-p2p",
            GameUdpPort = _udp?.LocalPort
        }).ConfigureAwait(false);

        StatusChanged?.Invoke(Loc.Tf("host.core.bound", gameId));
    }

    public async Task StartGameAsync(string gameId)
    {
        await PrepareGameSessionAsync(gameId).ConfigureAwait(false);
        Started = true;
        if (_beacon != null) _beacon.Joinable = false;

        var msg = new WireEnvelope
        {
            Type = WireTypes.Start,
            GameId = gameId,
            LaunchToken = LaunchToken,
            SdkPort = _settings.SdkPort,
            Players = GetRoster().ToList(),
            SessionId = SessionId,
            HostPlayerId = HostPlayerId,
            Peers = GetPeers().ToList(),
            Transport = "udp-p2p"
        };

        await BroadcastEncryptedAsync(msg).ConfigureAwait(false);
        await BroadcastPeerTableAsync().ConfigureAwait(false);
        if (_sdk != null)
        {
            await _sdk.PushSessionAsync().ConfigureAwait(false);
            await _sdk.PushRosterAsync().ConfigureAwait(false);
        }
        StatusChanged?.Invoke(Loc.T("host.core.start_sent"));
    }

    public bool ValidateSdkToken(string token, string gameId) =>
        !string.IsNullOrEmpty(LaunchToken)
        && LaunchToken == token
        && (string.Equals(gameId, "probe", StringComparison.OrdinalIgnoreCase)
            || GameIdMatcher.EqualsLoose(SelectedGameId, gameId)
            // 兼容主机曾广播随机短码、游戏已用清单正确 GameId 连接的情况
            || GameIdMatcher.IsLikelyEphemeralGameId(SelectedGameId));

    public async Task DissolveAsync()
    {
        try
        {
            await BroadcastEncryptedAsync(new WireEnvelope { Type = WireTypes.Dissolve }).ConfigureAwait(false);
        }
        catch { /* ignore */ }
        await DisposeAsync().ConfigureAwait(false);
    }

    private void OnUdpPacketReceived(string from, string? to, byte[] payload, long seq)
    {
        _ = _sdk?.DeliverToLocalGameAsync(from, to, payload, "unreliable", reliable: false, sequence: seq, transport: "udp-p2p");
    }

    private async Task OnLocalGameSendAsync(SdkOutbound outbound)
    {
        await _sdk!.DeliverToLocalGameAsync(
            outbound.FromPlayerId,
            outbound.TargetPlayerId,
            outbound.Payload,
            outbound.Channel,
            outbound.Reliable,
            outbound.Sequence).ConfigureAwait(false);

        if (!outbound.Reliable)
        {
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
                // UDP 对端未就绪 / 超 MTU：走 TCP，避免客户机收不到快照而静止
                await BroadcastEncryptedAsync(new WireEnvelope
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

        await BroadcastEncryptedAsync(new WireEnvelope
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
    }

    private bool HasReachableUdpPeer(string? targetPlayerId)
    {
        if (_udp == null) return false;
        var peers = _udp.GetPeers();
        if (string.IsNullOrEmpty(targetPlayerId))
            return peers.Any(p => p.PlayerId != HostPlayerId);
        return peers.Any(p => p.PlayerId == targetPlayerId && p.PlayerId != HostPlayerId);
    }

    private WireEnvelope BuildSdkSessionEnvelope() => new()
    {
        Type = WireTypes.SdkSession,
        SessionId = SessionId,
        HostPlayerId = HostPlayerId,
        GameId = SelectedGameId,
        MaxPayloadBytes = SdkLimits.MaxPayloadBytes,
        Players = GetRoster().ToList(),
        Peers = GetPeers().ToList(),
        Transport = "udp-p2p",
        GameUdpPort = _udp?.LocalPort
    };

    private async Task BroadcastPeerTableAsync()
    {
        _udp?.SetPeerTable(_peers.Values);
        await BroadcastEncryptedAsync(new WireEnvelope
        {
            Type = WireTypes.PeerTable,
            Peers = GetPeers().ToList(),
            HostPlayerId = HostPlayerId,
            SessionId = SessionId
        }).ConfigureAwait(false);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                StatusChanged?.Invoke(Loc.Tf("host.core.accept_fail", ex.Message));
            }
        }
    }

    private async Task HandleClientAsync(TcpClient tcp, CancellationToken hostCt)
    {
        var remote = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(hostCt);
        var stream = tcp.GetStream();
        string? joinedId = null;

        try
        {
            if (!AllowJoinAttempt(remote))
            {
                await FrameCodec.WriteJsonAsync(stream, new WireEnvelope
                {
                    Type = WireTypes.Reject,
                    ErrorCode = "1010",
                    Error = Loc.T("host.reject.rate")
                }, linked.Token).ConfigureAwait(false);
                return;
            }

            if (Started)
            {
                await FrameCodec.WriteJsonAsync(stream, new WireEnvelope
                {
                    Type = WireTypes.Reject,
                    Error = Loc.T("host.reject.started")
                }, linked.Token).ConfigureAwait(false);
                return;
            }

            var salt = RoomSalt;
            var nonce = SessionCrypto.CreateSalt(16);
            await FrameCodec.WriteJsonAsync(stream, new WireEnvelope
            {
                Type = WireTypes.Challenge,
                SaltBase64 = SessionCrypto.ToBase64(salt),
                NonceBase64 = SessionCrypto.ToBase64(nonce)
            }, linked.Token).ConfigureAwait(false);

            var join = await FrameCodec.ReadJsonAsync<WireEnvelope>(stream, linked.Token).ConfigureAwait(false);
            if (join?.Type != WireTypes.Join || string.IsNullOrWhiteSpace(join.DisplayName) || string.IsNullOrWhiteSpace(join.ProofBase64))
            {
                await FrameCodec.WriteJsonAsync(stream, new WireEnvelope { Type = WireTypes.Reject, Error = Loc.T("host.reject.invalid") }, linked.Token).ConfigureAwait(false);
                return;
            }

            var expected = SessionCrypto.ComputeJoinProof(RoomCode, salt, nonce);
            var actual = SessionCrypto.FromBase64(join.ProofBase64);
            if (!SessionCrypto.FixedTimeEquals(expected, actual))
            {
                await FrameCodec.WriteJsonAsync(stream, new WireEnvelope { Type = WireTypes.Reject, Error = Loc.T("host.reject.code") }, linked.Token).ConfigureAwait(false);
                return;
            }

            byte[] sessionKey;
            Member member;
            await _joinGate.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                // 同 IP 重复加入：踢掉旧连接，避免一台设备占多个席位
                await EvictGuestsFromRemoteAsync(remote).ConfigureAwait(false);

                if (_members.Count >= _settings.MaxPlayers)
                {
                    await FrameCodec.WriteJsonAsync(stream, new WireEnvelope { Type = WireTypes.Reject, Error = Loc.T("host.reject.full") }, linked.Token).ConfigureAwait(false);
                    return;
                }

                var playerId = Guid.NewGuid().ToString("N");
                sessionKey = SessionCrypto.DeriveSessionKey(RoomCode, salt);
                member = new Member
                {
                    Tcp = tcp,
                    Stream = stream,
                    SessionKey = sessionKey,
                    Cts = linked,
                    Info = new PlayerInfo
                    {
                        PlayerId = playerId,
                        DisplayName = join.DisplayName.Trim(),
                        IsHost = false,
                        IsReady = false
                    }
                };
                _members[playerId] = member;
                joinedId = playerId;

                // Prefer remote address for peer table until client announces its preferred LAN IP
                _peers[playerId] = new PeerEndpoint
                {
                    PlayerId = playerId,
                    IpAddress = remote == "unknown" ? "127.0.0.1" : remote,
                    UdpPort = _settings.GameUdpPort
                };
                _udp?.SetPeerTable(_peers.Values);

                await FrameCodec.WriteEncryptedAsync(stream, sessionKey, new WireEnvelope
                {
                    Type = WireTypes.Welcome,
                    PlayerId = playerId,
                    Players = GetRoster().ToList(),
                    SdkPort = _settings.SdkPort,
                    HostPlayerId = HostPlayerId,
                    SessionId = SessionId,
                    Peers = GetPeers().ToList(),
                    GameUdpPort = _udp?.LocalPort,
                    GameId = SelectedGameId,
                    LaunchToken = LaunchToken
                }, linked.Token).ConfigureAwait(false);
            }
            finally
            {
                _joinGate.Release();
            }

            if (joinedId == null) return;

            RosterChanged?.Invoke();
            await BroadcastRosterAsync().ConfigureAwait(false);
            await BroadcastPeerTableAsync().ConfigureAwait(false);

            while (!linked.IsCancellationRequested)
            {
                var msg = await FrameCodec.ReadEncryptedAsync<WireEnvelope>(stream, sessionKey, linked.Token).ConfigureAwait(false);
                if (msg == null) break;
                await HandleMemberMessageAsync(member, msg).ConfigureAwait(false);
            }
        }
        catch
        {
            // disconnect
        }
        finally
        {
            if (joinedId != null && _members.TryRemove(joinedId, out var removed))
            {
                _peers.TryRemove(joinedId, out _);
                RosterChanged?.Invoke();
                try
                {
                    await BroadcastRosterAsync().ConfigureAwait(false);
                    await BroadcastPeerTableAsync().ConfigureAwait(false);
                }
                catch { /* ignore */ }
                removed.Tcp?.Close();
            }
            else
            {
                tcp.Close();
            }
        }
    }

    private async Task HandleMemberMessageAsync(Member member, WireEnvelope msg)
    {
        switch (msg.Type)
        {
            case WireTypes.Ready:
                member.Info.IsReady = msg.IsReady ?? true;
                RosterChanged?.Invoke();
                await BroadcastRosterAsync().ConfigureAwait(false);
                break;
            case WireTypes.Leave:
                member.Cts?.Cancel();
                break;
            case WireTypes.PeerAnnounce:
            {
                if (!string.IsNullOrWhiteSpace(msg.IpAddress) && msg.GameUdpPort is > 0)
                {
                    _peers[member.Info.PlayerId] = new PeerEndpoint
                    {
                        PlayerId = member.Info.PlayerId,
                        IpAddress = msg.IpAddress!,
                        UdpPort = msg.GameUdpPort.Value
                    };
                    await BroadcastPeerTableAsync().ConfigureAwait(false);
                }
                break;
            }
            case WireTypes.SdkBroadcast:
            case WireTypes.SdkRelay:
            {
                var payload = string.IsNullOrEmpty(msg.PayloadBase64)
                    ? Array.Empty<byte>()
                    : Convert.FromBase64String(msg.PayloadBase64);
                var from = msg.PlayerId ?? member.Info.PlayerId;
                var channel = string.IsNullOrWhiteSpace(msg.Channel) ? "reliable" : msg.Channel!;
                var reliable = msg.Reliable ?? !string.Equals(channel, "unreliable", StringComparison.OrdinalIgnoreCase);

                if (!reliable)
                {
                    await _sdk!.DeliverToLocalGameAsync(from, msg.TargetPlayerId, payload, channel, false, msg.Sequence ?? 0)
                        .ConfigureAwait(false);
                    if (_udp != null && HasReachableUdpPeer(msg.TargetPlayerId))
                        await _udp.SendUnreliableAsync(msg.TargetPlayerId, payload, msg.Sequence ?? 0).ConfigureAwait(false);
                    else
                        await BroadcastEncryptedAsync(new WireEnvelope
                        {
                            Type = msg.Type,
                            PlayerId = from,
                            TargetPlayerId = msg.TargetPlayerId,
                            PayloadBase64 = msg.PayloadBase64,
                            Channel = channel,
                            Reliable = false,
                            Sequence = msg.Sequence,
                            Transport = "tcp"
                        }, exceptPlayerId: member.Info.PlayerId).ConfigureAwait(false);
                    break;
                }

                await _sdk!.DeliverToLocalGameAsync(from, msg.TargetPlayerId, payload, channel, true, msg.Sequence ?? 0)
                    .ConfigureAwait(false);
                await BroadcastEncryptedAsync(new WireEnvelope
                {
                    Type = msg.Type,
                    PlayerId = from,
                    TargetPlayerId = msg.TargetPlayerId,
                    PayloadBase64 = msg.PayloadBase64,
                    Channel = channel,
                    Reliable = true,
                    Sequence = msg.Sequence,
                    Transport = "tcp"
                }, exceptPlayerId: member.Info.PlayerId).ConfigureAwait(false);
                break;
            }
            case WireTypes.Ping:
                if (member.Stream != null && member.SessionKey != null)
                    await FrameCodec.WriteEncryptedAsync(member.Stream, member.SessionKey, new WireEnvelope { Type = WireTypes.Pong }).ConfigureAwait(false);
                break;
        }
    }

    private async Task BroadcastRosterAsync()
    {
        await BroadcastEncryptedAsync(new WireEnvelope
        {
            Type = WireTypes.Roster,
            Players = GetRoster().ToList(),
            Joinable = !Started,
            SessionId = SessionId,
            HostPlayerId = HostPlayerId
        }).ConfigureAwait(false);
        if (_sdk != null)
            await _sdk.PushRosterAsync().ConfigureAwait(false);
    }

    private async Task BroadcastEncryptedAsync(WireEnvelope msg, string? exceptPlayerId = null)
    {
        foreach (var member in _members.Values.Where(m => !m.Info.IsHost && m.Stream != null && m.SessionKey != null))
        {
            if (exceptPlayerId != null && member.Info.PlayerId == exceptPlayerId) continue;
            try
            {
                await FrameCodec.WriteEncryptedAsync(member.Stream!, member.SessionKey!, msg).ConfigureAwait(false);
            }
            catch { /* ignore */ }
        }
    }

    private bool AllowJoinAttempt(string ip)
    {
        var now = DateTime.UtcNow;
        var q = _joinAttempts.GetOrAdd(ip, _ => new Queue<DateTime>());
        lock (q)
        {
            while (q.Count > 0 && (now - q.Peek()).TotalMinutes >= 1)
                q.Dequeue();
            if (q.Count >= ProtocolConstants.JoinRateLimitPerMinute)
                return false;
            q.Enqueue(now);
            return true;
        }
    }

    /// <summary>
    /// Drop previous guest seats from the same remote IP (double-click join / reconnect).
    /// </summary>
    private async Task EvictGuestsFromRemoteAsync(string remoteIp)
    {
        if (string.IsNullOrWhiteSpace(remoteIp) || remoteIp == "unknown") return;

        var staleIds = _members.Values
            .Where(m => !m.Info.IsHost)
            .Where(m =>
            {
                var ep = m.Tcp?.Client.RemoteEndPoint as IPEndPoint;
                return ep != null && string.Equals(ep.Address.ToString(), remoteIp, StringComparison.OrdinalIgnoreCase);
            })
            .Select(m => m.Info.PlayerId)
            .ToList();

        foreach (var id in staleIds)
            await KickAsync(id).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        if (_beacon != null) await _beacon.DisposeAsync().ConfigureAwait(false);
        if (_sdk != null) await _sdk.DisposeAsync().ConfigureAwait(false);
        if (_udp != null) await _udp.DisposeAsync().ConfigureAwait(false);
        _listener?.Stop();
        foreach (var m in _members.Values)
        {
            m.Cts?.Cancel();
            m.Tcp?.Close();
        }
        _members.Clear();
        _peers.Clear();
        _listener = null;
        _cts?.Dispose();
        _cts = null;
        Started = false;
        LaunchToken = null;
    }

    private sealed class Member
    {
        public PlayerInfo Info { get; set; } = new();
        public TcpClient? Tcp { get; set; }
        public NetworkStream? Stream { get; set; }
        public byte[]? SessionKey { get; set; }
        public CancellationTokenSource? Cts { get; set; }
    }
}
