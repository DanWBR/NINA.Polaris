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
/// Pins the narrowband palette mapping + continuum subtraction math.
/// </summary>
[TestFixture]
public class NarrowbandCombineTests {

    [Test]
    public void Sho_MapsSiiToRed_HaToGreen_OiiiToBlue() {
        int w = 8, h = 8, plane = w * h;
        var ha = Flat(w, h, 10000);
        var oiii = Flat(w, h, 20000);
        var sii = Flat(w, h, 30000);

        var rgb = NarrowbandCombine.Compose(ha, oiii, sii, w, h, "sho", normalize: false);

        Assert.That(rgb[0], Is.EqualTo(30000), "R = SII");
        Assert.That(rgb[plane], Is.EqualTo(10000), "G = Ha");
        Assert.That(rgb[2 * plane], Is.EqualTo(20000), "B = OIII");
    }

    [Test]
    public void Hoo_UsesOiiiForBothGreenAndBlue() {
        int w = 8, h = 8, plane = w * h;
        var ha = Flat(w, h, 12000);
        var oiii = Flat(w, h, 24000);

        var rgb = NarrowbandCombine.Compose(ha, oiii, null, w, h, "hoo", normalize: false);

        Assert.That(rgb[0], Is.EqualTo(12000), "R = Ha");
        Assert.That(rgb[plane], Is.EqualTo(24000), "G = OIII");
        Assert.That(rgb[2 * plane], Is.EqualTo(24000), "B = OIII");
    }

    [Test]
    public void Normalize_MatchesChannelBackgrounds() {
        // Different medians in → roughly equal medians out (background match).
        int w = 32, h = 32;
        var ha = Flat(w, h, 5000);
        var oiii = Flat(w, h, 10000);
        var sii = Flat(w, h, 40000);

        var rgb = NarrowbandCombine.Compose(ha, oiii, sii, w, h, "sho", normalize: true);
        int plane = w * h;
        // All three channels now share the brightest channel's level (SII, 40000).
        Assert.That(rgb[0], Is.EqualTo(40000).Within(2));         // R=SII unchanged
        Assert.That(rgb[plane], Is.EqualTo(40000).Within(200));   // G=Ha scaled up
        Assert.That(rgb[2 * plane], Is.EqualTo(40000).Within(200));// B=OIII scaled up
    }

    [Test]
    public void Sho_MissingChannel_Throws() {
        var ha = Flat(4, 4, 1000);
        Assert.Throws<System.InvalidOperationException>(() =>
            NarrowbandCombine.Compose(ha, null, null, 4, 4, "sho", false));
    }

    // ---- continuum subtraction --------------------------------------

    [Test]
    public void Continuum_RemovesStars_KeepsNebulosity() {
        // NB = nebulosity everywhere + a star that also shows in continuum.
        int w = 32, h = 32;
        var nb = new ushort[w * h];
        var cont = new ushort[w * h];
        for (int i = 0; i < nb.Length; i++) { nb[i] = 4000; cont[i] = 200; } // faint bg
        int star = 10 * w + 10;
        nb[star] = 50000; cont[star] = 45000; // star: mostly continuum

        var outp = ContinuumSubtraction.Subtract(nb, cont, w, h, autoScale: true);

        Assert.That(outp[star], Is.LessThan((ushort)20000), "star should be strongly reduced");
        Assert.That(outp[0], Is.GreaterThan((ushort)3000), "faint nebulosity mostly kept");
    }

    [Test]
    public void Continuum_ManualScaleZero_IsIdentity() {
        int w = 8, h = 8;
        var nb = Flat(w, h, 12345);
        var cont = Flat(w, h, 5000);
        var outp = ContinuumSubtraction.Subtract(nb, cont, w, h, scale: 0.0, autoScale: false);
        Assert.That(outp, Is.EqualTo(nb));
    }

    private static ushort[] Flat(int w, int h, ushort v) {
        var d = new ushort[w * h];
        System.Array.Fill(d, v);
        return d;
    }
}
