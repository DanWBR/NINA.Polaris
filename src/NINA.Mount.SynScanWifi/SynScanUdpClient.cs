// N.I.N.A. Polaris
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT
// ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or
// FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License
// for more details. You should have received a copy of the license along with
// this program. If not, see <https://www.gnu.org/licenses/>.

using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NINA.Mount.SynScanWifi;

/// <summary>
/// Request/response UDP transport for SynScan Wi-Fi mounts.
///
/// <para>
/// Wire format: each datagram is one Sky-Watcher motor-controller command
/// (see <see cref="SkyWatcherMotorCodec"/>) sent to <c>UDP/11880</c>, and
/// the mount answers every command, motion commands included, with one
/// datagram.
/// </para>
///
/// <para>
/// Thread-safety: an internal <see cref="SemaphoreSlim"/> serialises
/// requests so two concurrent callers don't interleave. Replies carry no
/// request id, so anything already waiting in the socket is discarded
/// before a send: a reply that arrived after its query timed out must not
/// be read as the answer to the next one.
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
        _timeout = timeout ?? TimeSpan.FromSeconds(1);

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

    /// <summary>Send a command and read back the reply, as ASCII with the
    /// trailing carriage return kept. Throws <see cref="TimeoutException"/>
    /// when nothing comes back in time.</summary>
    public async Task<string> SendQueryAsync(string command, CancellationToken ct = default) {
        await _gate.WaitAsync(ct);
        try {
            while (_udp.Available > 0) {
                IPEndPoint? any = null;
                _udp.Receive(ref any);
            }
            await _udp.SendAsync(Encoding.ASCII.GetBytes(command), _endpoint, ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);
            try {
                var result = await _udp.ReceiveAsync(cts.Token);
                return Encoding.ASCII.GetString(result.Buffer);
            } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
                throw new TimeoutException($"No reply from {Host}:{Port} to {command.TrimEnd('\r')}");
            }
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