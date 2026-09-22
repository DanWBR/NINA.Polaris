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
using NINA.INDI.Client;
using NINA.INDI.Devices;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// A filter change has to outlive the session.
///
/// INDI drivers keep FILTER_SLOT in <c>~/.indi/&lt;driver&gt;_config.xml</c>,
/// and <see cref="IndiClient"/> auto-dispatches CONFIG_LOAD on every CONNECT,
/// which re-applies that value and MOVES the wheel. Nothing used to write the
/// file after a filter change, so the slot that happened to be saved once
/// became the filter every session started on. Verified against
/// <c>indi_simulator_wheel</c>: with the config holding slot 4, setting slot 7
/// and then sending CONFIG_LOAD puts the wheel back on 4.
///
/// These tests drive the real <see cref="IndiFilterWheel"/> against a socket
/// that speaks just enough INDI to answer one property write, so the ack path
/// and the scheduling both run for real.
/// </summary>
[TestFixture]
public class IndiFilterSlotPersistTests {
    private const string Device = "Filter Simulator";

    [Test]
    public async Task ASlotChange_SchedulesTheDriverConfigSave() {
        using var server = new FakeIndiServer(IndiPropertyReply.Ok);
        using var client = new IndiClient("127.0.0.1", server.Port);
        // Long debounce: the assertion is that the save was SCHEDULED, and a
        // save that fired would try to write CONFIG_PROCESS to the fake server.
        client.ConfigSaveDebounce = TimeSpan.FromMinutes(5);
        await client.ConnectAsync();
        var wheel = new IndiFilterWheel(client, Device);

        await wheel.SetPositionAsync(4);

        Assert.That(client.HasPendingConfigSave(Device), Is.True,
            "a filter change must persist the driver's config, or CONFIG_LOAD "
            + "drags the wheel back to the old slot on the next connect");
    }

    [Test]
    public async Task ARejectedSlotChange_PersistsNothing() {
        using var server = new FakeIndiServer(IndiPropertyReply.Alert);
        using var client = new IndiClient("127.0.0.1", server.Port);
        client.ConfigSaveDebounce = TimeSpan.FromMinutes(5);
        await client.ConnectAsync();
        var wheel = new IndiFilterWheel(client, Device);

        Assert.ThrowsAsync<InvalidOperationException>(() => wheel.SetPositionAsync(4));
        Assert.That(client.HasPendingConfigSave(Device), Is.False,
            "a slot the wheel refused must not be written into its config");
    }

    [Test]
    public async Task RepeatedChanges_CoalesceIntoOneSave() {
        using var server = new FakeIndiServer(IndiPropertyReply.Ok);
        using var client = new IndiClient("127.0.0.1", server.Port);
        client.ConfigSaveDebounce = TimeSpan.FromMinutes(5);
        await client.ConnectAsync();
        var wheel = new IndiFilterWheel(client, Device);

        // A sequence that walks L, R, G, B queues four writes in a row; they
        // must not become four config writes.
        await wheel.SetPositionAsync(1);
        await wheel.SetPositionAsync(2);
        await wheel.SetPositionAsync(3);

        Assert.That(client.PendingConfigSaveCount, Is.EqualTo(1));
    }

    [Test]
    public void NoDeviceName_SchedulesNothing() {
        using var client = new IndiClient();
        client.ScheduleConfigSaveDebounced("");
        Assert.That(client.PendingConfigSaveCount, Is.EqualTo(0));
    }

    private enum IndiPropertyReply { Ok, Alert }

    /// <summary>The smallest thing that can answer a property write: accepts
    /// one client, swallows whatever it sends, and echoes a matching
    /// setNumberVector in the chosen state so the ack helper settles.</summary>
    private sealed class FakeIndiServer : IDisposable {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly IndiPropertyReply _reply;

        public int Port { get; }

        public FakeIndiServer(IndiPropertyReply reply) {
            _reply = reply;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(ServeAsync);
        }

        private async Task ServeAsync() {
            try {
                using var socket = await _listener.AcceptTcpClientAsync(_cts.Token);
                using var stream = socket.GetStream();
                var buf = new byte[4096];
                while (!_cts.IsCancellationRequested) {
                    int n = await stream.ReadAsync(buf, _cts.Token);
                    if (n <= 0) break;
                    var text = Encoding.UTF8.GetString(buf, 0, n);
                    if (!text.Contains("FILTER_SLOT")) continue;   // getProperties etc.
                    var state = _reply == IndiPropertyReply.Ok ? "Ok" : "Alert";
                    var message = _reply == IndiPropertyReply.Alert
                        ? " message='Filter wheel is not calibrated'" : "";
                    var xml =
                        $"<setNumberVector device='{Device}' name='FILTER_SLOT' state='{state}'{message}>"
                        + "<oneNumber name='FILTER_SLOT_VALUE'>4</oneNumber></setNumberVector>\n";
                    var bytes = Encoding.UTF8.GetBytes(xml);
                    await stream.WriteAsync(bytes, _cts.Token);
                    await stream.FlushAsync(_cts.Token);
                }
            } catch (OperationCanceledException) {
                // Disposed: expected.
            } catch (Exception) {
                // A torn-down socket at the end of a test is not a failure.
            }
        }

        public void Dispose() {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            _cts.Dispose();
        }
    }
}
