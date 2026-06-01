using NUnit.Framework;
using NINA.Image.ImageData;
using NINA.Polaris.Services.Studio;

namespace NINA.Polaris.Test;

/// <summary>
/// Unit tests for CalibrationMath (LSPP-1). These pin the per-frame
/// calibration math that BOTH the existing batch CalibrationService
/// AND the new LiveStackPreProcessor (LSPP-2) call into. If anyone
/// touches the math here, behaviour for BOTH consumers changes --
/// the tests guard against accidental drift.
/// </summary>
[TestFixture]
public class CalibrationMathTests {

    // ---------- CalibratePixels ----------

    [Test]
    public void CalibratePixels_NoMasters_ReturnsCopyOfLight() {
        // Sanity: with no dark/bias/flat the output should be a copy
        // of the input. Doesn't matter what the values are; only the
        // identity of the math.
        var light = new ushort[] { 100, 200, 300, 400 };
        var result = CalibrationMath.CalibratePixels(light, dark: null, bias: null, flat: null);
        Assert.That(result, Is.EqualTo(light));
        Assert.That(result, Is.Not.SameAs(light), "should be a fresh array, not the input");
    }

    [Test]
    public void CalibratePixels_DarkOnly_SubtractsDark() {
        var light = new ushort[] { 1000, 2000, 3000, 4000 };
        var dark  = new ushort[] {  100,  200,  300,  400 };
        var result = CalibrationMath.CalibratePixels(light, dark, bias: null, flat: null);
        Assert.That(result, Is.EqualTo(new ushort[] { 900, 1800, 2700, 3600 }));
    }

    [Test]
    public void CalibratePixels_BiasOnly_SubtractsBias() {
        // No dark provided -- bias should be subtracted directly.
        var light = new ushort[] { 1000, 2000, 3000, 4000 };
        var bias  = new ushort[] {   50,   50,   50,   50 };
        var result = CalibrationMath.CalibratePixels(light, dark: null, bias: bias, flat: null);
        Assert.That(result, Is.EqualTo(new ushort[] { 950, 1950, 2950, 3950 }));
    }

    [Test]
    public void CalibratePixels_DarkWinsOverBias() {
        // Dark already contains the bias signal, so when both are
        // provided only dark is applied. Mirrors the existing
        // CalibrationService behaviour (the "double-counting" guard).
        var light = new ushort[] { 1000, 1000 };
        var dark  = new ushort[] {  100,  100 };
        var bias  = new ushort[] {  999,  999 };   // would over-subtract if applied
        var result = CalibrationMath.CalibratePixels(light, dark, bias, flat: null);
        Assert.That(result, Is.EqualTo(new ushort[] { 900, 900 }));
    }

    [Test]
    public void CalibratePixels_FlatDividesAndPreservesScale() {
        // Flat with normalised values 0.5 + mean 100. Division by 0.5
        // doubles the corresponding pixels; division by 1.0 leaves
        // them; division by 2.0 halves them.
        var light = new ushort[] { 1000, 1000, 1000 };
        var norm  = new double[] { 0.5, 1.0, 2.0 };
        var result = CalibrationMath.CalibratePixels(light, dark: null, bias: null,
            flat: (norm, 100.0));
        Assert.That(result, Is.EqualTo(new ushort[] { 2000, 1000, 500 }));
    }

    [Test]
    public void CalibratePixels_ClampsNegativesAndOverflow() {
        // Light < dark would go negative -> clamp to 0.
        // Dark of 0 + flat divide by 0.0001 explodes -> clamp to 65535.
        var light = new ushort[] { 50, 50000 };
        var dark  = new ushort[] { 200, 0 };
        var norm  = new double[] { 1.0, 0.0001 };
        var result = CalibrationMath.CalibratePixels(light, dark, bias: null,
            flat: (norm, 1.0));
        Assert.That(result[0], Is.EqualTo(0), "underflow should clamp");
        Assert.That(result[1], Is.EqualTo(65535), "overflow should clamp");
    }

    [Test]
    public void CalibratePixels_FlatNearZero_SkipsDivision() {
        // Flat pixel == 0 would be a divide-by-zero. Guard at 1e-6
        // skips the division entirely, light pixel passes through
        // (minus dark, if any). Keeps live-stack robust against a
        // single bad pixel in the flat.
        var light = new ushort[] { 1000 };
        var norm  = new double[] { 0.0 };
        var result = CalibrationMath.CalibratePixels(light, dark: null, bias: null,
            flat: (norm, 1.0));
        Assert.That(result[0], Is.EqualTo(1000));
    }

    [Test]
    public void CalibratePixels_DimensionMismatch_Throws() {
        var light = new ushort[] { 1, 2, 3, 4 };
        var darkWrong = new ushort[] { 1, 2 };
        Assert.Throws<InvalidOperationException>(() =>
            CalibrationMath.CalibratePixels(light, darkWrong, bias: null, flat: null));
    }

