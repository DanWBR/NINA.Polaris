using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NINA.Mount.SynScanWifi;

/// <summary>
/// Minimal request/response UDP transport for SynScan Wi-Fi mounts.
///
/// <para>
/// Wire format: each datagram is a single LX200 ASCII command (e.g.
/// <c>:GR#</c>) sent to the mount on <c>UDP/11880</c>. The mount
/// replies with one datagram per command for the read commands;
/// motion commands (start/stop jog, abort) are fire-and-forget — no
/// reply expected.
/// </para>
///
/// <para>
/// Thread-safety: an internal <see cref="SemaphoreSlim"/> serialises
/// requests so two concurrent callers don't interleave. The mount's
/// receive buffer can only correlate one in-flight request anyway.
/// </para>
///
/// <para>
/// Default mount endpoint when in AP mode is <c>192.168.4.1:11880</c>
/// (Sky-Watcher SynScan Wi-Fi factory default). On a home network
/// the mount picks up DHCP and exposes the same port at whatever the
/// router assigned it.
/// </para>
/// </summary>
public sealed class SynScanUdpClient : IDisposable {
    public const int DefaultPort = 11880;
    public const string DefaultHost = "192.168.4.1";

    private readonly IPEndPoint _endpoint;
    private readonly UdpClient _udp;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _timeout;
    private bool _disposed;

    public string Host { get; }
    public int Port { get; }

    public SynScanUdpClient(string host = DefaultHost, int port = DefaultPort,
                            TimeSpan? timeout = null) {
        Host = host;
        Port = port;
        _timeout = timeout ?? TimeSpan.FromSeconds(2);

        if (!IPAddress.TryParse(host, out var ip)) {
            // Allow user to point at "synscan.local" or similar; resolve
            // synchronously at construction so we fail fast rather than
            // on the first send.
            var entry = Dns.GetHostEntry(host);
            if (entry.AddressList.Length == 0)
                throw new InvalidOperationException($"Could not resolve '{host}' to an IP address.");
            ip = Array.Find(entry.AddressList, a => a.AddressFamily == AddressFamily.InterNetwork)
                 ?? entry.AddressList[0];
        }
        _endpoint = new IPEndPoint(ip, port);

        // Bind on any local interface, ephemeral port. Connect-style
        // UdpClient lets us SendAsync / ReceiveAsync without rebinding.
        _udp = new UdpClient(0, AddressFamily.InterNetwork);
        _udp.Client.ReceiveTimeout = (int)_timeout.TotalMilliseconds;
    }

    /// <summary>Send a command that doesn't expect a response (motion
    /// start, motion stop, abort).</summary>
    public async Task SendOneWayAsync(string command, CancellationToken ct = default) {
        await _gate.WaitAsync(ct);
        try {
            await _udp.SendAsync(Encoding.ASCII.GetBytes(command), _endpoint, ct);
        } finally {
            _gate.Release();
        }
    }

    /// <summary>Send a command and read back the reply. Returns the
    /// ASCII payload (terminating <c>#</c> kept so the codec can
    /// distinguish empty replies from missing ones). Throws on
    /// timeout — caller decides whether that's fatal.</summary>
    public async Task<string> SendQueryAsync(string command, CancellationToken ct = default) {
        await _gate.WaitAsync(ct);
        try {
            await _udp.SendAsync(Encoding.ASCII.GetBytes(command), _endpoint, ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);
            var result = await _udp.ReceiveAsync(cts.Token);
            return Encoding.ASCII.GetString(result.Buffer);
        } finally {
            _gate.Release();
        }
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        try { _udp.Dispose(); } catch { /* best effort */ }
        _gate.Dispose();
    }
}
