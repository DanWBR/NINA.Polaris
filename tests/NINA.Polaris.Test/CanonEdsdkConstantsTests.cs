using NUnit.Framework;
using NINA.Camera.CanonEdsdk.Native;

namespace NINA.Polaris.Test;

/// <summary>
/// Pins the Tv / ISO mapping tables used by the Canon EDSDK driver.
/// These are the only parts of <c>CanonEdsdkCamera</c> testable
/// without an attached body, they translate user-supplied seconds /
/// ISO values into the camera-property enum codes Canon expects.
/// </summary>
[TestFixture]
public class CanonEdsdkConstantsTests {

    // --- Tv (shutter speed) ---------------------------------------

    [Test]
    public void TvCodeFor_ExactMatches_HitTheRightEnum() {
        // Spot-check the entries an astrophotography session actually
        // uses: 30 s, 8 s, 1 s, 1/125 s.
        Assert.That(EdsdkConstants.TvCodeFor(30.0), Is.EqualTo(0x10u));
        Assert.That(EdsdkConstants.TvCodeFor(8.0),  Is.EqualTo(0x20u));
        Assert.That(EdsdkConstants.TvCodeFor(1.0),  Is.EqualTo(0x38u));
        Assert.That(EdsdkConstants.TvCodeFor(1.0 / 125), Is.EqualTo(0x70u));
    }

    [Test]
    public void TvCodeFor_OverThirtySeconds_FallsBackToBulb() {
        // Anything past the longest non-bulb entry must drop to the
        // Bulb code so the driver knows to use BulbStart/BulbEnd
        // instead of a plain TakePicture.
        Assert.That(EdsdkConstants.TvCodeFor(60.0),  Is.EqualTo(0x0Cu));
        Assert.That(EdsdkConstants.TvCodeFor(300.0), Is.EqualTo(0x0Cu));
    }

    [Test]
    public void TvCodeFor_PicksClosest_OnInBetweenValues() {
        // 7s sits between 6s (0x23) and 8s (0x20). 8s is closer
        // (delta 1 vs 1), ties resolve to the first-scanned, but
        // 7s itself is 1 s from 8 s and 1 s from 6 s. The first-pass
        // best is 30s; later iterations beat it. End state: 8s.
        var code = EdsdkConstants.TvCodeFor(7.0);
        Assert.That(code, Is.EqualTo(0x20u).Or.EqualTo(0x23u),
            "7s should round to the nearest 6s or 8s entry");
    }

    // --- ISO -------------------------------------------------------

    [Test]
    public void IsoCodeFor_StandardValues() {
        Assert.That(EdsdkConstants.IsoCodeFor(100),   Is.EqualTo(0x40u));
        Assert.That(EdsdkConstants.IsoCodeFor(800),   Is.EqualTo(0x58u));
        Assert.That(EdsdkConstants.IsoCodeFor(3200),  Is.EqualTo(0x68u));
        Assert.That(EdsdkConstants.IsoCodeFor(25600), Is.EqualTo(0x80u));
    }

    [Test]
    public void IsoCodeFor_NonStandardValue_PicksNearest() {
        // ISO 250 isn't a Canon-native step; should land at 200.
        Assert.That(EdsdkConstants.IsoCodeFor(250), Is.EqualTo(0x48u));
        // ISO 7000 closest to 6400.
        Assert.That(EdsdkConstants.IsoCodeFor(7000), Is.EqualTo(0x70u));
    }

    [Test]
    public void IsoFromCode_RoundTripsThroughIsoCodeFor() {
        foreach (var (code, iso) in EdsdkConstants.IsoTable) {
            Assert.That(EdsdkConstants.IsoFromCode(code), Is.EqualTo(iso));
            Assert.That(EdsdkConstants.IsoCodeFor(iso), Is.EqualTo(code));
        }
    }

    [Test]
    public void IsoFromCode_UnknownCode_ReturnsZero() {
        Assert.That(EdsdkConstants.IsoFromCode(0xFFu), Is.EqualTo(0));
    }
}
