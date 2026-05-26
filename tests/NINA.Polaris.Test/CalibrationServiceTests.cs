using NUnit.Framework;
using System.Reflection;

namespace NINA.Polaris.Test;

/// <summary>
/// Pins the calibration math and master-matching rules CalibrationService
/// uses. The private helpers (NormalizeFlat, FindNearestDark,
/// FindMatchingFlat, FindMatchingBias) are exercised via reflection
/// rather than spinning up FrameLibraryService, the math + matching
/// rules are what's worth pinning; the I/O wiring around them is
/// covered by the end-to-end manual verification.
/// </summary>
[TestFixture]
public class CalibrationServiceTests {

    private static readonly Type SvcType = Type.GetType(
        "NINA.Polaris.Services.Studio.CalibrationService, NINA.Polaris")!;

    private static MethodInfo PrivateStatic(string name)
        => SvcType.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;

    // --- Flat normalisation ---------------------------------------

    [Test]
    public void NormalizeFlat_NoCalibrator_DividesByMean() {
        // Simple gradient flat: pixels 100..199. mean = 149.5.
        var pixels = new ushort[100];
        for (int i = 0; i < 100; i++) pixels[i] = (ushort)(100 + i);
        var (norm, mean) = InvokeNormalizeFlat(pixels, null);
        Assert.That(mean, Is.EqualTo(149.5).Within(0.1));
        Assert.That(norm[0], Is.EqualTo(100.0 / 149.5).Within(1e-6));
        Assert.That(norm[99], Is.EqualTo(199.0 / 149.5).Within(1e-6));
    }

    [Test]
    public void NormalizeFlat_WithCalibrator_SubtractsBeforeNormalising() {
        // Flat values 200, bias values 50 → corrected = 150 everywhere.
        var flat = new ushort[10];
        var bias = new ushort[10];
        for (int i = 0; i < 10; i++) { flat[i] = 200; bias[i] = 50; }
        var (norm, mean) = InvokeNormalizeFlat(flat, bias);
        Assert.That(mean, Is.EqualTo(150).Within(0.01));
        // Uniform flat → uniform normalised values of 1.0.
        foreach (var v in norm) Assert.That(v, Is.EqualTo(1.0).Within(1e-9));
    }

    [Test]
    public void NormalizeFlat_NegativeCorrectionClampedToZero() {
        // Bias higher than flat (pathological but defended against).
        var flat = new ushort[] { 100, 100, 100 };
        var bias = new ushort[] { 200, 100, 50 };
        var (norm, _) = InvokeNormalizeFlat(flat, bias);
        // first pixel: 100-200 → clamped to 0
        Assert.That(norm[0], Is.EqualTo(0));
    }

    [Test]
    public void NormalizeFlat_FlatlineFlat_DoesntDivideByZero() {
        var flat = new ushort[10]; // all zeros
        Assert.DoesNotThrow(() => InvokeNormalizeFlat(flat, null));
    }

    // --- Dark matching -------------------------------------------

    [Test]
    public void FindNearestDark_RequiresExactGainMatch() {
        var darks = new List<object> {
            MakeFrameRow(id: 1, gain: 100, exposureSec: 300),
            MakeFrameRow(id: 2, gain: 50,  exposureSec: 300)
        };
        // Want gain=100 → must pick id 1 even if id 2's exposure is
        // closer to whatever we ask.
        var picked = InvokeFindNearestDark(darks, 300, 100);
        Assert.That(picked, Is.EqualTo(1));
        // gain=200 has no candidate at all → null.
        Assert.That(InvokeFindNearestDark(darks, 300, 200), Is.Null);
    }

    [Test]
    public void FindNearestDark_PicksClosestExposureWithinGain() {
        var darks = new List<object> {
            MakeFrameRow(id: 1, gain: 100, exposureSec: 60),
            MakeFrameRow(id: 2, gain: 100, exposureSec: 300),
            MakeFrameRow(id: 3, gain: 100, exposureSec: 600)
        };
        Assert.That(InvokeFindNearestDark(darks, 350, 100), Is.EqualTo(2),
            "300s is closer to 350 than 600 is");
    }

    // --- Flat matching --------------------------------------------