    // ---------- NormalizeFlat ----------

    [Test]
    public void NormalizeFlat_NoCalibrator_DividesByMean() {
        // Mean of [100, 200, 300, 400] is 250; normalised values are
        // each pixel / mean.
        var flat = MakeImage(new ushort[] { 100, 200, 300, 400 });
        var (norm, mean) = CalibrationMath.NormalizeFlat(flat, cal: null);
        Assert.That(mean, Is.EqualTo(250.0).Within(1e-6));
        Assert.That(norm[0], Is.EqualTo(100.0 / 250.0).Within(1e-9));
        Assert.That(norm[3], Is.EqualTo(400.0 / 250.0).Within(1e-9));
    }

    [Test]
    public void NormalizeFlat_WithBias_SubtractsBeforeNormalising() {
        // After bias subtraction the corrected flat is [80, 180, 280, 380]
        // (mean 230). Normalisation divides by that.
        var flat = MakeImage(new ushort[] { 100, 200, 300, 400 });
        var bias = MakeImage(new ushort[] { 20, 20, 20, 20 });
        var (norm, mean) = CalibrationMath.NormalizeFlat(flat, bias);
        Assert.That(mean, Is.EqualTo(230.0).Within(1e-6));
        Assert.That(norm[0], Is.EqualTo(80.0 / 230.0).Within(1e-9));
    }

    // ---------- FindNearestDark / FindMatchingFlat / FindMatchingBias ----------

    [Test]
    public void FindNearestDark_PicksClosestExposureAtMatchingGain() {
        var darks = new List<FrameRow> {
            MakeRow(id: 1, gain: 100, exp: 30.0),
            MakeRow(id: 2, gain: 100, exp: 60.0),
            MakeRow(id: 3, gain: 200, exp: 120.0),  // gain mismatch, skip
            MakeRow(id: 4, gain: 100, exp: 120.0),
        };
        var pick = CalibrationMath.FindNearestDark(darks, exposure: 90.0, gain: 100);
        Assert.That(pick?.Id, Is.EqualTo(2).Or.EqualTo(4),
            "tied 30s gap to 60s and 120s -- first-seen wins, which is row 2");
        Assert.That(pick?.Id, Is.EqualTo(2));   // pin tie-break order
    }

    [Test]
    public void FindNearestDark_ReturnsNullWhenNoGainMatch() {
        var darks = new List<FrameRow> {
            MakeRow(id: 1, gain: 200, exp: 60.0),
        };
        var pick = CalibrationMath.FindNearestDark(darks, exposure: 60.0, gain: 100);
        Assert.That(pick, Is.Null);
    }

    [Test]
    public void FindMatchingFlat_RequiresFilterAndGain() {
        var flats = new List<FrameRow> {
            MakeRow(id: 1, gain: 100, filter: "L"),
            MakeRow(id: 2, gain: 100, filter: "R"),
            MakeRow(id: 3, gain: 200, filter: "L"),
        };
        Assert.That(CalibrationMath.FindMatchingFlat(flats, "L", 100)?.Id, Is.EqualTo(1));
        Assert.That(CalibrationMath.FindMatchingFlat(flats, "R", 100)?.Id, Is.EqualTo(2));
        Assert.That(CalibrationMath.FindMatchingFlat(flats, "Ha", 100), Is.Null);
        Assert.That(CalibrationMath.FindMatchingFlat(flats, "L", 999), Is.Null);
    }

    [Test]
    public void FindMatchingBias_GainOnly() {
        var biases = new List<FrameRow> {
            MakeRow(id: 1, gain: 100),
            MakeRow(id: 2, gain: 200),
        };
        Assert.That(CalibrationMath.FindMatchingBias(biases, 100)?.Id, Is.EqualTo(1));
        Assert.That(CalibrationMath.FindMatchingBias(biases, 200)?.Id, Is.EqualTo(2));
        Assert.That(CalibrationMath.FindMatchingBias(biases, 50), Is.Null);
    }

    // ---------- Test helpers ----------

    /// <summary>2x2 BaseImageData from raw pixels for the flat
    /// normalisation tests. Width/height don't matter for the math,
    /// just need .Data to wrap the buffer.</summary>
    static BaseImageData MakeImage(ushort[] pixels) {
        var props = new ImageProperties {
            Width = 2,
            Height = pixels.Length / 2,
            BitDepth = 16
        };
        return new BaseImageData(pixels, props, new ImageMetaData());
    }

    static FrameRow MakeRow(int id, int gain, double exp = 0, string? filter = null)
        => new FrameRow(
            Id: id, Path: $"/tmp/{id}.fits", FileName: $"{id}.fits",
            ImageType: "MASTERDARK", Filter: filter ?? "", Target: "",
            ExposureSec: exp, Gain: gain, Offset: 0,
            Width: 100, Height: 100, Bayer: "",
            DateObs: "2026-05-31T22:00:00", FileSize: 1234);
}
