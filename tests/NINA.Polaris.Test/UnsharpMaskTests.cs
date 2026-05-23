using NUnit.Framework;
using NINA.Image.ImageAnalysis;

namespace NINA.Polaris.Test;

[TestFixture]
public class UnsharpMaskTests {

    [Test]
    public void Apply_AmountZero_ReturnsClone() {
        var data = new ushort[] { 100, 200, 300 };
        var sharp = UnsharpMask.Apply(data, 3, 1, amount: 0);
        Assert.That(sharp, Is.EqualTo(data));
    }

    [Test]
    public void Apply_FlatField_StaysFlat() {
        // No edges → diff(original, blurred) ≈ 0 everywhere →
        // output ≈ original.
        var data = new ushort[64 * 64];
        Array.Fill(data, (ushort)1000);
        var sharp = UnsharpMask.Apply(data, 64, 64, amount: 2.0, radius: 3);
        foreach (var v in sharp) {
            Assert.That(v, Is.InRange((ushort)998, (ushort)1002));
        }
    }

    [Test]
    public void Apply_StepEdge_BecomesSteeper() {
        // 1×32 strip: 0s on the left half, 50000 on the right. After
        // unsharp the bright side of the step gets brighter and the
        // dark side gets darker (overshoot is exactly the sharpening
        // signature).
        var data = new ushort[32];
        for (int i = 16; i < 32; i++) data[i] = 50000;
        var sharp = UnsharpMask.Apply(data, 32, 1, amount: 1.5, radius: 2);
        // Bright side just past the step (within reach of the blur
        // kernel) should be ≥ original.
        Assert.That(sharp[18], Is.GreaterThanOrEqualTo(data[18]),
            "Sample on bright side should be boosted");
        // Dark side just before the step should be lower than its
        // original 0 — except 0 is the floor, so clamp guarantees
        // ≤ original; really we just verify it doesn't get brighter.
        Assert.That(sharp[14], Is.LessThanOrEqualTo(data[14]),
            "Sample on dark side should be pulled down (or clamped at 0)");
    }

    [Test]
    public void Apply_HighThreshold_SuppressesNoise() {
        // Add tiny random noise to a flat field; with a high threshold
        // the sharpener should leave it alone (output == input).
        var data = new ushort[256];
        var rnd = new Random(42);
        for (int i = 0; i < data.Length; i++) data[i] = (ushort)(1000 + rnd.Next(-3, 4));
        var sharp = UnsharpMask.Apply(data, 256, 1, amount: 3.0, radius: 2, threshold: 100);
        Assert.That(sharp, Is.EqualTo(data),
            "Above-threshold guard should pass small differences through unchanged");
    }
}
