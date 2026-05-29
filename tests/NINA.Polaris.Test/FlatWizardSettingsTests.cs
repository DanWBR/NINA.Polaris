using System.Text.Json;
using NUnit.Framework;
using NINA.Polaris.Services;

namespace NINA.Polaris.Test;

/// <summary>
/// FW-1 round-trip + default-value pinning for <see cref="FlatWizardSettings"/>.
/// The class is plain JSON-serializable so the active rig PUT body
/// (which carries the full <see cref="EquipmentProfile"/> shape) can
/// hydrate it without custom converters. Tests pin the defaults so a
/// stray rename or default-value change can't silently shift what an
/// out-of-the-box profile uses.
/// </summary>
[TestFixture]
public class FlatWizardSettingsTests {

    [Test]
    public void Defaults_MatchPlannedShape() {
        var s = new FlatWizardSettings();
        Assert.That(s.TargetAdu, Is.EqualTo(30000));
        Assert.That(s.Tolerance, Is.EqualTo(0.05).Within(1e-9));
        Assert.That(s.FramesPerFilter, Is.EqualTo(20));
        Assert.That(s.MinExposureSec, Is.EqualTo(0.1).Within(1e-9));
        Assert.That(s.MaxExposureSec, Is.EqualTo(30.0).Within(1e-9));
        Assert.That(s.Binning, Is.EqualTo(1));
        Assert.That(s.MaxSearchIterations, Is.EqualTo(10));
        Assert.That(s.PanelBrightness, Is.EqualTo(0));
    }

    [Test]
    public void JsonRoundTrip_PreservesAllFields() {
        var original = new FlatWizardSettings {
            TargetAdu = 25000,
            Tolerance = 0.03,
            FramesPerFilter = 30,
            MinExposureSec = 0.05,
            MaxExposureSec = 60,
            Binning = 2,
            MaxSearchIterations = 15,
            PanelBrightness = 75
        };
        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<FlatWizardSettings>(json)!;

        Assert.That(restored.TargetAdu, Is.EqualTo(25000));
        Assert.That(restored.Tolerance, Is.EqualTo(0.03).Within(1e-9));
        Assert.That(restored.FramesPerFilter, Is.EqualTo(30));
        Assert.That(restored.MinExposureSec, Is.EqualTo(0.05).Within(1e-9));
        Assert.That(restored.MaxExposureSec, Is.EqualTo(60).Within(1e-9));
        Assert.That(restored.Binning, Is.EqualTo(2));
        Assert.That(restored.MaxSearchIterations, Is.EqualTo(15));
        Assert.That(restored.PanelBrightness, Is.EqualTo(75));
    }

    [Test]
    public void JsonDeserialize_MissingFields_KeepsDefaults() {
        // Old client / partial PUT shouldn't blow up.
        var partial = "{\"TargetAdu\":40000}";
        var restored = JsonSerializer.Deserialize<FlatWizardSettings>(partial)!;
        Assert.That(restored.TargetAdu, Is.EqualTo(40000));
        Assert.That(restored.Tolerance, Is.EqualTo(0.05).Within(1e-9));
        Assert.That(restored.FramesPerFilter, Is.EqualTo(20));
        Assert.That(restored.MinExposureSec, Is.EqualTo(0.1).Within(1e-9));
        Assert.That(restored.MaxExposureSec, Is.EqualTo(30.0).Within(1e-9));
        Assert.That(restored.Binning, Is.EqualTo(1));
        Assert.That(restored.MaxSearchIterations, Is.EqualTo(10));
        Assert.That(restored.PanelBrightness, Is.EqualTo(0));
    }

    [Test]
    public void EquipmentProfile_FreshClone_GetsOwnFlatWizardInstance() {
        // Make sure the property isn't accidentally shared across rigs
        // (would be a "static-default-instance" footgun if someone reused
        // an init expression). Two fresh profiles must not see each
        // other's mutations.
        var a = new EquipmentProfile { Name = "rigA" };
        var b = new EquipmentProfile { Name = "rigB" };
        a.FlatWizard.TargetAdu = 12345;
        Assert.That(b.FlatWizard.TargetAdu, Is.EqualTo(30000));
    }
}
