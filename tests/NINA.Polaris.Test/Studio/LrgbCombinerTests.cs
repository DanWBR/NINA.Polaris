using NUnit.Framework;
using NINA.Polaris.Services.Studio;

namespace NINA.Polaris.Test.Studio;

/// <summary>
/// CC-2: pins LrgbCombiner's two algorithms (Lab swap + Ratio). The
/// service-level wiring is covered by the ChannelCombineServiceTests
/// LrgbCompose path; these focus on the pure pixel math so a future
/// refactor (e.g. SIMD vectorisation, switching the sRGB→XYZ matrix
/// to a different white point) doesn't silently shift colour.
/// </summary>
[TestFixture]
public class LrgbCombinerTests {

    private const int W = 8, H = 8, N = W * H;

    // ─── Lab swap ────────────────────────────────────────────────────

    [Test]
    public void LabSwap_BrighterL_ProducesBrighterOutput() {
        // The core LRGB contract: a brighter L master produces a
        // brighter output. The combiner trusts the user has stretched
        // L and RGB together (PixInsight's LRGBCombination semantics).
        var r = Filled(15000);
        var g = Filled(15000);
        var b = Filled(15000);
        var L = Filled(50000);

        var output = LrgbCombiner.Combine(r, g, b, L, W, H,
            LrgbCombiner.LrgbAlgorithm.Lab);

        ushort outR = output[0];
        Assert.That(outR, Is.GreaterThan(15000),
            $"Brighter L should produce a brighter output (got {outR}, " +
            $"input was RGB=15000, L=50000).");
        // Grey input → grey output.
        Assert.That(output[N],     Is.EqualTo(outR).Within(500));
        Assert.That(output[N * 2], Is.EqualTo(outR).Within(500));
    }

    [Test]
    public void LabSwap_VaryingL_TransfersDetailToOutput() {
        // RGB is uniform; L has a step. Output's luminance should
        // track L's step (bright half of L → bright half of output)
        // while chroma stays flat (RGB was grey → output stays grey).
        var r = Filled(15000);
        var g = Filled(15000);
        var b = Filled(15000);
        var L = new ushort[N];
        for (int i = 0; i < N / 2; i++) L[i] = 5000;     // dim half
        for (int i = N / 2; i < N; i++) L[i] = 50000;    // bright half

        var output = LrgbCombiner.Combine(r, g, b, L, W, H,
            LrgbCombiner.LrgbAlgorithm.Lab);

        int dimIdx = 0;
        int brightIdx = N - 1;
        Assert.That(output[brightIdx], Is.GreaterThan(output[dimIdx] + 10000),
            $"Bright half of L should map to noticeably brighter output " +
            $"(got dim={output[dimIdx]}, bright={output[brightIdx]}).");
        // Chroma preserved within each half.
        Assert.That(output[dimIdx],    Is.EqualTo(output[N + dimIdx]).Within(500));
        Assert.That(output[brightIdx], Is.EqualTo(output[N + brightIdx]).Within(500));
    }

    [Test]
    public void LabSwap_RedRgb_PreservesRedDominance() {
        // Red-dominant input goes through the Lab roundtrip; chroma
        // (a*, b*) is preserved so output remains red-dominant
        // regardless of the L master. Tolerance on G≈B is 3000 ADU
        // because the sRGB→XYZ→Lab→XYZ→sRGB roundtrip introduces
        // small per-channel rounding (the matrix isn't exactly
        // inverse-symmetric in float, plus the gamma=2.2 shortcut
        // adds ~0.5% drift).
        var r = Filled(55000);
        var g = Filled(8000);
        var b = Filled(8000);
        var L = Filled(30000);

        var output = LrgbCombiner.Combine(r, g, b, L, W, H,
            LrgbCombiner.LrgbAlgorithm.Lab);

        ushort outR = output[0];
        ushort outG = output[N];
        ushort outB = output[N * 2];

        Assert.That(outR, Is.GreaterThan(outG),
            $"Red dominance lost in Lab swap (R={outR}, G={outG}).");
        Assert.That(outR, Is.GreaterThan(outB),
            $"Red dominance lost in Lab swap (R={outR}, B={outB}).");
        Assert.That(outG, Is.EqualTo(outB).Within(3000),
            $"Green-blue balance shifted by Lab swap (G={outG}, B={outB}).");
    }

