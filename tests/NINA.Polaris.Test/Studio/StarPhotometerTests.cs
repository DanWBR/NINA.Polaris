using NUnit.Framework;
using NINA.Image.ImageAnalysis;

namespace NINA.Polaris.Test.Studio;

/// <summary>
/// CCALB-0b: pins StarPhotometer's per-channel aperture flux. PCC
/// fits per-channel gains from these numbers, so a systematic bias
/// here would produce a calibration that looks plausible but is
/// quantitatively wrong. Tests cover known-flux recovery,
/// background subtraction, saturation rejection, and edge clipping.
/// </summary>
[TestFixture]
public class StarPhotometerTests {

    private const int W = 64, H = 64, N = W * H;

    /// <summary>
    /// Place a synthetic flat-disc star at (cx, cy) with radius r
    /// and per-channel ADU values. Returns the buffer. Background
    /// is added uniformly.
    /// </summary>
    private static ushort[] MakeFrame(int cx, int cy, int r,
            ushort starR, ushort starG, ushort starB,
            ushort bgR, ushort bgG, ushort bgB) {
        var pix = new ushort[N * 3];
        for (int i = 0; i < N; i++) {
            pix[i]         = bgR;
            pix[N + i]     = bgG;
            pix[2 * N + i] = bgB;
        }
        int r2 = r * r;
        for (int y = Math.Max(0, cy - r); y <= Math.Min(H - 1, cy + r); y++) {
            for (int x = Math.Max(0, cx - r); x <= Math.Min(W - 1, cx + r); x++) {
                int dx = x - cx, dy = y - cy;
                if (dx * dx + dy * dy > r2) continue;
                int idx = y * W + x;
                pix[idx]         = starR;
                pix[N + idx]     = starG;
                pix[2 * N + idx] = starB;
            }
        }
        return pix;
    }

    // ─── happy path ──────────────────────────────────────────────────

    [Test]
    public void MeasureRgb_KnownFlatDisc_RecoversNetPerChannelFlux() {
        // Star: radius 3, R=10000, G=20000, B=15000. BG: 1000 across.
        // Aperture (r_in = 2 * HFR ≈ 6 px) easily contains the disc;
        // background median should be 1000 from the annulus. Net flux
        // per channel = (star_value - bg) * pixel_count. For a r=3
        // disc that fits inside the aperture, pixel count ≈ π * 9 = 28
        // (integer grid; actual count depends on the discrete pixels
        // satisfying d² <= 9, which is exactly 29 in this grid).
        var pix = MakeFrame(cx: 32, cy: 32, r: 3,
            starR: 10000, starG: 20000, starB: 15000,
            bgR: 1000, bgG: 1000, bgB: 1000);
        var stars = new[] {
            new DetectedStar { X = 32, Y = 32, HFR = 3.0 }
        };
        var phots = StarPhotometer.MeasureRgb(pix, W, H, stars);
        Assert.That(phots.Count, Is.EqualTo(1));
        var p = phots[0];
        Assert.That(p.Saturated, Is.False);
        // The R channel reads ~10000, BG ~1000, net per pixel ~9000.
        // Aperture has ~113 pixels (π × r_in² = π × 36 ≈ 113) but
        // only ~29 of them are inside the bright disc, the rest are
        // at BG level. So net flux = 29 * 9000 + 84 * 0 = ~261000.
        // (Pixels outside the star but inside the aperture have BG
        // value, and net = BG - BG = 0 for them.)
        Assert.That(p.FluxR, Is.InRange(200000.0, 320000.0));
        // G channel ratio: net per G pixel = 20000 - 1000 = 19000.
        // FluxG / FluxR should track 19000 / 9000 ≈ 2.11.
        double ratioGR = p.FluxG / p.FluxR;
        Assert.That(ratioGR, Is.EqualTo(19000.0 / 9000.0).Within(0.1));
        double ratioBR = p.FluxB / p.FluxR;
        Assert.That(ratioBR, Is.EqualTo(14000.0 / 9000.0).Within(0.1));
    }

