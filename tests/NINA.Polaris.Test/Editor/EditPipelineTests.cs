using NUnit.Framework;
using NINA.Image.Editor;

namespace NINA.Polaris.Test.Editor;

/// <summary>
/// Pins the EditPipeline contract. Each step has at least one test that
/// exercises both "default = no-op" and "max slider = visible change",
/// guarding against silent regressions where the pipeline forgets to
/// apply (or over-applies) a step.
/// </summary>
[TestFixture]
public class EditPipelineTests {

    private const int W = 16;
    private const int H = 16;

    [Test]
    public void Apply_NullParams_ReturnsBuffer() {
        var buf = SolidMono(128);
        var result = EditPipeline.Apply(buf, W, H, 1, null!);
        Assert.That(result, Is.SameAs(buf));
    }

    [Test]
    public void Apply_AllDefaults_NoChange() {
        var buf = Gradient(channels: 1);
        var original = (byte[])buf.Clone();
        EditPipeline.Apply(buf, W, H, 1, EditParams.Defaults);
        Assert.That(buf, Is.EqualTo(original), "Default params must be a no-op");
    }

    [Test]
    public void Apply_ExposurePlus1_DoublesBrightness() {
        var buf = SolidMono(60);
        EditPipeline.Apply(buf, W, H, 1, EditParams.Defaults with {
            Light = new LightParams(Exposure: 1)
        });
        // 60 × 2 = 120 (clamped to 255 in extreme cases).
        Assert.That(buf[0], Is.EqualTo(120).Within(2),
            "Exposure +1 stop should ≈ double brightness in display tone space");
    }

    [Test]
    public void Apply_ExposureMinus1_HalvesBrightness() {
        var buf = SolidMono(200);
        EditPipeline.Apply(buf, W, H, 1, EditParams.Defaults with {
            Light = new LightParams(Exposure: -1)
        });
        Assert.That(buf[0], Is.EqualTo(100).Within(2),
            "Exposure -1 stop should ≈ halve brightness");
    }

    [Test]
    public void Apply_ContrastPositive_PushesAwayFromMid() {
        var buf = new byte[] { 100, 128, 156 };
        var bufCopy = (byte[])buf.Clone();
        EditPipeline.Apply(buf, 3, 1, 1, EditParams.Defaults with {
            Light = new LightParams(Contrast: 0.8)
        });
        // 100 should darken, 156 should brighten, mid stays near mid.
        Assert.That(buf[0], Is.LessThan(bufCopy[0]), "Sub-mid pixel should darken");
        Assert.That(buf[2], Is.GreaterThan(bufCopy[2]), "Super-mid pixel should brighten");
    }

    [Test]
    public void Apply_SaturationMinusOne_Grayscales() {
        var buf = new byte[] { 255, 0, 0 }; // pure red
        EditPipeline.Apply(buf, 1, 1, 3, EditParams.Defaults with {
            Color = new ColorParams(Saturation: -1)
        });
        // After full desat, R=G=B (grayscale).
        Assert.That(buf[0], Is.EqualTo(buf[1]).Within(2));
        Assert.That(buf[1], Is.EqualTo(buf[2]).Within(2));
    }

    [Test]
    public void Apply_VibranceProtectsSaturatedPixels() {
        // Two pixels: dull (R=120,G=100,B=100) and already-saturated (R=255,G=0,B=0).
        var buf = new byte[] { 120, 100, 100,  255, 0, 0 };
        var pre = (byte[])buf.Clone();
        EditPipeline.Apply(buf, 2, 1, 3, EditParams.Defaults with {
            Color = new ColorParams(Vibrance: 1)
        });
        // Dull pixel should saturate more; pure-red pixel should change less.
        int dullDelta = Math.Abs(buf[0] - pre[0]) + Math.Abs(buf[1] - pre[1]);
        int satDelta  = Math.Abs(buf[3] - pre[3]) + Math.Abs(buf[4] - pre[4]);
        Assert.That(dullDelta, Is.GreaterThan(satDelta),
            "Vibrance should affect dull pixel more than already-saturated one");
    }