    // ─── Ratio ───────────────────────────────────────────────────────

    [Test]
    public void Ratio_VaryingL_BrightHalfGetsBrighterOutput() {
        // ratio = L / lum(RGB). For varying L over a uniform RGB,
        // brighter L → larger ratio → brighter output.
        var r = Filled(10000);
        var g = Filled(15000);
        var b = Filled(20000);
        var L = new ushort[N];
        for (int i = 0; i < N / 2; i++) L[i] = 5000;
        for (int i = N / 2; i < N; i++) L[i] = 50000;

        var output = LrgbCombiner.Combine(r, g, b, L, W, H,
            LrgbCombiner.LrgbAlgorithm.Ratio);

        ushort dimR     = output[0];
        ushort brightR  = output[N - 1];
        Assert.That(brightR, Is.GreaterThan(dimR + 5000),
            $"Ratio's bright pixel should be noticeably brighter " +
            $"than dim (got bright={brightR}, dim={dimR}).");
    }

    [Test]
    public void Ratio_PreservesHuePerPixel() {
        // Ratio multiplies all three channels by the same factor at
        // each pixel. So outB/outG should equal inB/inG = 20000/15000
        // at every (non-clipped) pixel.
        var r = Filled(8000);
        var g = Filled(12000);
        var b = Filled(16000);
        var L = Filled(11000);   // ratio ~ 0.93, no clipping risk

        var output = LrgbCombiner.Combine(r, g, b, L, W, H,
            LrgbCombiner.LrgbAlgorithm.Ratio);

        int idx = 3;
        ushort outG = output[N + idx];
        ushort outB = output[N * 2 + idx];
        double inRatio  = 16000.0 / 12000.0;
        double outRatio = (double)outB / outG;
        Assert.That(outRatio, Is.EqualTo(inRatio).Within(0.02),
            $"Ratio should preserve hue (in B/G={inRatio:F3}, out B/G={outRatio:F3}).");
    }

    [Test]
    public void Ratio_ZeroLumPixel_DoesNotCrash() {
        // Pixel where lum(R,G,B) is zero exercises the divide-by-zero
        // guard. We expect the output pixel to be 0 or clamped rather
        // than NaN/throwing.
        var r = Filled(0);
        var g = Filled(0);
        var b = Filled(0);
        var L = Filled(10000);

        Assert.DoesNotThrow(() => {
            var output = LrgbCombiner.Combine(r, g, b, L, W, H,
                LrgbCombiner.LrgbAlgorithm.Ratio);
            // Output pixel just needs to be a valid ushort; clamp
            // handles overflow so any value in [0, 65535] is OK.
            Assert.That(output[0], Is.InRange((ushort)0, (ushort)65535));
        });
    }

    // ─── Shared shape contracts ──────────────────────────────────────

    [Test]
    public void Combine_OutputHasCorrectShape() {
        var r = Filled(1000);
        var g = Filled(2000);
        var b = Filled(3000);
        var L = Filled(4000);

        foreach (LrgbCombiner.LrgbAlgorithm algo in Enum.GetValues<LrgbCombiner.LrgbAlgorithm>()) {
            var output = LrgbCombiner.Combine(r, g, b, L, W, H, algo);
            Assert.That(output.Length, Is.EqualTo(N * 3),
                $"{algo}: output length wrong.");
        }
    }

    [Test]
    public void Combine_LengthMismatch_Throws() {
        var r = new ushort[N];
        var g = new ushort[N - 1];   // wrong length
        var b = new ushort[N];
        var L = new ushort[N];

        var ex = Assert.Throws<ArgumentException>(() =>
            LrgbCombiner.Combine(r, g, b, L, W, H));
        Assert.That(ex!.Message, Does.Contain("length mismatch").IgnoreCase);
    }

    // ─── helpers ─────────────────────────────────────────────────────

    private static ushort[] Filled(ushort value) {
        var a = new ushort[N];
        Array.Fill(a, value);
        return a;
    }
}
