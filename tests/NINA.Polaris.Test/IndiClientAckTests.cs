using NUnit.Framework;
using NINA.INDI.Client;
using NINA.INDI.Protocol;

namespace NINA.Polaris.Test;

/// <summary>
/// INDIROB-1: covers the ack-based property-write helpers added to
/// <see cref="IndiClient"/>. The internal SendAndAwaitAckAsync (used
/// by the public Set*AsyncAck wrappers) is what we exercise here —
/// tests pass a no-op send func so we don't need a real socket, then
/// simulate the driver's echo by firing PropertyChanged manually.
/// </summary>
[TestFixture]
public class IndiClientAckTests {
    /// <summary>Driver acknowledged → state=Busy → result has
    /// Acknowledged=true. Happy path for slew: mount said "got it,
    /// working on it" so the caller can poll IsSlewing without racing
    /// a stale Ok state.</summary>
    [Test]
    public async Task AckOnBusy_ReturnsAcknowledged() {
        using var client = new IndiClient("localhost", 7624);
        var sendTask = client.SendAndAwaitAckAsync(
            "Mount", "EQUATORIAL_EOD_COORD",
            send: () => Task.CompletedTask,
            timeout: TimeSpan.FromSeconds(2),
            ct: default);

        await Task.Delay(50);
        FirePropertyChanged(client, "Mount", "EQUATORIAL_EOD_COORD",
            IndiPropertyState.Busy, message: null);

        var result = await sendTask;
        Assert.That(result.Acknowledged, Is.True);
        Assert.That(result.Rejected, Is.False);
        Assert.That(result.TimedOut, Is.False);
        Assert.That(result.AlertMessage, Is.Null);
    }

    /// <summary>Instant-action driver: state=Ok counts as ack too
    /// (some toggles like tracking on/off complete in microseconds
    /// and never visit Busy).</summary>
    [Test]
    public async Task AckOnOk_ReturnsAcknowledged() {
        using var client = new IndiClient();
        var sendTask = client.SendAndAwaitAckAsync(
            "Mount", "TELESCOPE_TRACK_STATE",
            send: () => Task.CompletedTask,
            timeout: TimeSpan.FromSeconds(2),
            ct: default);

        await Task.Delay(50);
        FirePropertyChanged(client, "Mount", "TELESCOPE_TRACK_STATE",
            IndiPropertyState.Ok, message: null);

        var result = await sendTask;
        Assert.That(result.Acknowledged, Is.True);
        Assert.That(result.Rejected, Is.False);
    }

    /// <summary>Driver rejected with state=Alert + a message — most
    /// common failure mode in practice. Verify the AlertMessage is
    /// captured so IndiTelescope can re-throw with a user-actionable
    /// string ("Below horizon", "Mount is parked", etc).</summary>
    [Test]
    public async Task AlertWithMessage_ReturnsRejectedWithMessage() {
        using var client = new IndiClient();
        var sendTask = client.SendAndAwaitAckAsync(
            "Mount", "EQUATORIAL_EOD_COORD",
            send: () => Task.CompletedTask,
            timeout: TimeSpan.FromSeconds(2),
            ct: default);

        await Task.Delay(50);
        FirePropertyChanged(client, "Mount", "EQUATORIAL_EOD_COORD",
            IndiPropertyState.Alert, message: "Below horizon");

        var result = await sendTask;
        Assert.That(result.Acknowledged, Is.False);
        Assert.That(result.Rejected, Is.True);
        Assert.That(result.TimedOut, Is.False);
        Assert.That(result.AlertMessage, Is.EqualTo("Below horizon"));
    }

    /// <summary>Driver was silent — no echo within timeout. This
    /// usually means the property name was wrong (server-side drop)
    /// or the driver wedged. Different from Alert so the caller can
    /// surface a different user-facing message.</summary>
    [Test]
    public async Task NoEcho_ReturnsTimedOut() {
        using var client = new IndiClient();
        var result = await client.SendAndAwaitAckAsync(
            "Mount", "EQUATORIAL_EOD_COORD",
            send: () => Task.CompletedTask,
            timeout: TimeSpan.FromMilliseconds(200),
            ct: default);

        Assert.That(result.Acknowledged, Is.False);
        Assert.That(result.Rejected, Is.False);
        Assert.That(result.TimedOut, Is.True);
        Assert.That(result.AlertMessage, Is.Null);
    }