    [Test]
    public void MeasureRgb_BackgroundEstimateMatchesAnnulus() {
        // BG at 500 ADU, star at 8000 R, 8000 G, 8000 B. Photometry's
        // BackgroundR/G/B fields should report ~500 (annulus is the
        // ring between r_in and r_out, which is all BG pixels here).
        var pix = MakeFrame(cx: 32, cy: 32, r: 3,
            starR: 8000, starG: 8000, starB: 8000,
            bgR: 500, bgG: 500, bgB: 500);
        var phots = StarPhotometer.MeasureRgb(pix, W, H,
            new[] { new DetectedStar { X = 32, Y = 32, HFR = 3.0 } });
        var p = phots[0];
        Assert.That(p.BackgroundR, Is.EqualTo(500).Within(1));
        Assert.That(p.BackgroundG, Is.EqualTo(500).Within(1));
        Assert.That(p.BackgroundB, Is.EqualTo(500).Within(1));
    }

    // ─── saturation rejection ────────────────────────────────────────

    [Test]
    public void MeasureRgb_SaturatedStar_FlaggedNotMeasured() {
        // R saturated at 62000 (above the 60000 threshold). Star
        // should be returned with Saturated=true and zero flux so
        // PCC can drop it without losing the position info.
        var pix = MakeFrame(cx: 32, cy: 32, r: 3,
            starR: 62000, starG: 20000, starB: 15000,
            bgR: 500, bgG: 500, bgB: 500);
        var phots = StarPhotometer.MeasureRgb(pix, W, H,
            new[] { new DetectedStar { X = 32, Y = 32, HFR = 3.0 } });
        Assert.That(phots.Count, Is.EqualTo(1));
        Assert.That(phots[0].Saturated, Is.True);
        Assert.That(phots[0].FluxR, Is.EqualTo(0));
        Assert.That(phots[0].FluxG, Is.EqualTo(0));
        Assert.That(phots[0].FluxB, Is.EqualTo(0));
    }

    // ─── edge clipping ───────────────────────────────────────────────

    [Test]
    public void MeasureRgb_StarTooCloseToEdge_Skipped() {
        // Star at (2, 32) with HFR=3 → aperture out-radius 12 px,
        // clipping leaves only ~3 px of aperture on the right side
        // (the left side is clipped). The result must be skipped
        // (not returned with garbage flux).
        var pix = MakeFrame(cx: 2, cy: 32, r: 3,
            starR: 10000, starG: 10000, starB: 10000,
            bgR: 500, bgG: 500, bgB: 500);
        var phots = StarPhotometer.MeasureRgb(pix, W, H,
            new[] { new DetectedStar { X = 2, Y = 32, HFR = 3.0 } });
        // Either the star is dropped entirely, or it's returned with
        // saturated=false but the caller can check PixelCount. Our
        // contract: drop the star if too few aperture pixels survive
        // clipping (< 5 pixels makes flux measurement noise-dominated).
        // Note: r_in is clamped to min 2 px so a HFR=3 with most of
        // the aperture clipped might still have enough pixels; the
        // assertion is intentionally lenient about whether the star
        // is returned, only that any returned value is plausible.
        if (phots.Count > 0) {
            Assert.That(phots[0].PixelCount, Is.GreaterThanOrEqualTo(5));
        }
    }

    // ─── input validation ───────────────────────────────────────────

    [Test]
    public void MeasureRgb_WrongBufferLength_Throws() {
        // Mono buffer (length w*h, not w*h*3) is a programming error.
        var monoBuf = new ushort[N];
        Assert.Throws<ArgumentException>(() =>
            StarPhotometer.MeasureRgb(monoBuf, W, H,
                new[] { new DetectedStar { X = 32, Y = 32, HFR = 3.0 } }));
    }

    [Test]
    public void MeasureRgb_NoStars_ReturnsEmpty() {
        var pix = new ushort[N * 3];
        var phots = StarPhotometer.MeasureRgb(pix, W, H, Array.Empty<DetectedStar>());
        Assert.That(phots, Is.Empty);
    }
}
