using NUnit.Framework;
using NINA.Image.ImageAnalysis;

namespace NINA.Polaris.Test;

/// <summary>
/// Pins the gradient-subtraction math used by STUDIO's "Remove
/// gradient" action. Real-world cases are noisy but a few synthetic
/// gradients let us verify the polynomial fit is doing what its
/// name says.
/// </summary>
[TestFixture]
public class BackgroundExtractorTests {

    [Test]
    public void Subtract_FlatBackground_LeavesImageUnchanged() {
        // No gradient → the polynomial fit + subtract should be a
        // near-no-op (minus rounding).
        int W = 64, H = 48;
        var data = new ushort[W * H];
        Array.Fill(data, (ushort)1000);
        var output = BackgroundExtractor.Subtract(data, W, H);

        // Every pixel within ±2 ADU of the original 1000.
        for (int i = 0; i < data.Length; i++) {
            Assert.That(output[i], Is.InRange((ushort)998, (ushort)1002),
                $"pixel {i} drifted from flat background");
        }
    }

    [Test]
    public void Subtract_LinearTilt_RemovesGradient() {
        // Synthetic linear gradient from 500 (left) to 1500 (right).
        // After subtraction the corrected frame should be nearly flat
        // (≈ minBackground); we tolerate ±50 ADU because the patch
        // medians don't sit exactly on the analytic fit line.
        int W = 128, H = 96;
        var data = new ushort[W * H];
        for (int y = 0; y < H; y++) {
            for (int x = 0; x < W; x++) {
                data[y * W + x] = (ushort)(500 + (x * 1000 / (W - 1)));
            }
        }
        var output = BackgroundExtractor.Subtract(data, W, H);

        // After subtracting the fitted tilt, the spread across the
        // image should be a tiny fraction of the original 1000 ADU
        // dynamic range.
        ushort min = ushort.MaxValue, max = 0;
        for (int i = 0; i < output.Length; i++) {
            if (output[i] < min) min = output[i];
            if (output[i] > max) max = output[i];
        }
        Assert.That(max - min, Is.LessThan(50),
            $"after subtract range {min}..{max}; original 500..1500");
    }

    [Test]
    public void Subtract_PreservesMinimumLevel() {
        // The implementation subtracts the polynomial's *minimum*, not
        // its absolute value, so the dimmest area of the image keeps
        // its original brightness (≈ input min). Test with a tilt
        // again — the corrected minimum should be near 500 (the input
        // min), not 0.
        int W = 128, H = 96;
        var data = new ushort[W * H];
        for (int y = 0; y < H; y++) {
            for (int x = 0; x < W; x++) {
                data[y * W + x] = (ushort)(500 + (x * 1000 / (W - 1)));
            }
        }
        var output = BackgroundExtractor.Subtract(data, W, H);

        ushort min = ushort.MaxValue;
        for (int i = 0; i < output.Length; i++) {
            if (output[i] < min) min = output[i];
        }
        Assert.That(min, Is.InRange((ushort)450, (ushort)550),
            "Output min should hover near input min (500), not collapse to 0");
    }

    [Test]
    public void Subtract_RejectsBadDegree() {
        var data = new ushort[100];
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BackgroundExtractor.Subtract(data, 10, 10,
                new BackgroundExtractor.Options(4, 4, PolyDegree: 5)));
    }

    [Test]
    public void Subtract_StellarSpikesRejectedByMAD() {
        // Background gradient 500→1500 with a handful of saturated
        // "stars" sprinkled around. The MAD-based outlier rejection
        // inside MedianSample should ignore the stars so the fit
        // tracks the background, not the star peaks.
        int W = 128, H = 96;
        var data = new ushort[W * H];
        for (int y = 0; y < H; y++) {
            for (int x = 0; x < W; x++) {
                data[y * W + x] = (ushort)(500 + (x * 1000 / (W - 1)));
            }
        }
        var rnd = new Random(42);
        for (int s = 0; s < 30; s++) {
            int sx = rnd.Next(W);
            int sy = rnd.Next(H);
            data[sy * W + sx] = 65535;  // saturated star
        }
        var output = BackgroundExtractor.Subtract(data, W, H);

        // Stars should still be visible (their value above 65535-fit-offset
        // remains very high) but background pixels should be reasonably
        // flat. Sample 100 background pixels (not at star locations).
        ushort min = ushort.MaxValue, max = 0;
        for (int y = 5; y < H - 5; y += 3) {
            for (int x = 5; x < W - 5; x += 3) {
                var v = output[y * W + x];
                if (v < 60000) {   // skip residual stars
                    if (v < min) min = v;
                    if (v > max) max = v;
                }
            }
        }
        Assert.That(max - min, Is.LessThan(80),
            $"with stars present, bg range still {min}..{max}");
    }
}