    /// <summary>Echo from a DIFFERENT device or DIFFERENT property
    /// should not satisfy the ack — the helper filters strictly by
    /// (device, property). Without this we'd see false-positive acks
    /// when an unrelated property updates concurrently (very common
    /// during connect/disconnect cycles where many properties echo
    /// at once).</summary>
    [Test]
    public async Task WrongDevice_OrWrongProperty_IsIgnored() {
        using var client = new IndiClient();
        var sendTask = client.SendAndAwaitAckAsync(
            "Mount", "EQUATORIAL_EOD_COORD",
            send: () => Task.CompletedTask,
            timeout: TimeSpan.FromMilliseconds(300),
            ct: default);

        await Task.Delay(50);
        // Different device — should not satisfy the ack.
        FirePropertyChanged(client, "Focuser", "EQUATORIAL_EOD_COORD",
            IndiPropertyState.Busy, message: null);
        // Different property on the right device — also no match.
        FirePropertyChanged(client, "Mount", "TELESCOPE_PARK",
            IndiPropertyState.Busy, message: null);

        var result = await sendTask;
        Assert.That(result.TimedOut, Is.True, "Mismatched echoes must not count as ack");
    }

    /// <summary>INDIROB-2: ConnectDeviceAsync should short-circuit
    /// (no socket write) when the device's CONNECTION.CONNECT switch
    /// is already true. This protects against the 30s no-reply hang
    /// when reconnecting to a shared INDI driver (lx200generic exposes
    /// both telescope + focuser; CONNECTing the second role hangs
    /// because the driver sees it's already up and never replies).
    /// Verified by setting the switch state manually and confirming
    /// the call completes without touching the (non-existent) socket.</summary>
    [Test]
    public async Task ConnectDeviceAsync_WhenAlreadyConnected_SkipsWriteAndReturns() {
        using var client = new IndiClient();
        // Pre-seed the device snapshot with CONNECTION.CONNECT=true.
        SeedSwitch(client, "Mount", "CONNECTION", "CONNECT", true);

        // Without the skip, SendAsync would throw "not connected" since
        // we never opened a real socket. Completing without exception
        // proves the skip path fired.
        await client.ConnectDeviceAsync("Mount");

        Assert.Pass("ConnectDeviceAsync returned without attempting a TCP write");
    }

    /// <summary>INDIROB-2: mirror for the disconnect side. If
    /// CONNECTION.DISCONNECT is already true (device already
    /// disconnected), DisconnectDeviceAsync should skip the write.</summary>
    [Test]
    public async Task DisconnectDeviceAsync_WhenAlreadyDisconnected_SkipsWriteAndReturns() {
        using var client = new IndiClient();
        SeedSwitch(client, "Mount", "CONNECTION", "DISCONNECT", true);
        await client.DisconnectDeviceAsync("Mount");
        Assert.Pass("DisconnectDeviceAsync returned without attempting a TCP write");
    }

    private static void SeedSwitch(IndiClient client, string device,
            string property, string element, bool value) {
        var prop = new IndiSwitchProperty {
            Device = device,
            Name = property,
            State = IndiPropertyState.Ok,
            Values = new Dictionary<string, bool> { [element] = value }
        };
        var deviceMap = client.Devices.GetOrAdd(device,
            _ => new System.Collections.Concurrent.ConcurrentDictionary<string, IndiProperty>());
        deviceMap[property] = prop;
    }

    /// <summary>IndiClient exposes PropertyChanged as a public event;
    /// the helper subscribes to it. Tests fire it via reflection
    /// because C# only lets the declaring class invoke events
    /// directly. Captures both the property's new state and the
    /// optional driver message (used in the Alert case).</summary>
    private static void FirePropertyChanged(IndiClient client, string device,
            string name, IndiPropertyState state, string? message) {
        var prop = new IndiNumberProperty {
            Device = device,
            Name = name,
            State = state,
            Message = message
        };
        var eventField = typeof(IndiClient).GetField("PropertyChanged",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var handler = eventField?.GetValue(client) as Action<string, IndiProperty>;
        handler?.Invoke(device, prop);
    }
}
