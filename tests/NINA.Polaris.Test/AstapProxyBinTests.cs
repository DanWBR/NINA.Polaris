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
using NINA.Polaris.Services;
using NINA.Polaris.Services.PlateSolving;

namespace NINA.Polaris.Test;

/// <summary>The 2x2-binned proxy a big colour stack is solved from: when it
/// applies, what the binned plane holds, and how the solve maps back onto the
/// original pixel grid.</summary>
[TestFixture]
public class AstapProxyBinTests {

    [Test]
    public void BinFactor_BigOversampledFrameBins_SmallOrCoarseDoesNot() {
        Assert.That(AstapSolver.ProxyBinFactor(6248, 4176, 1.14), Is.EqualTo(2), "26 MP at 1.14 arcsec/px");
        Assert.That(AstapSolver.ProxyBinFactor(4144, 2822, 1.79), Is.EqualTo(1), "11.7 MP is below the size where it pays");
        Assert.That(AstapSolver.ProxyBinFactor(6248, 4176, 2.75), Is.EqualTo(1), "binning would pass 4 arcsec/px and lose the stars");
        Assert.That(AstapSolver.ProxyBinFactor(6248, 4176, 0), Is.EqualTo(2), "unknown scale on a 26 MP sensor: bin");
        Assert.That(AstapSolver.ProxyBinFactor(4144, 3000, 0), Is.EqualTo(1), "unknown scale on 12 MP: leave it");
    }

    [Test]
    public void BinPlane_AveragesEachBlock_AndReadsTheRightPlane() {
        int w = 4, h = 2;
        // plane 0 is noise; plane 1 (offset 8) is the one we bin
        var data = new ushort[] {
            9, 9, 9, 9,  9, 9, 9, 9,
            10, 20, 30, 40,
            50, 60, 70, 80,
        };
        var b = AstapSolver.BinPlane2x2(data, 8, w, h);
        Assert.That(b, Is.EqualTo(new ushort[] { 35, 55 }));
    }

    [Test]
    public void UnbinResult_ScalesBackToTheFullFrame() {
        var r = new PlateSolveResult {
            Success = true, RaHours = 5.5, DecDeg = -5.4, RotationDeg = 13.6,
            ScaleArcsecPerPixel = 2.28,
            CD11 = -0.0006, CD12 = 0.0001, CD21 = 0.0001, CD22 = 0.0006,
            CrPix1 = 1562.5, CrPix2 = 1044.5,
        };
        AstapSolver.UnbinResult(r, 2);
        Assert.That(r.RaHours, Is.EqualTo(5.5));
        Assert.That(r.RotationDeg, Is.EqualTo(13.6));
        Assert.That(r.ScaleArcsecPerPixel, Is.EqualTo(1.14).Within(1e-9));
        Assert.That(r.CD11, Is.EqualTo(-0.0003).Within(1e-12));
        Assert.That(r.CD22, Is.EqualTo(0.0003).Within(1e-12));
        // binned pixel 1562.5 (the centre of a 3124-wide proxy) is the centre of the 6248-wide original
        Assert.That(r.CrPix1, Is.EqualTo(3124.5).Within(1e-9));
        Assert.That(r.CrPix2, Is.EqualTo(2088.5).Within(1e-9));
    }
}
