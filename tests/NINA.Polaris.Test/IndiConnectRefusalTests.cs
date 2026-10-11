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

using NUnit.Framework;
using NINA.INDI.Client;
using NINA.INDI.Protocol;

namespace NINA.Polaris.Test;

/// <summary>
/// A driver that refuses a CONNECT used to be invisible: the write was
/// fire-and-forget, so /api/camera/select answered 200 while the camera
/// never came up, and the driver's reason ("Camera open error (-53): Could
/// not claim the USB device", from a Canon EOS 1500D on a Pi image) only
/// ever reached the log. These cover the outcome wait and the text the
/// operator gets.
/// </summary>
[TestFixture]
public class IndiConnectRefusalTests {

    // ----- the outcome wait -----

    /// <summary>The reason the ack helper cannot be reused here: a driver
    /// about to fail answers Busy first and Alert only after it has tried
    /// the hardware. Settling on the ack would call every refusal a
    /// success.</summary>
    [Test]
    public async Task BusyThenAlert_IsARefusal_NotAnAck() {
        using var client = new IndiClient();
        var wait = client.AwaitConnectOutcomeAsync("Canon DSLR EOS 1500D",
            send: () => Task.CompletedTask,
            timeout: TimeSpan.FromSeconds(3), ct: default);

        await Task.Delay(30);
        FireConnection(client, "Canon DSLR EOS 1500D", IndiPropertyState.Busy, connect: false, message: null);
        await Task.Delay(30);
        FireConnection(client, "Canon DSLR EOS 1500D", IndiPropertyState.Alert, connect: false,
            message: "Failed to connect");

        var outcome = await wait;
        Assert.That(outcome.Refused, Is.True);
        Assert.That(outcome.Connected, Is.False);
        Assert.That(outcome.AlertMessage, Is.EqualTo("Failed to connect"));
    }

    [Test]
    public async Task ConnectTrue_IsSuccess() {
        using var client = new IndiClient();
        var wait = client.AwaitConnectOutcomeAsync("ZWO CCD ASI585MC Pro",
            send: () => Task.CompletedTask,
            timeout: TimeSpan.FromSeconds(3), ct: default);

        await Task.Delay(30);
        FireConnection(client, "ZWO CCD ASI585MC Pro", IndiPropertyState.Ok, connect: true, message: null);

        var outcome = await wait;
        Assert.That(outcome.Connected, Is.True);
        Assert.That(outcome.Refused, Is.False);
    }

    /// <summary>Silence is not failure. A shared driver serving two roles
    /// can stay quiet on a CONNECT that nonetheless worked, so the caller
    /// carries on and polls IsConnected as it always did.</summary>
    [Test]
    public async Task NoAnswer_IsNeitherConnectedNorRefused() {
        using var client = new IndiClient();
        var outcome = await client.AwaitConnectOutcomeAsync("Quiet Driver",
            send: () => Task.CompletedTask,
            timeout: TimeSpan.FromMilliseconds(200), ct: default);
        Assert.That(outcome.Connected, Is.False);
        Assert.That(outcome.Refused, Is.False);
        Assert.That(outcome.AlertMessage, Is.Null);
    }

    /// <summary>Another device's alert must not fail this device's connect:
    /// several drivers share one indiserver and one property stream.</summary>
    [Test]
    public async Task AnotherDevicesAlert_IsIgnored() {
        using var client = new IndiClient();
        var wait = client.AwaitConnectOutcomeAsync("Gemini EAF",
            send: () => Task.CompletedTask,
            timeout: TimeSpan.FromMilliseconds(300), ct: default);

        await Task.Delay(30);
        FireConnection(client, "Canon DSLR EOS 1500D", IndiPropertyState.Alert, connect: false, message: "nope");

        var outcome = await wait;
        Assert.That(outcome.Refused, Is.False, "the alert belonged to another device");
    }

    // ----- what the driver said -----

    /// <summary>indi_gphoto emits the error line and then a friendlier
    /// hint. The line carrying the code is the one worth quoting.</summary>
    [Test]
    public void RecentDriverMessage_PrefersTheErrorLineOverLaterChatter() {
        using var client = new IndiClient();
        client.RememberDriverMessage("Canon DSLR EOS 1500D",
            "[ERROR] Camera open error (-53): Could not claim the USB device");
        client.RememberDriverMessage("Canon DSLR EOS 1500D",
            "[INFO] Can not open camera: Power OK?");

        Assert.That(client.RecentDriverMessage("Canon DSLR EOS 1500D", TimeSpan.FromMinutes(1)),
            Does.Contain("-53"));
    }

    [Test]
    public void RecentDriverMessage_IgnoresMessagesOlderThanTheWindow() {
        using var client = new IndiClient();
        client.RememberDriverMessage("Mount", "[ERROR] something from last night");
        Assert.That(client.RecentDriverMessage("Mount", TimeSpan.Zero), Is.Null);
        Assert.That(client.RecentDriverMessage("Never Spoke", TimeSpan.FromMinutes(1)), Is.Null);
    }

    // ----- the hint -----

    /// <summary>The whole point of the exercise: -53 is libgphoto2 for
    /// "someone else has the camera", which nobody can act on without
    /// being told who and what to do about it.</summary>
    [TestCase("[ERROR] Camera open error (-53): Could not claim the USB device")]
    [TestCase("Can not open camera: Power OK? If camera is auto-mounted as external disk storage, please unmount it")]
    public void HintFor_UsbClaim_NamesTheDesktopVolumeMonitor(string reason) {
        var hint = IndiConnectDiagnostics.HintFor(reason);
        Assert.That(hint, Is.Not.Null);
        Assert.That(hint, Does.Contain("gvfsd-gphoto2"));
    }

    [Test]
    public void HintFor_SerialPort_PointsAtThePortRatherThanTheCable() {
        var hint = IndiConnectDiagnostics.HintFor(
            "Failed to connect to port (/dev/serial/by-id/usb-1a86_USB_Serial-if00-port0)");
        Assert.That(hint, Does.Contain("serial port"));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("Mount is parked")]
    public void HintFor_SomethingWeHaveNothingToAddTo_StaysQuiet(string? reason) {
        Assert.That(IndiConnectDiagnostics.HintFor(reason), Is.Null);
    }

    [Test]
    public void RefusalMessage_KeepsTheDriversOwnWords() {
        var msg = IndiConnectDiagnostics.RefusalMessage("Canon DSLR EOS 1500D",
            "[ERROR] Camera open error (-53): Could not claim the USB device");
        Assert.That(msg, Does.Contain("Canon DSLR EOS 1500D"));
        Assert.That(msg, Does.Contain("-53"), "never drop the driver's own reason");
        Assert.That(msg, Does.Contain("pkill"), "and say what to do about it");
    }

    [Test]
    public void RefusalMessage_WithoutAReason_StillSaysWhichDeviceRefused() {
        var msg = IndiConnectDiagnostics.RefusalMessage("Gemini EAF", null);
        Assert.That(msg, Does.Contain("Gemini EAF"));
        Assert.That(msg, Does.Contain("no reason"));
    }

    private static void FireConnection(IndiClient client, string device,
            IndiPropertyState state, bool connect, string? message) {
        var prop = new IndiSwitchProperty {
            Device = device,
            Name = "CONNECTION",
            State = state,
            Message = message,
            Values = new Dictionary<string, bool> { ["CONNECT"] = connect, ["DISCONNECT"] = !connect }
        };
        var eventField = typeof(IndiClient).GetField("PropertyChanged",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var handler = eventField?.GetValue(client) as Action<string, IndiProperty>;
        handler?.Invoke(device, prop);
    }
}
