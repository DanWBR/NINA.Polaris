using NINA.Image.ImageAnalysis;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test.LiveStack;

/// <summary>
/// Field session 2026-09-05 (ASI585MC, RGGB, 3840x2160, 60 s, gain 200): live
/// stacking rejected good frames with "alignment failed (2 stars detected)".
///
/// The detector was being handed the raw MOSAIC, and its threshold is
/// median + 5 * MAD * 1.4826. On a mosaic the MAD does not measure noise, it
/// measures the gap between the R, G and B pedestals. Measured on the real
/// light: MAD 3215, threshold 37705, while the frame's 99.99th percentile was
/// 23221 — no star could clear it. Through the 2x2 mean: MAD 162, threshold
/// 15423, 85 stars. So these tests give the synthetic field the thing that
/// actually breaks it, per-channel pedestals, not just a Bayer pattern.
/// </summary>
[TestFixture]
public class CfaStarDetectionTests {
    private const int W = 256, H = 256;

    /// <summary>An RGGB mosaic with Gaussian stars, green-dominant like a real
    /// star on an OSC sensor.</summary>
    private static ushort[] BayerFieldWithStars((int x, int y)[] stars, double fwhmPx = 3.0) {
        var img = new ushort[W * H];
        var rnd = new Random(1234);
        // The pedestals measured on the real ASI585MC light: the three channels
        // sit thousands of counts apart, which is what wrecks a robust noise
        // estimate taken over the mosaic. Noise itself is small.
        const int pedR = 13900, pedG = 12800, pedB = 19200;
        for (int y = 0; y < H; y++) {
            for (int x = 0; x < W; x++) {
                bool evenRow = (y % 2 == 0), evenCol = (x % 2 == 0);
                int ped = (evenRow && evenCol) ? pedR
                        : (!evenRow && !evenCol) ? pedB
                        : pedG;
                img[y * W + x] = (ushort)(ped + rnd.Next(0, 120));
            }
        }
        double sigma = fwhmPx / 2.355;
        foreach (var (sx, sy) in stars) {
            for (int y = Math.Max(0, sy - 8); y < Math.Min(H, sy + 8); y++) {
                for (int x = Math.Max(0, sx - 8); x < Math.Min(W, sx + 8); x++) {
                    double d2 = (x - sx) * (x - sx) + (y - sy) * (y - sy);
                    double v = 4000 * Math.Exp(-d2 / (2 * sigma * sigma));
                    // RGGB: (0,0)=R (1,0)=G (0,1)=G (1,1)=B. A star is brightest
                    // in green, weakest in blue, which is what fragments it.
                    bool even = (x % 2 == 0), evenRow = (y % 2 == 0);
                    double chan = (evenRow && even) ? 0.55            // R
                                : (!evenRow && !even) ? 0.35          // B
                                : 1.00;                               // G
                    img[y * W + x] = (ushort)Math.Min(65535, img[y * W + x] + v * chan);
                }
            }
        }
        return img;
    }

    private static (int x, int y)[] TenStars() => new[] {
        (40, 40), (90, 55), (150, 48), (200, 70), (60, 120),
        (120, 130), (185, 140), (45, 195), (110, 205), (170, 200),
    };

    [Test]
    public void OnTheRawMosaic_TheChannelPedestalsStarveTheDetector() {
        var img = BayerFieldWithStars(TenStars());
        var found = new StarDetector { MaxStars = 200 }.Detect(img, W, H);
        // Not a wish, a record of the failure this fix exists for: with the
        // channel gap inflating the MAD, the 5-sigma threshold sits above the
        // stars themselves.
        Assert.That(found.Count, Is.LessThan(TenStars().Length),
            "the raw mosaic should starve the detector");
    }

    [Test]
    public void OnThePseudoLuminance_TheStarsComeBack() {
        var img = BayerFieldWithStars(TenStars());
        var lum = LiveStackingService.CfaPseudoLuminance(img, W, H);
        var found = new StarDetector { MaxStars = 200 }.Detect(lum, W, H);
        Assert.That(found.Count, Is.EqualTo(TenStars().Length),
            "every planted star should survive once the mosaic is gone");
    }

    [Test]
    public void PositionsAreNotShifted() {
        var planted = TenStars();
        var lum = LiveStackingService.CfaPseudoLuminance(BayerFieldWithStars(planted), W, H);
        var found = new StarDetector { MaxStars = 200 }.Detect(lum, W, H);
        foreach (var (sx, sy) in planted) {
            var near = found.Where(s => Math.Abs(s.X - sx) < 1.5 && Math.Abs(s.Y - sy) < 1.5).ToList();
            Assert.That(near, Is.Not.Empty, $"no star found within 1.5 px of ({sx},{sy})");
        }
    }

    [Test]
    public void AMonoFrameIsNotTouched() {
        // The 2x2 mean is only for CFA frames; the caller skips it for mono, but
        // the helper must still be safe on a degenerate size.
        var tiny = new ushort[] { 10, 20, 30, 40 };
        var outp = LiveStackingService.CfaPseudoLuminance(tiny, 2, 2);
        Assert.That(outp[0], Is.EqualTo(25));   // (10+20+30+40)/4
        Assert.That(outp.Length, Is.EqualTo(4));
    }

    [Test]
    public void ADegenerateFrameIsReturnedUnchanged() {
        var one = new ushort[] { 42 };
        Assert.That(LiveStackingService.CfaPseudoLuminance(one, 1, 1), Is.SameAs(one));
    }
}
