// N.I.N.A. Polaris
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT
// ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or
// FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License
// for more details. You should have received a copy of the license along with
// this program. If not, see <https://www.gnu.org/licenses/>.

using System;
using NUnit.Framework;
using NINA.Polaris.Services.Planetary;

namespace NINA.Polaris.Test;

/// <summary>Metering and compositing for the planet + moons capture, on
/// synthetic frames: a disc with a band across it, point-like moons, a sky
/// level and a little noise. Brightness scales with exposure; everything
/// clips at 16-bit full scale like a real frame.</summary>
[TestFixture]
public class PlanetMoonsMathTests {
    private const int W = 320, H = 240;
    private const double Cx = 160, Cy = 120, R = 30;
    private static readonly (double X, double Y, double Rel)[] Moons = {
        (60, 110, 0.020), (250, 128, 0.012), (290, 116, 0.006),
    };

    /// <summary>Planet surface brightness per ms is 10000 ADU, so ~4.9 ms
    /// puts the disc at 75%. Moons are fractions of that, as points.</summary>
    private static ushort[] Frame(double exposureMs, double shiftX = 0, double shiftY = 0,
                                  int seed = 1, bool hotPixel = false) {
        var rnd = new Random(seed);
        var f = new ushort[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++) {
                double v = 800 + rnd.NextDouble() * 40;
                double dx = x - (Cx + shiftX), dy = y - (Cy + shiftY);
                double r = Math.Sqrt(dx * dx + dy * dy);
                if (r < R) {
                    double band = Math.Abs(dy) < 6 ? 0.7 : 1.0;   // dark belt across the disc
                    double limb = Math.Sqrt(1 - (r / R) * (r / R)) * 0.4 + 0.6;
                    v += 10000 * exposureMs * band * limb;
                }
                foreach (var m in Moons) {
                    double mx = x - (m.X + shiftX), my = y - (m.Y + shiftY);
                    v += 10000 * exposureMs * m.Rel * 40 * Math.Exp(-(mx * mx + my * my) / (2 * 1.5 * 1.5));
                }
                f[y * W + x] = (ushort)Math.Clamp(Math.Round(v), 0, 65535);
            }
        if (hotPixel) f[30 * W + 30] = 60000;
        return f;
    }

    private static (float[] P, int W, int H) Binned(ushort[] f) => PlanetMoonsMath.Bin2(f, W, H);

    [Test]
    public void ReadDisc_FindsTheCentreAndRadius() {
        var (p, w, h) = Binned(Frame(4));
        var d = PlanetMoonsMath.ReadDisc(p, w, h);
        Assert.That(d.Found, Is.True);
        Assert.That(d.CenterX, Is.EqualTo(Cx / 2).Within(1.0));
        Assert.That(d.CenterY, Is.EqualTo(Cy / 2).Within(1.0));
        Assert.That(d.Radius, Is.EqualTo(R / 2).Within(2.0));
        Assert.That(d.SaturatedFraction, Is.EqualTo(0));
    }

    [Test]
    public void ReadDisc_EmptySky_FindsNothing() {
        var rnd = new Random(3);
        var f = new ushort[W * H];
        for (int i = 0; i < f.Length; i++) f[i] = (ushort)(800 + rnd.Next(40));
        var (p, w, h) = Binned(f);
        Assert.That(PlanetMoonsMath.ReadDisc(p, w, h).Found, Is.False);
    }

    [Test]
    public void ReadDisc_SaturatedDisc_IsReported() {
        var (p, w, h) = Binned(Frame(60));
        var d = PlanetMoonsMath.ReadDisc(p, w, h);
        Assert.That(d.Found, Is.True);
        Assert.That(d.SaturatedFraction, Is.GreaterThan(0.5));
    }

    [Test]
    public void ReadMoons_ShortExposure_SeesNone() {
        var (p, w, h) = Binned(Frame(4));
        var d = PlanetMoonsMath.ReadDisc(p, w, h);
        // At 4 ms the brightest moon peaks around 800 ADU over the sky.
        var m = PlanetMoonsMath.ReadMoons(p, w, h, d);
        Assert.That(m.Signal, Is.LessThan(0.05 * PlanetMoonsMath.FullScale));
    }

