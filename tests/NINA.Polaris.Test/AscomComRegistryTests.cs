using NINA.Ascom.Com;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Smoke tests for the registry walker. Windows-only via the
/// [Platform] gate. The tests don't assert the EXACT driver list
/// (depends on what the developer happens to have installed), they
/// validate the walker returns a coherent shape, handles the
/// "platform missing" case gracefully, and never throws.
/// </summary>
[TestFixture]
[Platform("Win")]
public class AscomComRegistryTests {

    [Test]
    public void Enumerate_AnyDeviceType_NeverThrows() {
        foreach (var t in System.Enum.GetValues<AscomComRegistry.DeviceType>()) {
            var list = AscomComRegistry.Enumerate(t);
            Assert.That(list, Is.Not.Null,
                $"Enumerate({t}) returned null instead of an empty list.");
            // Spot-check every entry has the expected shape.
            foreach (var d in list) {
                Assert.That(d.ProgId, Is.Not.Null.And.Not.Empty);
                Assert.That(d.Description, Is.Not.Null);
                Assert.That(d.DeviceType, Is.EqualTo(t),
                    "DeviceType on the record must match the queried type.");
            }
        }
    }

    [Test]
    public void IsPlatformInstalled_ReturnsBool() {
        // No assertion on the actual value — the CI box might or
        // might not have ASCOM installed. We just want the probe to
        // not throw and to come back with a bool.
        var v = AscomComRegistry.IsPlatformInstalled();
        Assert.That(v, Is.TypeOf<bool>());
    }

    [Test]
    public void Enumerate_DedupesByProgId_When64And32BitEntriesCollide() {
        // The walker prefers the 64-bit native entry when the same
        // ProgID is registered in both views. We can't force a fake
        // entry into HKLM from a test, so the assertion here is
        // weaker: no two entries in the result share a ProgID.
        foreach (var t in System.Enum.GetValues<AscomComRegistry.DeviceType>()) {
            var list = AscomComRegistry.Enumerate(t);
            var distinct = list.Select(d => d.ProgId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            Assert.That(distinct, Is.EqualTo(list.Count),
                $"Duplicate ProgIDs in {t} enumeration.");
        }
    }

    [Test]
    public void Enumerate_ResultsAreSortedByDescription() {
        // UI dropdown depends on this for predictable rendering.
        foreach (var t in System.Enum.GetValues<AscomComRegistry.DeviceType>()) {
            var list = AscomComRegistry.Enumerate(t);
            if (list.Count < 2) continue;
            var descriptions = list.Select(d => d.Description).ToList();
            var sorted = descriptions
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();
            Assert.That(descriptions, Is.EqualTo(sorted),
                $"{t} enumeration should be sorted by description case-insensitively.");
        }
    }
}
