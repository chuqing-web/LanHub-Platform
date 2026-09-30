using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using LanHub.Core.Crypto;
using LanHub.Core.Models;

namespace LanHub.Core.Net;

/// <summary>
/// Encrypted UDP transport between LanHub instances for unreliable game traffic.
/// Prefers P2P; falls back to host relay when the target endpoint is unknown.
/// </summary>
public sealed class UdpGameTransport : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, PeerEndpoint> _peers = new();
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private byte[]? _sessionKey;
    private string _localPlayerId = "";
    private string _hostPlayerId = "";

    public int LocalPort { get; private set; }
    public string LocalIp { get; private set; } = "127.0.0.1";

    public event Action<string /*from*/, string? /*to*/, byte[] /*payload*/, long /*seq*/>? PacketReceived;

    public void Configure(string localPlayerId, string hostPlayerId, byte[] sessionKey)
    {
        _localPlayerId = localPlayerId;
        _hostPlayerId = hostPlayerId;
        _sessionKey = sessionKey;
    }

    public void SetPeerTable(IEnumerable<PeerEndpoint> peers)
    {
        _peers.Clear();
        foreach (var p in peers)
        {
            if (string.IsNullOrWhiteSpace(p.PlayerId) || string.IsNullOrWhiteSpace(p.IpAddress) || p.UdpPort <= 0)
                continue;
            _peers[p.PlayerId] = p;
        }
    }

    public IReadOnlyCollection<PeerEndpoint> GetPeers() => _peers.Values.ToArray();

    public void Start(int preferredPort)
    {
        if (_udp != null) return;
        _udp = BindUdp(preferredPort, out var actual);
        LocalPort = actual;
        LocalIp = GetLocalIPv4() ?? "127.0.0.1";
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
    }

    public async Task SendUnreliableAsync(string? targetPlayerId, byte[] payload, long sequence)
    {
        if (_udp == null || _sessionKey == null || string.IsNullOrEmpty(_localPlayerId))
            return;
        if (payload.Length > ProtocolConstants.MaxUdpGamePayload)
            return; // drop oversized unreliable

        if (string.IsNullOrEmpty(targetPlayerId))
        {
            foreach (var peer in _peers.Values)
            {
                if (peer.PlayerId == _localPlayerId) continue;
                await SendOneAsync(peer, targetPlayerId: null, payload, sequence, relay: false).ConfigureAwait(false);
            }
            return;
        }

        if (_peers.TryGetValue(targetPlayerId, out var direct) && direct.PlayerId != _localPlayerId)
        {
            await SendOneAsync(direct, targetPlayerId, payload, sequence, relay: false).ConfigureAwait(false);
            return;
        }

        // Fallback: ask host to relay
        if (!string.IsNullOrEmpty(_hostPlayerId) &&
            _peers.TryGetValue(_hostPlayerId, out var host) &&
            _hostPlayerId != _localPlayerId)
        {
            await SendOneAsync(host, targetPlayerId, payload, sequence, relay: true).ConfigureAwait(false);
        }
    }

    /// <summary>Host-only: rebroadcast a relayed packet to the intended peer(s).</summary>
    public async Task HostRelayAsync(string fromPlayerId, string? targetPlayerId, byte[] payload, long sequence)
    {
        if (_udp == null || _sessionKey == null) return;

        if (string.IsNullOrEmpty(targetPlayerId))
        {
            foreach (var peer in _peers.Values)
            {
                if (peer.PlayerId == fromPlayerId || peer.PlayerId == _localPlayerId) continue;
                await SendOneAsync(peer, null, payload, sequence, relay: false, fromOverride: fromPlayerId)
                    .ConfigureAwait(false);
            }
            return;
        }

        if (_peers.TryGetValue(targetPlayerId, out var dest) && dest.PlayerId != _localPlayerId)
        {
            await SendOneAsync(dest, targetPlayerId, payload, sequence, relay: false, fromOverride: fromPlayerId)
                .ConfigureAwait(false);
        }
    }

    private async Task SendOneAsync(
        PeerEndpoint peer,
        string? targetPlayerId,
        byte[] payload,
        long sequence,
        bool relay,
        string? fromOverride = null)
    {
        try
        {
            var packet = BuildPacket(fromOverride ?? _localPlayerId, targetPlayerId, payload, sequence, relay);
            var sealedBytes = SessionCrypto.Seal(_sessionKey!, packet);
            // prefix magic + sealed
            var datagram = new byte[4 + sealedBytes.Length];
            Buffer.BlockCopy(ProtocolConstants.GameUdpMagic, 0, datagram, 0, 4);
            Buffer.BlockCopy(sealedBytes, 0, datagram, 4, sealedBytes.Length);
            await _udp!.SendAsync(datagram, datagram.Length, peer.IpAddress, peer.UdpPort).ConfigureAwait(false);
        }
        catch
        {
            // drop unreliable
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _udp!.ReceiveAsync(ct).ConfigureAwait(false);
                HandleDatagram(result.Buffer);
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                // keep listening
            }
        }
    }

    private void HandleDatagram(byte[] buffer)
    {
        if (_sessionKey == null) return;
        if (buffer.Length < 4 + 12 + 16) return;
        if (!buffer.AsSpan(0, 4).SequenceEqual(ProtocolConstants.GameUdpMagic)) return;

        byte[] plain;
        try { plain = SessionCrypto.Open(_sessionKey, buffer.AsSpan(4).ToArray()); }
        catch { return; }

        if (!TryParsePacket(plain, out var from, out var to, out var payload, out var seq, out var relay))
            return;

        // Host relay path
        if (relay && !string.IsNullOrEmpty(_hostPlayerId) && _localPlayerId == _hostPlayerId)
        {
            _ = HostRelayAsync(from, string.IsNullOrEmpty(to) ? null : to, payload, seq);
            // also deliver locally if host is a target of broadcast
            if (string.IsNullOrEmpty(to) || to == _localPlayerId)
                PacketReceived?.Invoke(from, string.IsNullOrEmpty(to) ? null : to, payload, seq);
            return;
        }

        if (!string.IsNullOrEmpty(to) && to != _localPlayerId)
            return;

        PacketReceived?.Invoke(from, string.IsNullOrEmpty(to) ? null : to, payload, seq);
    }

    private static byte[] BuildPacket(string fromId, string? toId, byte[] payload, long sequence, bool relay)
    {
        var from = ParsePlayerId(fromId);
        var to = string.IsNullOrEmpty(toId) ? new byte[16] : ParsePlayerId(toId);
        var packet = new byte[1 + 1 + 4 + 16 + 16 + payload.Length];
        packet[0] = 1; // ver
        packet[1] = (byte)(relay ? 1 : 0);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(2, 4), unchecked((uint)sequence));
        from.CopyTo(packet.AsSpan(6, 16));
        to.CopyTo(packet.AsSpan(22, 16));
        payload.CopyTo(packet.AsSpan(38));
        return packet;
    }

    private static bool TryParsePacket(
        byte[] plain,
        out string from,
        out string to,
        out byte[] payload,
        out long seq,
        out bool relay)
    {
        from = "";
        to = "";
        payload = Array.Empty<byte>();
        seq = 0;
        relay = false;
        if (plain.Length < 38) return false;
        if (plain[0] != 1) return false;
        relay = (plain[1] & 1) != 0;
        seq = BinaryPrimitives.ReadUInt32BigEndian(plain.AsSpan(2, 4));
        from = Convert.ToHexString(plain.AsSpan(6, 16)).ToLowerInvariant();
        var toBytes = plain.AsSpan(22, 16);
        to = toBytes.IndexOfAnyExcept((byte)0) < 0
            ? ""
            : Convert.ToHexString(toBytes).ToLowerInvariant();
        payload = plain.AsSpan(38).ToArray();
        return true;
    }

    private static byte[] ParsePlayerId(string playerId)
    {
        if (playerId.Length == 32)
        {
            try { return Convert.FromHexString(playerId); }
            catch { /* fall through */ }
        }

        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(playerId));
        return hash.AsSpan(0, 16).ToArray();
    }

    private static UdpClient BindUdp(int preferredPort, out int actualPort)
    {
        try
        {
            var c = new UdpClient(preferredPort);
            actualPort = preferredPort;
            return c;
        }
        catch
        {
            var c = new UdpClient(0);
            actualPort = ((IPEndPoint)c.Client.LocalEndPoint!).Port;
            return c;
        }
    }

    public static string? GetLocalIPv4()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback) continue;
                var props = ni.GetIPProperties();
                foreach (var addr in props.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(addr.Address))
                        return addr.Address.ToString();
                }
            }
        }
        catch { /* ignore */ }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { if (_loop != null) await _loop.ConfigureAwait(false); } catch { /* ignore */ }
        _udp?.Dispose();
        _cts?.Dispose();
        _udp = null;
        _cts = null;
        _loop = null;
    }
}
