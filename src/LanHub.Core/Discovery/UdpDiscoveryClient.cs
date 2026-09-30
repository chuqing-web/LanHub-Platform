using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LanHub.Core.Models;

namespace LanHub.Core.Discovery;

public sealed class UdpDiscoveryClient : IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly ConcurrentDictionary<string, DiscoveredHost> _hosts = new();
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _changedGate;

    public event Action? Changed;

    public UdpDiscoveryClient(AppSettings settings) => _settings = settings;

    public IReadOnlyCollection<DiscoveredHost> GetHosts()
    {
        var now = DateTime.UtcNow;
        var removed = false;
        foreach (var kv in _hosts)
        {
            if ((now - kv.Value.LastSeenUtc).TotalSeconds > 8)
            {
                if (_hosts.TryRemove(kv.Key, out _))
                    removed = true;
            }
        }

        if (removed)
            RaiseChanged();

        return _hosts.Values.OrderBy(h => h.HostName).ToArray();
    }

    public void Start()
    {
        if (_loop != null) return;
        _cts = new CancellationTokenSource();
        _udp = new UdpClient(_settings.UdpPort);
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
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _udp!.ReceiveAsync(ct).ConfigureAwait(false);
                TryParse(result.Buffer, result.RemoteEndPoint);
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                // keep listening
            }
        }
    }

    private void TryParse(byte[] buffer, IPEndPoint remote)
    {
        if (buffer.Length < 9) return;
        if (!buffer.AsSpan(0, 4).SequenceEqual(ProtocolConstants.UdpMagic)) return;
        if (buffer[4] != 1) return;

        var joinable = buffer[5] == 1;
        var tcpPort = (buffer[6] << 8) | buffer[7];
        var nameLen = buffer[8];
        if (buffer.Length < 9 + nameLen) return;
        var name = Encoding.UTF8.GetString(buffer, 9, nameLen);
        var ip = NormalizeIp(remote.Address);
        var key = $"{ip}:{tcpPort}";

        var added = false;
        var host = _hosts.AddOrUpdate(
            key,
            _ =>
            {
                added = true;
                return new DiscoveredHost
                {
                    HostName = name,
                    IpAddress = ip,
                    TcpPort = tcpPort,
                    Joinable = joinable,
                    LastSeenUtc = DateTime.UtcNow
                };
            },
            (_, existing) =>
            {
                existing.HostName = name;
                existing.Joinable = joinable;
                existing.LastSeenUtc = DateTime.UtcNow;
                existing.IpAddress = ip;
                existing.TcpPort = tcpPort;
                return existing;
            });

        // Only notify UI when a new host appears (not every beacon tick)
        if (added)
            RaiseChanged();
    }

    private void RaiseChanged()
    {
        // coalesce bursty notifications
        if (Interlocked.Exchange(ref _changedGate, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(200).ConfigureAwait(false);
                Changed?.Invoke();
            }
            finally
            {
                Interlocked.Exchange(ref _changedGate, 0);
            }
        });
    }

    public static string NormalizeIp(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            return address.MapToIPv4().ToString();
        return address.ToString();
    }
}
