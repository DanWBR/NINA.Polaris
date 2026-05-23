using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Tests for the pure-string helpers in HostInfo. The IO-heavy
/// DetectLinux/DetectWindows are not unit-tested directly — they hit
/// real /proc and WMI which differ per host; the classifier +
/// label-builder logic is what's worth pinning down.
/// </summary>
[TestFixture]
public class HostInfoTests {

    [TestCase("Raspberry Pi 5 Model B Rev 1.0", "raspberry-pi")]
    [TestCase("Raspberry Pi 4 Model B Rev 1.4", "raspberry-pi")]
    [TestCase("Raspberry Pi 3 Model B Plus Rev 1.3", "raspberry-pi")]
    [TestCase("NVIDIA Jetson Nano Developer Kit", "jetson")]
    [TestCase("Radxa Rock Pi 4B", "rockpi")]
    [TestCase("ROCK Pi 5B (RK3588)", "rockpi")]
    [TestCase("Hardkernel Odroid-N2Plus", "odroid")]
    [TestCase("Intel NUC11PAHi7", "mini-pc")]
    [TestCase("VMware Virtual Platform", "vm")]
    [TestCase("Innotek GmbH VirtualBox", "vm")]
    [TestCase("QEMU Standard PC", "vm")]
    [TestCase("Dell Inc. PowerEdge R720", "linux")]   // no rule matches → generic linux
    public void ClassifyLinuxModel_RecognisesCommonHardware(string model, string expected) {
        Assert.That(HostInfo.ClassifyLinuxModel(model), Is.EqualTo(expected));
    }

    [TestCase("Intel NUC11PAHi7", "mini-pc")]
    [TestCase("VMware Virtual Platform", "vm")]
    [TestCase("Microsoft Hyper-V Virtual Machine", "vm")]
    [TestCase("Dell Latitude 7420", "windows")]
    [TestCase("ASUS All Series", "windows")]
    public void ClassifyWindowsModel_RecognisesCommonHardware(string model, string expected) {
        Assert.That(HostInfo.ClassifyWindowsModel(model), Is.EqualTo(expected));
    }

    [Test]
    public void BuildShortLabel_StripsManufacturerNoise() {
        // Dell + similar OEMs love trailing "Inc.", "Corporation",
        // "Computer" — drops them to keep the bar readable.
        Assert.That(HostInfo.BuildShortLabel("linux", "Dell Inc. PowerEdge R720", "x64"),
            Is.EqualTo("Dell PowerEdge R720"));
        Assert.That(HostInfo.BuildShortLabel("linux", "Hewlett-Packard Corporation EliteBook 840", "x64"),
            Is.EqualTo("Hewlett-Packard EliteBook 840"));
    }

    [Test]
    public void BuildShortLabel_StripsRevSuffix() {
        // Silicon revisions ("Rev 1.0", "Rev 1.4") are noise to a
        // user reading the status bar.
        Assert.That(HostInfo.BuildShortLabel("raspberry-pi", "Raspberry Pi 5 Model B Rev 1.0", "arm64"),
            Is.EqualTo("Raspberry Pi 5 Model B"));
    }

    [Test]
    public void BuildShortLabel_TruncatesAt48Chars() {
        var long_ = new string('X', 80);
        var s = HostInfo.BuildShortLabel("linux", long_, "x64");
        Assert.That(s.Length, Is.LessThanOrEqualTo(48));
        Assert.That(s, Does.EndWith("..."));
    }

    [Test]
    public void BuildShortLabel_UnknownGivesArchFallback() {
        // When the model itself is unknown the bar still shows
        // something — falls back to the arch.
        Assert.That(HostInfo.BuildShortLabel("generic", "Unknown", "arm64"),
            Is.EqualTo("Host (arm64)"));
        Assert.That(HostInfo.BuildShortLabel("generic", "", "x64"),
            Is.EqualTo("Host (x64)"));
    }

    [Test]
    public void Current_ReturnsCachedInstance_AcrossCalls() {
        // HostInfo.Current is a Lazy<T>; subsequent calls must not
        // re-run the (relatively expensive) detection path.
        var a = HostInfo.Current;
        var b = HostInfo.Current;
        Assert.That(b, Is.SameAs(a));
    }

    [Test]
    public void Current_HasNonEmptyFields() {
        // Best-effort detection should always populate every field
        // with at least a fallback — the UI binds to ShortLabel
        // and expects a string.
        var info = HostInfo.Current;
        Assert.That(info.Kind, Is.Not.Null.And.Not.Empty);
        Assert.That(info.Model, Is.Not.Null.And.Not.Empty);
        Assert.That(info.Os, Is.Not.Null.And.Not.Empty);
        Assert.That(info.Architecture, Is.Not.Null.And.Not.Empty);
        Assert.That(info.Cores, Is.GreaterThan(0));
        Assert.That(info.ShortLabel, Is.Not.Null.And.Not.Empty);
    }
}