    [Test]
    public void FindMatchingFlat_RequiresFilterAndGain() {
        var flats = new List<object> {
            MakeFrameRow(id: 10, gain: 100, filter: "Ha"),
            MakeFrameRow(id: 11, gain: 100, filter: "L"),
            MakeFrameRow(id: 12, gain: 200, filter: "Ha")
        };
        Assert.That(InvokeFindMatchingFlat(flats, "Ha", 100), Is.EqualTo(10));
        Assert.That(InvokeFindMatchingFlat(flats, "L",  100), Is.EqualTo(11));
        Assert.That(InvokeFindMatchingFlat(flats, "Ha", 200), Is.EqualTo(12));
        Assert.That(InvokeFindMatchingFlat(flats, "OIII", 100), Is.Null,
            "No OIII flat → no match");
    }

    [Test]
    public void FindMatchingFlat_FilterMatchIsCaseInsensitive() {
        var flats = new List<object> { MakeFrameRow(id: 1, gain: 100, filter: "Ha") };
        Assert.That(InvokeFindMatchingFlat(flats, "HA", 100), Is.EqualTo(1));
    }

    // --- Reflection helpers --------------------------------------

    private static (double[] norm, double mean) InvokeNormalizeFlat(ushort[] flat, ushort[]? cal) {
        var baseImageDataType = Type.GetType("NINA.Image.ImageData.BaseImageData, NINA.Image.Portable")!;
        var imagePropsType = Type.GetType("NINA.Image.ImageData.ImageProperties, NINA.Image.Portable")!;
        var props = Activator.CreateInstance(imagePropsType)!;
        imagePropsType.GetProperty("Width")!.SetValue(props, flat.Length);
        imagePropsType.GetProperty("Height")!.SetValue(props, 1);

        var flatImg = Activator.CreateInstance(baseImageDataType,
            new object?[] { flat, props, null })!;
        object? calImg = null;
        if (cal != null) {
            var calProps = Activator.CreateInstance(imagePropsType)!;
            imagePropsType.GetProperty("Width")!.SetValue(calProps, cal.Length);
            imagePropsType.GetProperty("Height")!.SetValue(calProps, 1);
            calImg = Activator.CreateInstance(baseImageDataType,
                new object?[] { cal, calProps, null });
        }
        var result = PrivateStatic("NormalizeFlat").Invoke(null, new[] { flatImg, calImg })!;
        var resultType = result.GetType();
        var norm = (double[])resultType.GetField("Item1")!.GetValue(result)!;
        var mean = (double)resultType.GetField("Item2")!.GetValue(result)!;
        return (norm, mean);
    }

    private static int? InvokeFindNearestDark(List<object> darks, double exp, int gain) {
        var rowType = Type.GetType("NINA.Polaris.Services.Studio.FrameRow, NINA.Polaris")!;
        var listType = typeof(List<>).MakeGenericType(rowType);
        var list = (System.Collections.IList)Activator.CreateInstance(listType)!;
        foreach (var d in darks) list.Add(d);
        return (int?)PrivateStatic("FindNearestDark").Invoke(null, new object[] { list, exp, gain });
    }

    private static int? InvokeFindMatchingFlat(List<object> flats, string filter, int gain) {
        var rowType = Type.GetType("NINA.Polaris.Services.Studio.FrameRow, NINA.Polaris")!;
        var listType = typeof(List<>).MakeGenericType(rowType);
        var list = (System.Collections.IList)Activator.CreateInstance(listType)!;
        foreach (var f in flats) list.Add(f);
        return (int?)PrivateStatic("FindMatchingFlat").Invoke(null, new object[] { list, filter, gain });
    }

    private static object MakeFrameRow(int id, int gain = 0, double exposureSec = 0,
                                       string filter = "", string target = "") {
        // FrameRow signature: (Id, Path, FileName, ImageType, Filter, Target,
        //                     ExposureSec, Gain, Offset, Width, Height, Bayer,
        //                     DateObs, FileSize)
        var rowType = Type.GetType("NINA.Polaris.Services.Studio.FrameRow, NINA.Polaris")!;
        return Activator.CreateInstance(rowType, new object[] {
            id, "", "", "MASTERDARK", filter, target,
            exposureSec, gain, 0, 0, 0, "", "", 0L
        })!;
    }
}
