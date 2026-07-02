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
/// Pins the OSC dual-band line extraction: red plane is the red line
/// (Ha/SII), OIII is the mean of green + blue.
/// </summary>
[TestFixture]
public class NarrowbandExtractTests {

    [Test]
    public void Extract_RedLineIsRedPlane_OiiiIsMeanOfGreenBlue() {
        int w = 4, h = 4, plane = w * h;
        var rgb = new ushort[plane * 3];
        for (int i = 0; i < plane; i++) {
            rgb[i] = 5000;                 // R -> red line
            rgb[plane + i] = 8000;         // G
            rgb[plane * 2 + i] = 12000;    // B
        }

        var (redLine, oiii) = NarrowbandExtract.Extract(rgb, w, h);

        Assert.That(redLine[0], Is.EqualTo(5000), "red line = R plane");
        Assert.That(oiii[0], Is.EqualTo(10000), "OIII = (G+B)/2 = (8000+12000)/2");
    }

    [Test]
    public void Extract_RoundsOiiiHalfUp() {
        int w = 2, h = 2, plane = w * h;
        var rgb = new ushort[plane * 3];
        for (int i = 0; i < plane; i++) {
            rgb[i] = 1;
            rgb[plane + i] = 100;      // G
            rgb[plane * 2 + i] = 101;  // B -> (100+101)/2 = 100.5 -> 101 (half up)
        }

        var (_, oiii) = NarrowbandExtract.Extract(rgb, w, h);

        Assert.That(oiii[0], Is.EqualTo(101));
    }

    [Test]
    public void Extract_RejectsNonRgb() {
        var mono = new ushort[16]; // only 1 plane for a 4x4
        Assert.Throws<System.InvalidOperationException>(() =>
            NarrowbandExtract.Extract(mono, 4, 4));
    }

    [Test]
    public void Extract_PreservesPerPixelValues() {
        int w = 3, h = 1, plane = w * h;
        var rgb = new ushort[plane * 3];
        // distinct per-pixel reds so we know planes aren't swapped
        rgb[0] = 10; rgb[1] = 20; rgb[2] = 30;                       // R
        rgb[plane + 0] = 40; rgb[plane + 1] = 60; rgb[plane + 2] = 80;   // G
        rgb[plane * 2 + 0] = 40; rgb[plane * 2 + 1] = 60; rgb[plane * 2 + 2] = 80; // B

        var (redLine, oiii) = NarrowbandExtract.Extract(rgb, w, h);

        Assert.That(redLine, Is.EqualTo(new ushort[] { 10, 20, 30 }));
        Assert.That(oiii, Is.EqualTo(new ushort[] { 40, 60, 80 }));
    }
}
