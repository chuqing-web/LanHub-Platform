using System.Net;
using System.Net.Sockets;
using System.Text;
using LanHub.Core.Models;

namespace LanHub.Core.Discovery;

public sealed class UdpBeaconService : IAsyncDisposable
{
    private readonly AppSettings _settings;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public string HostName { get; set; } = Environment.UserName;
    public bool Joinable { get; set; } = true;

    public UdpBeaconService(AppSettings settings) => _settings = settings;

    public void Start()
    {
        if (_loop != null) return;
        _cts = new CancellationTokenSource();
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.EnableBroadcast = true;
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts == null) return;
        _cts.Cancel();
        try { if (_loop != null) await _loop.ConfigureAwait(false); } catch { /* ignore */ }
        _cts.Dispose();
        _udp?.Dispose();
        _cts = null;
        _loop = null;
        _udp = null;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var endpoint = new IPEndPoint(IPAddress.Broadcast, _settings.UdpPort);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var packet = BuildPacket();
                await _udp!.SendAsync(packet, packet.Length, endpoint).ConfigureAwait(false);
            }
            catch
            {
                // keep beaconing
            }

            try { await Task.Delay(ProtocolConstants.BeaconIntervalMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private byte[] BuildPacket()
    {
        // magic(4) | ver(1) | joinable(1) | tcpPort(u16) | nameLen(u8) | nameUtf8
        var nameBytes = Encoding.UTF8.GetBytes(HostName);
        if (nameBytes.Length > 64) nameBytes = nameBytes.AsSpan(0, 64).ToArray();

        var packet = new byte[4 + 1 + 1 + 2 + 1 + nameBytes.Length];
        Buffer.BlockCopy(ProtocolConstants.UdpMagic, 0, packet, 0, 4);
        packet[4] = 1;
        packet[5] = (byte)(Joinable ? 1 : 0);
        packet[6] = (byte)((_settings.TcpPort >> 8) & 0xFF);
        packet[7] = (byte)(_settings.TcpPort & 0xFF);
        packet[8] = (byte)nameBytes.Length;
        Buffer.BlockCopy(nameBytes, 0, packet, 9, nameBytes.Length);
        return packet;
    }
}
