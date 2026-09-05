using NINA.Polaris.Services.Planetary;
using NUnit.Framework;

namespace NINA.Polaris.Test.Planetary;

/// <summary>
/// Field clip 2026-09-05: the Saturn stack was written on top of the camera's
/// black level (sky floor 12 800-19 200 of 65 535, planet only ~1 600 counts
/// above it), so every viewer stretch anchored on the sky and blew the planet.
/// The output normalisation has to remove that floor per channel, keep the
/// object's colour ratios, and leave a frame that already uses the range alone.
/// </summary>
[TestFixture]
public class PlanetaryLevelsTests {
    private const int W = 32, H = 32, N = W * H;

    /// <summary>One plane: a flat floor with a small bright disc on it.</summary>
    private static ushort[] Plane(ushort floor, ushort peak) {
        var p = new ushort[N];
        for (int i = 0; i < N; i++) p[i] = floor;
        for (int y = 14; y < 18; y++)
            for (int x = 14; x < 18; x++) p[y * W + x] = peak;
        return p;
    }

    private static ushort[] Cube(params ushort[][] planes) {
        var all = new ushort[planes.Length * N];
        for (int c = 0; c < planes.Length; c++) Array.Copy(planes[c], 0, all, c * N, N);
        return all;
    }

    [Test]
    public void TheBlackLevelIsRemovedAndTheSignalFillsTheRange() {
        var pixels = Cube(Plane(13900, 15500));
        var (floor, gain) = PlanetaryFrames.NormaliseLevels(pixels, 1, N);

        Assert.That(floor[0], Is.EqualTo(13900).Within(1));
        Assert.That(gain, Is.GreaterThan(30));
        Assert.That(pixels[0], Is.EqualTo(0), "sky sits at black");
        Assert.That(pixels[15 * W + 15], Is.GreaterThan(55000), "the planet reaches near full scale");
    }

    [Test]
    public void EachChannelLosesItsOwnFloorButTheColourRatioSurvives() {
        // the field levels: blue background far above red and green, planet
        // brighter in red than in blue
        var pixels = Cube(Plane(13900, 15500), Plane(12800, 14200), Plane(19200, 20000));
        var (floor, gain) = PlanetaryFrames.NormaliseLevels(pixels, 3, N);

        Assert.That(floor[0], Is.EqualTo(13900).Within(1));
        Assert.That(floor[1], Is.EqualTo(12800).Within(1));
        Assert.That(floor[2], Is.EqualTo(19200).Within(1));

        for (int c = 0; c < 3; c++) {
            Assert.That(pixels[c * N], Is.EqualTo(0), $"channel {c} background is neutralised");
        }
        int mid = 15 * W + 15;
        double r = pixels[mid], g = pixels[N + mid], b = pixels[2 * N + mid];
        // signals were 1600 / 1400 / 800 before, one common gain keeps those ratios
        Assert.That(g / r, Is.EqualTo(1400.0 / 1600.0).Within(0.02));
        Assert.That(b / r, Is.EqualTo(800.0 / 1600.0).Within(0.02));
        Assert.That(gain, Is.EqualTo(65535 * 0.92 / 1600).Within(0.5));
    }

    [Test]
    public void AFrameThatAlreadyUsesTheRangeIsLeftAlone() {
        var pixels = Cube(Plane(120, 62000));
        var before = (ushort[])pixels.Clone();
        var (_, gain) = PlanetaryFrames.NormaliseLevels(pixels, 1, N);
        Assert.That(gain, Is.EqualTo(1.0));
        Assert.That(pixels, Is.EqualTo(before));
    }

    [Test]
    public void AFlatFrameIsNotAmplifiedIntoNoise() {
        var pixels = Cube(Plane(4000, 4000));
        var before = (ushort[])pixels.Clone();
        var (_, gain) = PlanetaryFrames.NormaliseLevels(pixels, 1, N);
        Assert.That(gain, Is.EqualTo(1.0));
        Assert.That(pixels, Is.EqualTo(before));
    }
}
