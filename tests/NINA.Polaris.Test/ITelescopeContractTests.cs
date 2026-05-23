using NUnit.Framework;
using NINA.Image.Interfaces;

namespace NINA.Polaris.Test;

/// <summary>
/// Pins the <see cref="ITelescope"/> abstraction the EquipmentManager,
/// Slew &amp; Center workflow, meridian-flip service and sequencer all
/// depend on. The interface is the seam where direct Wi-Fi drivers
/// (SynScan UDP, NexStar TCP, LX200 TCP) plug in next to the existing
/// INDI mount.
/// </summary>
[TestFixture]
public class ITelescopeContractTests {

    [Test]
    public void Capabilities_GermanEquatorialPreset_EnablesEverything() {
        var c = MountCapabilities.GermanEquatorial;
        Assert.That(c.SupportsPark, Is.True);
        Assert.That(c.SupportsTrackingToggle, Is.True);
        Assert.That(c.SupportsSync, Is.True);
        Assert.That(c.SupportsPierSide, Is.True);
        Assert.That(c.SupportsManualJog, Is.True);
    }

    [Test]
    public void Capabilities_AltAzPreset_HasNoPierSide() {
        // Alt-az mounts (AZ-GTi, NexStar SE fork) don't have a
        // mechanical pier side; the UI hides the indicator.
        var c = MountCapabilities.AltAz;
        Assert.That(c.SupportsPierSide, Is.False);
        Assert.That(c.SupportsPark, Is.True,
            "Alt-az mounts still park (typically to a home position)");
        Assert.That(c.SupportsTrackingToggle, Is.True);
    }

    [Test]
    public void IndiTelescopeImplementsITelescope() {
        // Compile-time sanity: the refactor must keep IndiTelescope
        // implementing the new interface so EquipmentManager's
        // ITelescope-typed property still binds.
        Assert.That(typeof(ITelescope).IsAssignableFrom(typeof(NINA.INDI.Devices.IndiTelescope)),
            Is.True);
    }
}