    [Test]
    public void ReadMoons_LongExposure_SeesThemAndSkipsAHotPixel() {
        var (p, w, h) = Binned(Frame(60, hotPixel: true));
        var d = PlanetMoonsMath.ReadDisc(p, w, h);
        var m = PlanetMoonsMath.ReadMoons(p, w, h, d);
        Assert.That(m.Found, Is.True);
        Assert.That(m.Count, Is.EqualTo(3));
        Assert.That(m.Signal, Is.GreaterThan(0.1 * PlanetMoonsMath.FullScale));
    }

    [Test]
    public void NextExposure_StepsTowardsTheTargetWithinBounds() {
        Assert.That(PlanetMoonsMath.NextExposure(0.01, 1000, 4000, false, 0.0001, 1), Is.EqualTo(0.04).Within(1e-9));
        Assert.That(PlanetMoonsMath.NextExposure(0.01, 100, 40000, false, 0.0001, 1), Is.EqualTo(0.04).Within(1e-9));
        Assert.That(PlanetMoonsMath.NextExposure(0.01, 60000, 40000, true, 0.0001, 1), Is.EqualTo(0.005).Within(1e-9));
        Assert.That(PlanetMoonsMath.NextExposure(0.5, 100, 40000, false, 0.0001, 1), Is.EqualTo(1));
    }

    [Test]
    public void Metering_ConvergesOnTheSyntheticPlanet() {
        double exp = 1;   // ms
        for (int i = 0; i < 14; i++) {
            var (p, w, h) = Binned(Frame(exp, seed: i));
            var d = PlanetMoonsMath.ReadDisc(p, w, h);
            double target = 0.75 * (PlanetMoonsMath.FullScale - d.Background);
            bool sat = d.SaturatedFraction > 0.002;
            if (!sat && PlanetMoonsMath.Converged(d.Signal, target, 0.12)) break;
            exp = PlanetMoonsMath.NextExposure(exp / 1000, d.Signal, target, sat, 0.0001, 0.1) * 1000;
        }
        Assert.That(exp, Is.InRange(4.3, 5.5), "~4.9 ms puts the disc at 75% of the room above the sky");
    }

    private static PlanetMoonsMath.Plane16 Plane(ushort[] f) => new(f, W, H, 1);

    [Test]
    public void Composite_KeepsThePlanetDetailAndTheMoons() {
        var planet = Frame(6);
        var moons = Frame(150, shiftX: 3, shiftY: -2, seed: 7);   // drifted 3,-2 px between the recordings
        var c = PlanetMoonsMath.Composite(Plane(planet), Plane(moons));

        Assert.That(c.ShiftX, Is.EqualTo(-3).Within(0.6));
        Assert.That(c.ShiftY, Is.EqualTo(2).Within(0.6));

        float At(double x, double y) => c.Rgb[(int)Math.Round(y) * W + (int)Math.Round(x)];
        // The belt is darker than the zone next to it: detail from the short stack.
        Assert.That(At(Cx, Cy), Is.LessThan(At(Cx, Cy - 15) * 0.85));
        Assert.That(At(Cx, Cy - 15), Is.InRange(0.5, 1.0));
        // Each moon is visible at its position in the planet frame.
        foreach (var m in Moons)
            Assert.That(At(m.X, m.Y), Is.GreaterThan(0.1), $"moon at {m.X},{m.Y}");
        // Sky stays dark.
        Assert.That(At(20, 220), Is.LessThan(0.05));
    }

    [Test]
    public void Composite_DifferentSizes_AreRefused() {
        var a = new PlanetMoonsMath.Plane16(new ushort[10 * 10], 10, 10, 1);
        var b = new PlanetMoonsMath.Plane16(new ushort[12 * 10], 12, 10, 1);
        Assert.Throws<InvalidOperationException>(() => PlanetMoonsMath.Composite(a, b));
    }

    [Test]
    public void LargestComponent_KeepsOnlyTheBiggestBlob() {
        var above = new bool[10 * 10];
        for (int y = 2; y < 6; y++) for (int x = 2; x < 6; x++) above[y * 10 + x] = true;   // 16 px
        above[8 * 10 + 8] = true; above[8 * 10 + 9] = true;                                  // 2 px
        var o = PlanetMoonsMath.LargestComponent(above, 10, 10);
        Assert.That(o[3 * 10 + 3], Is.True);
        Assert.That(o[8 * 10 + 8], Is.False);
    }
}
