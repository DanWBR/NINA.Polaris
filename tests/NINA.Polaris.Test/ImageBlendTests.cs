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

using NUnit.Framework;
using NINA.Image.ImageAnalysis;

namespace NINA.Polaris.Test;

/// <summary>
/// Pins the star-recombination math (the "Image Blend" tool): the float MTF
/// stretch, the Screen/Add/Lighten + opacity blend, and the stars-from-starless
/// derivation. These are the correctness core of the starless workflow.
/// </summary>
[TestFixture]
public class ImageBlendTests {

    private const double Eps = 1e-6;

    // ---- ApplyManualFloat: parity with the 8-bit ApplyManual LUT ----------

    [Test]
    public void ApplyManualFloat_MatchesEightBitLut_WhenQuantised() {
        var data = new ushort[2048];
        for (int i = 0; i < data.Length; i++) data[i] = (ushort)((i * 65535) / (data.Length - 1));

        // Identity-ish stretch with a shadow lift so the curve is non-trivial.
        double black = 0.1, mid = 0.3, white = 0.95;
        var lut8 = AutoStretch.ApplyManual(data, data.Length, 1, black, mid, white);
        var flt = AutoStretch.ApplyManualFloat(data, black, mid, white);

        for (int i = 0; i < data.Length; i++) {
            // The float path uses the same MTF; quantising it to 8-bit the same
            // way the LUT does must land within one level.
            byte q = (byte)(flt[i] * 255f);
            Assert.That(System.Math.Abs(q - lut8[i]), Is.LessThanOrEqualTo(1),
                $"mismatch at idx {i}: float {flt[i]} -> {q} vs lut {lut8[i]}");
        }
    }

    [Test]
    public void ApplyManualFloat_Monotonic_NonDecreasing() {
        var data = new ushort[256];
        for (int i = 0; i < data.Length; i++) data[i] = (ushort)(i * 257); // 0..65535
        var flt = AutoStretch.ApplyManualFloat(data, 0.0, 0.4, 1.0);
        for (int i = 1; i < flt.Length; i++)
            Assert.That(flt[i], Is.GreaterThanOrEqualTo(flt[i - 1]));
        Assert.That(flt[0], Is.EqualTo(0f).Within(Eps));
        Assert.That(flt[^1], Is.EqualTo(1f).Within(1e-3));
    }

    // ---- Blend modes ------------------------------------------------------

    private static ushort[] Const(int n, ushort v) {
        var a = new ushort[n];
        for (int i = 0; i < n; i++) a[i] = v;
        return a;
    }

    private static readonly ImageBlend.StretchSpec Linear = ImageBlend.StretchSpec.Identity;

    [Test]
    public void Screen_TwoMidGreys_BrightensPerFormula() {
        // a = b = 0.5 (linear). screen = 1-(1-0.5)^2 = 0.75 → 49151.
        var a = Const(16, 32768);
        var b = Const(16, 32768);
        var outp = ImageBlend.Combine(a, b, Linear, Linear, ImageBlend.Mode.Screen, 1.0);
        foreach (var v in outp) Assert.That(v, Is.EqualTo(49151).Within(2));
    }

    [Test]
    public void Add_ClampsToWhite() {
        var a = Const(8, 45000);
        var b = Const(8, 40000);            // 0.686 + 0.610 > 1 → clamp to white
        var outp = ImageBlend.Combine(a, b, Linear, Linear, ImageBlend.Mode.Add, 1.0);
        foreach (var v in outp) Assert.That(v, Is.EqualTo(65535));
    }

    [Test]
    public void Lighten_KeepsBrighter() {
        var a = Const(8, 20000);
        var b = Const(8, 50000);
        var outp = ImageBlend.Combine(a, b, Linear, Linear, ImageBlend.Mode.Lighten, 1.0);
        foreach (var v in outp) Assert.That(v, Is.EqualTo(50000).Within(2));
    }

    [Test]
    public void Opacity_Zero_ReturnsBaseUnchanged() {
        var a = Const(8, 20000);
        var b = Const(8, 60000);
        var outp = ImageBlend.Combine(a, b, Linear, Linear, ImageBlend.Mode.Screen, 0.0);
        foreach (var v in outp) Assert.That(v, Is.EqualTo(20000).Within(2)); // == base
    }

    [Test]
    public void Opacity_Half_MixesBaseAndBlend() {
        var a = Const(8, 32768);   // 0.5
        var b = Const(8, 32768);   // 0.5
        // screen=0.75; out = 0.5*0.5 + 0.75*0.5 = 0.625 → 40959
        var outp = ImageBlend.Combine(a, b, Linear, Linear, ImageBlend.Mode.Screen, 0.5);
        foreach (var v in outp) Assert.That(v, Is.EqualTo(40959).Within(2));
    }

    [Test]
    public void Combine_LengthMismatch_Throws() {
        Assert.Throws<System.ArgumentException>(() =>
            ImageBlend.Combine(Const(8, 0), Const(4, 0), Linear, Linear, ImageBlend.Mode.Screen, 1.0));
    }

    [Test]
    public void ParseMode_DefaultsToScreen() {
        Assert.That(ImageBlend.ParseMode(null), Is.EqualTo(ImageBlend.Mode.Screen));
        Assert.That(ImageBlend.ParseMode("ADD"), Is.EqualTo(ImageBlend.Mode.Add));
        Assert.That(ImageBlend.ParseMode("lighten"), Is.EqualTo(ImageBlend.Mode.Lighten));
        Assert.That(ImageBlend.ParseMode("garbage"), Is.EqualTo(ImageBlend.Mode.Screen));
    }

    // ---- DeriveStars ------------------------------------------------------

    [Test]
    public void DeriveStars_ClampsNegativeToZero() {
        var orig     = new ushort[] { 1000, 5000, 200, 65535, 0 };
        var starless = new ushort[] { 1200, 1000, 200, 0,     500 };
        var stars = ImageBlend.DeriveStars(orig, starless);
        Assert.That(stars, Is.EqualTo(new ushort[] { 0, 4000, 0, 65535, 0 }));
    }

    [Test]
    public void DeriveStars_LengthMismatch_Throws() {
        Assert.Throws<System.ArgumentException>(() =>
            ImageBlend.DeriveStars(new ushort[4], new ushort[5]));
    }
}
