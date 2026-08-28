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

namespace NINA.Polaris.Test.Timelapse;

// asinh is the auto-HDR tone curve: concave through (0,0)-(1,1), so it lifts
// shadows/midtones ABOVE the linear map and compresses highlights (shrinking
// increments toward white). That's what shows an eclipse corona and disc at once.
[TestFixture]
public class AutoStretchAsinhTests {

    [Test]
    public void Asinh_IsMonotonic_LiftsMidtones_AndCompressesHighlights() {
        // Five equally-spaced inputs across the full 16-bit range (black=0..white=1).
        var data = new ushort[] { 0, 16383, 32767, 49151, 65535 };
        var outp = AutoStretch.ApplyAsinh(data, data.Length, 1, black: 0.0, white: 1.0,
                                          bitDepth: 16, strength: 8.0);

        // Endpoints are pinned.
        Assert.That(outp[0], Is.EqualTo(0));
        Assert.That(outp[4], Is.EqualTo(255));

        // Strictly increasing.
        for (int i = 1; i < outp.Length; i++)
            Assert.That(outp[i], Is.GreaterThan(outp[i - 1]), $"monotonic at {i}");

        // Midtone lift: the middle input maps well above the linear 127.
        Assert.That(outp[2], Is.GreaterThan(127), "midtone lifted above linear");

        // Concave: increments shrink from shadows to highlights (highlight compression).
        int d0 = outp[1] - outp[0];
        int d1 = outp[2] - outp[1];
        int d2 = outp[3] - outp[2];
        int d3 = outp[4] - outp[3];
        Assert.That(d0, Is.GreaterThan(d1));
        Assert.That(d1, Is.GreaterThan(d2));
        Assert.That(d2, Is.GreaterThan(d3));
    }

    [Test]
    public void Asinh_AutoParams_RunsAndStaysClamped() {
        // A high-dynamic-range frame: a dim background with a few blazing pixels.
        int w = 64, h = 64;
        var data = new ushort[w * h];
        for (int i = 0; i < data.Length; i++) data[i] = (ushort)(200 + (i % 50));
        data[10] = 65535; data[2000] = 60000; data[3000] = 50000;

        var outp = AutoStretch.ApplyAsinh(data, w, h);   // auto black/white
        Assert.That(outp.Length, Is.EqualTo(w * h));
        foreach (var b in outp) Assert.That(b, Is.InRange((byte)0, (byte)255));
        // The blazing pixels land at or near white; the dim floor stays low.
        Assert.That(outp[10], Is.GreaterThan(outp[0]));
    }
}
