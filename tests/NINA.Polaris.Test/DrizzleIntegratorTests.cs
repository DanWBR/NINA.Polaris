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

using NINA.Image.ImageAnalysis;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Pins the drizzle drop-deposit: identity reproduction, mean-combine,
/// output dims, surface-brightness preservation on upscale, and coverage.
/// </summary>
[TestFixture]
public class DrizzleIntegratorTests {

    private static ushort[] Flat(int w, int h, ushort v) {
        var d = new ushort[w * h];
        System.Array.Fill(d, v);
        return d;
    }

    [Test]
    public void Scale1_Identity_ReproducesInput() {
        int w = 6, h = 6;
        var img = new ushort[w * h];
        for (int i = 0; i < img.Length; i++) img[i] = (ushort)(1000 + i * 10);
        var dz = new DrizzleIntegrator(w, h, scale: 1, pixfrac: 1.0);
        dz.AddFrame(img, AffineTransform.Identity);
        var outp = dz.Result();
        Assert.That(outp.Length, Is.EqualTo(w * h));
        // pixfrac 1 + scale 1: each drop lands exactly on its own output pixel.
        Assert.That(outp, Is.EqualTo(img));
        Assert.That(dz.EmptyFraction(), Is.EqualTo(0).Within(1e-9));
    }

    [Test]
    public void Scale1_TwoFrames_AveragesValues() {
        int w = 4, h = 4;
        var dz = new DrizzleIntegrator(w, h, 1, 1.0);
        dz.AddFrame(Flat(w, h, 100), AffineTransform.Identity);
        dz.AddFrame(Flat(w, h, 200), AffineTransform.Identity);
        var outp = dz.Result();
        Assert.That(outp[0], Is.EqualTo(150).Within(1), "data/weight = (100+200)/2");
    }

    [Test]
    public void Scale2_OutputDimsDoubled() {
        int w = 8, h = 5;
        var dz = new DrizzleIntegrator(w, h, 2, 1.0);
        Assert.That(dz.OutW, Is.EqualTo(16));
        Assert.That(dz.OutH, Is.EqualTo(10));
    }

    [Test]
    public void Scale2_UniformInput_StaysUniform_FullCoverage() {
        int w = 8, h = 8;
        var dz = new DrizzleIntegrator(w, h, 2, 1.0);
        dz.AddFrame(Flat(w, h, 5000), AffineTransform.Identity);
        var outp = dz.Result();
        // Surface brightness preserved: a flat field upsamples to the same
        // flat value everywhere, with no coverage holes at pixfrac 1.
        Assert.That(dz.EmptyFraction(), Is.EqualTo(0).Within(1e-9));
        foreach (var p in outp) Assert.That(p, Is.EqualTo(5000).Within(1));
    }

    [Test]
    public void Scale3_SmallPixfrac_SingleFrame_LeavesCoverageHoles() {
        int w = 9, h = 9;
        // scale 3 + small pixfrac: a single un-dithered frame's drops are too
        // small to reach the outer sub-pixels of each 3x3 cell -> holes.
        // (Dithering across many subs fills them; that's the point of drizzle.)
        var dz = new DrizzleIntegrator(w, h, 3, 0.3);
        dz.AddFrame(Flat(w, h, 5000), AffineTransform.Identity);
        Assert.That(dz.EmptyFraction(), Is.GreaterThan(0.1));
    }

    [Test]
    public void Translation_ShiftsSignalOnGrid() {
        int w = 8, h = 8;
        var img = new ushort[w * h];
        img[2 * w + 2] = 40000; // a single bright pixel at (2,2)
        // cur->ref shift of +1 px in X: the reference position is x+1.
        var t = new AffineTransform { M00 = 1, M01 = 0, M10 = 0, M11 = 1, Tx = 1, Ty = 0 };
        var dz = new DrizzleIntegrator(w, h, 1, 1.0);
        dz.AddFrame(img, t);
        var outp = dz.Result();
        Assert.That(outp[2 * w + 3], Is.EqualTo(40000).Within(1), "moved to (3,2)");
        Assert.That(outp[2 * w + 2], Is.EqualTo(0), "original cell now empty");
    }
}
