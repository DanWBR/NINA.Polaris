using NUnit.Framework;
using NINA.Core.Enum;
using NINA.Image.ImageAnalysis;

namespace NINA.Polaris.Test;

/// <summary>
/// Pins the bilinear-debayer math: the four canonical patterns'
/// colour layouts must match the convention documented in
/// BayerDebayer (top-left 2×2 read row-major), and the interpolation
/// must populate every channel at every pixel.
/// </summary>
[TestFixture]
public class BayerDebayerTests {

    private static ushort[] Build4x4(ushort fill) {
        var a = new ushort[16];
        Array.Fill(a, fill);
        return a;
    }

    [Test]
    public void Bilinear_RGGB_RedAtTopLeftSurvives() {
        // Pattern: top-left is R. Force a 4×4 with the R pixel at (0,0)
        // = 60000 and everything else = 0; the R-channel output should
        // be 60000 at (0,0) and the green/blue channels should be 0
        // there (no neighbours to average).
        var cfa = new ushort[16];
        cfa[0] = 60000;
        var ch = BayerDebayer.Bilinear(cfa, 4, 4, BayerPatternEnum.RGGB);
        Assert.That(ch.R[0], Is.EqualTo(60000));
        // G at the corner has no N/S/E/W neighbour with green — only
        // (0,1) and (1,0) which are green sites. They're zero in this
        // synthetic frame, so green at (0,0) is 0.
        Assert.That(ch.G[0], Is.EqualTo(0));
        Assert.That(ch.B[0], Is.EqualTo(0));
    }

    [Test]
    public void Bilinear_BGGR_BlueAtTopLeftSurvives() {
        var cfa = new ushort[16];
        cfa[0] = 50000;
        var ch = BayerDebayer.Bilinear(cfa, 4, 4, BayerPatternEnum.BGGR);
        Assert.That(ch.B[0], Is.EqualTo(50000));
        Assert.That(ch.R[0], Is.EqualTo(0));   // no R neighbours
    }

    [Test]
    public void Bilinear_OutputChannelsHaveCorrectSize() {
        var cfa = Build4x4(1000);
        var ch = BayerDebayer.Bilinear(cfa, 4, 4, BayerPatternEnum.RGGB);
        Assert.That(ch.R.Length, Is.EqualTo(16));
        Assert.That(ch.G.Length, Is.EqualTo(16));
        Assert.That(ch.B.Length, Is.EqualTo(16));
    }

    [Test]
    public void Bilinear_UniformInput_ProducesUniformOutput() {
        // Flat-field 1000 everywhere. After bilinear demosaic every
        // pixel of every channel should also read 1000 (interpolation
        // of constants is the constant), modulo edge effects.
        var cfa = Build4x4(1000);
        var ch = BayerDebayer.Bilinear(cfa, 4, 4, BayerPatternEnum.RGGB);
        // Check a centre pixel (1,1) — it has full neighbour coverage.
        int idx = 1 * 4 + 1;
        Assert.That(ch.R[idx], Is.EqualTo(1000));
        Assert.That(ch.G[idx], Is.EqualTo(1000));
        Assert.That(ch.B[idx], Is.EqualTo(1000));
    }

    [Test]
    public void Bilinear_RejectsNonePattern() {
        var cfa = Build4x4(0);
        Assert.Throws<ArgumentException>(() =>
            BayerDebayer.Bilinear(cfa, 4, 4, BayerPatternEnum.None));
        Assert.Throws<ArgumentException>(() =>
            BayerDebayer.Bilinear(cfa, 4, 4, BayerPatternEnum.Auto));
    }

    [Test]
    public void ToLuminance_GreenDominatesWeight() {
        // Rec.601: Y = 0.299R + 0.587G + 0.114B. A pure-green pixel
        // should clearly outshine a pure-blue one of the same value.
        var ch = new BayerDebayer.Channels(
            R: new ushort[] { 0, 0, 0 },
            G: new ushort[] { 0, 1000, 0 },
            B: new ushort[] { 0, 0, 0 });
        var y = BayerDebayer.ToLuminance(ch);
        Assert.That(y[1], Is.EqualTo(587));  // 0.587 × 1000

        ch = new BayerDebayer.Channels(
            R: new ushort[] { 0, 0, 0 },
            G: new ushort[] { 0, 0, 0 },
            B: new ushort[] { 0, 1000, 0 });
        y = BayerDebayer.ToLuminance(ch);
        Assert.That(y[1], Is.EqualTo(114));  // 0.114 × 1000
    }

    [TestCase(BayerPatternEnum.RGGB)]
    [TestCase(BayerPatternEnum.GRBG)]
    [TestCase(BayerPatternEnum.GBRG)]
    [TestCase(BayerPatternEnum.BGGR)]
    public void Bilinear_AllFourPatternsProduceValidOutput(BayerPatternEnum pattern) {
        // Smoke test: each pattern returns three full-sized buffers
        // and doesn't throw for a typical input. Specific colour
        // placement is locked down by the dedicated RGGB / BGGR tests.
        var cfa = new ushort[64];
        var r = new Random(42);
        for (int i = 0; i < 64; i++) cfa[i] = (ushort)r.Next(0, 65535);
        var ch = BayerDebayer.Bilinear(cfa, 8, 8, pattern);
        Assert.That(ch.R.Length, Is.EqualTo(64));
        Assert.That(ch.G.Length, Is.EqualTo(64));
        Assert.That(ch.B.Length, Is.EqualTo(64));
    }
}
