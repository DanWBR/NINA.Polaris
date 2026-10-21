// N.I.N.A. Polaris
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.

using System.Net;
using System.Net.Sockets;
using System.Text;
using NINA.INDI.Client;
using NINA.INDI.Devices;
using NINA.INDI.Protocol;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>Regression coverage for the Accessories rotator path. The UI used
/// to leave Telescope Simulator selected in the backend, report it connected,
/// and send ABS_ROTATOR_ANGLE to the telescope where INDI dropped it.</summary>
[TestFixture]
public class IndiRotatorMoveTests {
    private const string Rotator = "Rotator Simulator";

    [Test]
    public async Task IsConnected_TracksTheSelectedDeviceConnection() {
        using var server = new FakeIndiServer(replyToMove: false);
        using var client = new IndiClient("127.0.0.1", server.Port);
        await client.ConnectAsync();
        SeedConnection(client, "Telescope Simulator", connected: true);
        using var rotator = new IndiRotator(client, Rotator);

        Assert.That(rotator.IsConnected, Is.False,
            "another INDI device being connected must not turn the rotator card green");

        SeedConnection(client, Rotator, connected: true);
        Assert.That(rotator.IsConnected, Is.True);
    }

    [Test]
    public async Task MoveTo_WhenDeviceHasNoRotatorVector_FailsBeforeWriting() {
        using var server = new FakeIndiServer(replyToMove: false);
        using var client = new IndiClient("127.0.0.1", server.Port);
        await client.ConnectAsync();
        using var rotator = new IndiRotator(client, "Telescope Simulator");

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() => rotator.MoveToAsync(37));

        Assert.That(ex!.Message, Does.Contain("ABS_ROTATOR_ANGLE.ANGLE"));
        Assert.That(server.Received, Does.Not.Contain("ABS_ROTATOR_ANGLE"));
    }

    [Test]
    public async Task MoveTo_WritesRequestedAngleToSelectedRotator() {
        using var server = new FakeIndiServer(replyToMove: true);
        using var client = new IndiClient("127.0.0.1", server.Port);
        await client.ConnectAsync();
        SeedAngle(client, Rotator, 0);
        using var rotator = new IndiRotator(client, Rotator);

        await rotator.MoveToAsync(37);

        Assert.That(server.Received, Does.Contain(
            "<newNumberVector device=\"Rotator Simulator\" name=\"ABS_ROTATOR_ANGLE\">"));
        Assert.That(server.Received, Does.Contain("<oneNumber name=\"ANGLE\">37</oneNumber>"));
    }

    [Test]
    public async Task MoveTo_WhenDriverDoesNotAcknowledge_ReportsTimeout() {
        using var server = new FakeIndiServer(replyToMove: false);
        using var client = new IndiClient("127.0.0.1", server.Port) {
            DefaultAckTimeout = TimeSpan.FromMilliseconds(100)
        };
        await client.ConnectAsync();
        SeedAngle(client, Rotator, 0);
        using var rotator = new IndiRotator(client, Rotator);

        var ex = Assert.ThrowsAsync<TimeoutException>(() => rotator.MoveToAsync(47));

        Assert.That(ex!.Message, Does.Contain("did not acknowledge"));
    }

    private static void SeedConnection(IndiClient client, string device, bool connected) {
        var property = new IndiSwitchProperty {
            Device = device,
            Name = "CONNECTION",
            State = IndiPropertyState.Ok,
            Values = new Dictionary<string, bool> {
                ["CONNECT"] = connected,
                ["DISCONNECT"] = !connected
            }
        };
        SeedProperty(client, device, property);
    }

    private static void SeedAngle(IndiClient client, string device, double angle) {
        var property = new IndiNumberProperty {
            Device = device,
            Name = "ABS_ROTATOR_ANGLE",
            State = IndiPropertyState.Ok,
            Values = new Dictionary<string, IndiNumberElement> {
                ["ANGLE"] = new() { Value = angle, Min = 0, Max = 360, Step = 0.1 }
            }
        };
        SeedProperty(client, device, property);
    }

    private static void SeedProperty(IndiClient client, string device, IndiProperty property) {
        var properties = client.Devices.GetOrAdd(device,
            _ => new System.Collections.Concurrent.ConcurrentDictionary<string, IndiProperty>());
        properties[property.Name] = property;
    }

    private sealed class FakeIndiServer : IDisposable {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly bool _replyToMove;
        private readonly StringBuilder _received = new();
        private readonly object _sync = new();

        public int Port { get; }
        public string Received { get { lock (_sync) return _received.ToString(); } }

        public FakeIndiServer(bool replyToMove) {
            _replyToMove = replyToMove;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(ServeAsync);
        }

        private async Task ServeAsync() {
            try {
                using var socket = await _listener.AcceptTcpClientAsync(_cts.Token);
                using var stream = socket.GetStream();
                var buffer = new byte[4096];
                var replied = false;
                while (!_cts.IsCancellationRequested) {
                    var count = await stream.ReadAsync(buffer, _cts.Token);
                    if (count <= 0) break;
                    var chunk = Encoding.UTF8.GetString(buffer, 0, count);
                    string all;
                    lock (_sync) {
                        _received.Append(chunk);
                        all = _received.ToString();
                    }
                    if (!_replyToMove || replied ||
                        !all.Contains("name=\"ABS_ROTATOR_ANGLE\"", StringComparison.Ordinal) ||
                        !all.Contains("</newNumberVector>", StringComparison.Ordinal)) continue;

                    replied = true;
                    var xml =
                        $"<setNumberVector device='{Rotator}' name='ABS_ROTATOR_ANGLE' state='Busy'>" +
                        "<oneNumber name='ANGLE'>37</oneNumber></setNumberVector>\n";
                    var bytes = Encoding.UTF8.GetBytes(xml);
                    await stream.WriteAsync(bytes, _cts.Token);
                    await stream.FlushAsync(_cts.Token);
                }
            } catch (OperationCanceledException) {
                // Expected at test teardown.
            } catch (Exception) {
                // A socket can be torn down while the background loop exits.
            }
        }

        public void Dispose() {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            _cts.Dispose();
        }
    }
}
