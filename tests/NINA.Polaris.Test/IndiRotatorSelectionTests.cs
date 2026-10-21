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

using System.Collections.Concurrent;
using System.Text;
using NUnit.Framework;
using NINA.INDI.Client;
using NINA.INDI.Devices;
using NINA.INDI.Protocol;

namespace NINA.Polaris.Test;

/// <summary>
/// Issue #48: the rotator slot could stay bound to "Telescope Simulator"
/// while the card showed "Rotator Simulator", so Move sent
/// ABS_ROTATOR_ANGLE to the mount, indiserver dropped it, and the UI still
/// said "moving to 37°".
/// </summary>
[TestFixture]
public class IndiRotatorSelectionTests {
    private const string Rotator = "Rotator Simulator";
    private const string Mount = "Telescope Simulator";

    [Test]
    public async Task Move_SendsTheAngleToTheSelectedRotator() {
        using var client = Client(out var sent);
        AddDevice(client, Rotator, IndiRotator.RotatorInterface, connected: true, withAngle: true);
        AddDevice(client, Mount, 0x0001, connected: true, withAngle: false);
        client.SendOverride = (data, _) => {
            sent.Add(Encoding.UTF8.GetString(data));
            Fire(client, Rotator, "ABS_ROTATOR_ANGLE", IndiPropertyState.Busy);
            return Task.CompletedTask;
        };

        using var rotator = new IndiRotator(client, Rotator);
        await rotator.MoveToAsync(37);

        Assert.That(sent, Has.Count.EqualTo(1));
        Assert.That(sent[0], Does.Contain("<newNumberVector device=\"Rotator Simulator\" name=\"ABS_ROTATOR_ANGLE\">"));
        Assert.That(sent[0], Does.Contain("<oneNumber name=\"ANGLE\">37</oneNumber>"));
    }

    [Test]
    public void Move_ToADeviceWithoutTheAngleProperty_IsRefusedUnsent() {
        using var client = Client(out var sent);
        AddDevice(client, Mount, 0x0001, connected: true, withAngle: false);

        using var bound = new IndiRotator(client, Mount);
        var ex = Assert.ThrowsAsync<InvalidOperationException>(() => bound.MoveToAsync(37));
        Assert.That(ex!.Message, Does.Contain("ABS_ROTATOR_ANGLE"));
        Assert.That(sent, Is.Empty);
    }

    [Test]
    public void Move_ToADisconnectedRotator_IsRefusedUnsent() {
        using var client = Client(out var sent);
        AddDevice(client, Rotator, IndiRotator.RotatorInterface, connected: false, withAngle: true);

        using var rotator = new IndiRotator(client, Rotator);
        Assert.That(rotator.IsConnected, Is.False, "the INDI link being up is not the rotator being connected");
        Assert.ThrowsAsync<InvalidOperationException>(() => rotator.MoveToAsync(37));
        Assert.That(sent, Is.Empty);
    }

    [Test]
    public void Move_ThatTheDriverNeverAcknowledges_Fails() {
        using var client = Client(out _);
        client.DefaultAckTimeout = TimeSpan.FromMilliseconds(100);
        AddDevice(client, Rotator, IndiRotator.RotatorInterface, connected: true, withAngle: true);

        using var rotator = new IndiRotator(client, Rotator);
        var ex = Assert.ThrowsAsync<InvalidOperationException>(() => rotator.MoveToAsync(37));
        Assert.That(ex!.Message, Does.Contain("did not acknowledge"));
    }

    [Test]
    public void Move_ThatTheDriverRejects_Fails() {
        using var client = Client(out _);
        AddDevice(client, Rotator, IndiRotator.RotatorInterface, connected: true, withAngle: true);
        client.SendOverride = (_, _) => {
            Fire(client, Rotator, "ABS_ROTATOR_ANGLE", IndiPropertyState.Alert, "limit reached");
            return Task.CompletedTask;
        };

        using var rotator = new IndiRotator(client, Rotator);
        var ex = Assert.ThrowsAsync<InvalidOperationException>(() => rotator.MoveToAsync(37));
        Assert.That(ex!.Message, Does.Contain("limit reached"));
    }

    [Test]
    public void OnlyRotatorsAndUntypedDevicesFitTheRotatorSlot() {
        using var client = Client(out _);
        AddDevice(client, Rotator, IndiRotator.RotatorInterface, connected: true, withAngle: true);
        AddDevice(client, Mount, 0x0001, connected: true, withAngle: false);
        AddDevice(client, "Focuser+Rotator", 0x0008 | IndiRotator.RotatorInterface, connected: true, withAngle: true);
        AddDevice(client, "Untyped", null, connected: true, withAngle: false);

        Assert.Multiple(() => {
            Assert.That(IndiRotator.NotARotator(client, Rotator), Is.Null);
            Assert.That(IndiRotator.NotARotator(client, "Focuser+Rotator"), Is.Null);
            Assert.That(IndiRotator.NotARotator(client, "Untyped"), Is.Null);
            Assert.That(IndiRotator.NotARotator(client, Mount), Does.Contain("not a rotator"));
        });
    }

    private static IndiClient Client(out List<string> sent) {
        var log = new List<string>();
        sent = log;
        return new IndiClient {
            SendOverride = (data, _) => { log.Add(Encoding.UTF8.GetString(data)); return Task.CompletedTask; }
        };
    }

    private static void AddDevice(IndiClient client, string device, int? driverInterface,
            bool connected, bool withAngle) {
        var props = new ConcurrentDictionary<string, IndiProperty>();
        props["CONNECTION"] = new IndiSwitchProperty {
            Device = device, Name = "CONNECTION", State = IndiPropertyState.Ok,
            Values = new Dictionary<string, bool> { ["CONNECT"] = connected, ["DISCONNECT"] = !connected }
        };
        if (driverInterface is int mask)
            props["DRIVER_INFO"] = new IndiTextProperty {
                Device = device, Name = "DRIVER_INFO", State = IndiPropertyState.Ok,
                Values = new Dictionary<string, string> { ["DRIVER_INTERFACE"] = mask.ToString() }
            };
        if (withAngle)
            props["ABS_ROTATOR_ANGLE"] = new IndiNumberProperty {
                Device = device, Name = "ABS_ROTATOR_ANGLE", State = IndiPropertyState.Ok,
                Values = new Dictionary<string, IndiNumberElement> { ["ANGLE"] = new() { Label = "Angle" } }
            };
        client.Devices[device] = props;
    }

    private static void Fire(IndiClient client, string device, string name,
            IndiPropertyState state, string? message = null) {
        var prop = new IndiNumberProperty { Device = device, Name = name, State = state, Message = message };
        var eventField = typeof(IndiClient).GetField("PropertyChanged",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        (eventField?.GetValue(client) as Action<string, IndiProperty>)?.Invoke(device, prop);
    }
}