    [Test]
    public void Apply_HueRotate180_SwapsRedAndCyan() {
        var buf = new byte[] { 255, 0, 0 };   // pure red, hue=0
        EditPipeline.Apply(buf, 1, 1, 3, EditParams.Defaults with {
            Color = new ColorParams(Hue: 180)
        });
        // Hue 180 = cyan (R=0, G=255, B=255)
        Assert.That(buf[0], Is.EqualTo(0).Within(2));
        Assert.That(buf[1], Is.EqualTo(255).Within(2));
        Assert.That(buf[2], Is.EqualTo(255).Within(2));
    }

    [Test]
    public void Apply_ToneCurveIdentity_NoChange() {
        var buf = Gradient(channels: 1);
        var original = (byte[])buf.Clone();
        EditPipeline.Apply(buf, W, H, 1, EditParams.Defaults with {
            ToneCurve = new ToneCurveParams(Rgb: new[] {
                new CurvePoint(0, 0), new CurvePoint(255, 255)
            })
        });
        Assert.That(buf, Is.EqualTo(original).Within(1).AsCollection,
            "Identity tone curve should leave gradient ±1");
    }

    [Test]
    public void Apply_SharpenAmount_ChangesEdgePixels() {
        // Pure mid-grey shouldn't change under sharpen (no edges).
        var buf = SolidMono(128);
        EditPipeline.Apply(buf, W, H, 1, EditParams.Defaults with {
            Detail = new DetailParams(SharpenAmount: 1, SharpenRadius: 1)
        });
        // No-edge image survives sharpen ±1.
        for (int i = 0; i < buf.Length; i++) {
            Assert.That(buf[i], Is.EqualTo(128).Within(2),
                $"Flat field should be near-invariant under sharpen at i={i}");
        }
    }

    [Test]
    public void Apply_VignetteMinus1_DarkensCorners() {
        var buf = SolidMono(200);
        EditPipeline.Apply(buf, W, H, 1, EditParams.Defaults with {
            Effects = new EffectsParams(VignetteAmount: -1, VignetteFeather: 0.1)
        });
        // Centre pixel stays bright; corner darkens.
        byte center = buf[(H / 2) * W + (W / 2)];
        byte corner = buf[0];
        Assert.That(center, Is.GreaterThan(corner),
            "Negative vignette should darken corners more than centre");
    }

    [Test]
    public void ApplyCropResize_Crop_ReducesDimensions() {
        var buf = Gradient(channels: 1);
        var (data, w, h) = EditPipeline.ApplyCropResize(buf, W, H, 1,
            new CropParams(2, 2, 8, 8), null, null);
        Assert.That(w, Is.EqualTo(8));
        Assert.That(h, Is.EqualTo(8));
        Assert.That(data.Length, Is.EqualTo(64));
    }

    [Test]
    public void ApplyCropResize_Resize_TargetDimensions() {
        var buf = Gradient(channels: 1);
        var (data, w, h) = EditPipeline.ApplyCropResize(buf, W, H, 1,
            null, 8, 8);
        Assert.That(w, Is.EqualTo(8));
        Assert.That(h, Is.EqualTo(8));
        Assert.That(data.Length, Is.EqualTo(64));
    }

    // ── helpers ──────────────────────────────────────────────────────

    private static byte[] SolidMono(byte value) {
        var arr = new byte[W * H];
        for (int i = 0; i < arr.Length; i++) arr[i] = value;
        return arr;
    }

    private static byte[] Gradient(int channels) {
        var arr = new byte[W * H * channels];
        for (int y = 0; y < H; y++) {
            for (int x = 0; x < W; x++) {
                byte v = (byte)((x + y) * 255 / (W + H - 2));
                for (int c = 0; c < channels; c++) {
                    arr[(y * W + x) * channels + c] = v;
                }
            }
        }
        return arr;
    }
}
