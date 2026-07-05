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
using NINA.Polaris.Services.Studio;

namespace NINA.Polaris.Test.Studio;

/// <summary>
/// Pins the white-balance summary fit used by the PCC/SPCC result plot: the
/// least-squares slope/intercept and the 3σ outlier rejection.
/// </summary>
[TestFixture]
public class WhiteBalanceFitTests {

    [Test]
    public void FitChannel_PerfectLine_RecoversSlopeInterceptZeroSigma() {
        // y = 0.3 + 1.5·x exactly.
        var x = new List<double>();
        var y = new List<double>();
        for (int i = 1; i <= 50; i++) { x.Add(i * 0.02); y.Add(0.3 + 1.5 * (i * 0.02)); }

        var ch = WhiteBalanceFit.FitChannel(x, y);
        Assert.That(ch.Fit.Slope, Is.EqualTo(1.5).Within(1e-6));
        Assert.That(ch.Fit.Intercept, Is.EqualTo(0.3).Within(1e-6));
        Assert.That(ch.Fit.Sigma, Is.EqualTo(0).Within(1e-6));
        Assert.That(ch.Fit.NStars, Is.EqualTo(50));
        Assert.That(ch.Fit.NOutliers, Is.EqualTo(0));
    }

    [Test]
    public void FitChannel_GrossOutlier_IsRejected_AndSlopeStaysClean() {
        // A clean y = x line with one wild point that must be clipped so it
        // doesn't drag the slope (the reason PCC/SPCC report "removed N
        // outliers").
        var x = new List<double>();
        var y = new List<double>();
        for (int i = 1; i <= 60; i++) { x.Add(i * 0.01); y.Add(i * 0.01); }
        y[30] = 5.0;   // gross outlier

        var ch = WhiteBalanceFit.FitChannel(x, y);
        Assert.That(ch.Fit.NOutliers, Is.GreaterThanOrEqualTo(1),
            "The gross outlier should be clipped.");
        Assert.That(ch.Fit.Slope, Is.EqualTo(1.0).Within(0.05),
            "Slope must stay ~1 after rejecting the outlier.");
        Assert.That(ch.Fit.Intercept, Is.EqualTo(0.0).Within(0.05));
    }

    [Test]
    public void Build_ReturnsBothPanels_AndCarriesGainsAndLabels() {
        var cat = new List<double> { 0.5, 0.6, 0.7, 0.8, 0.9 };
        var img = new List<double> { 0.5, 0.6, 0.7, 0.8, 0.9 };
        var s = WhiteBalanceFit.Build(cat, img, cat, img,
            gainR: 0.9, gainG: 1.0, gainB: 0.8,
            reference: "G2V", method: "SPCC", stars: 5);

        Assert.That(s.Bg, Is.Not.Null);
        Assert.That(s.Rg, Is.Not.Null);
        Assert.That(s.Bg.CatX.Length, Is.EqualTo(5));
        Assert.That(s.GainR, Is.EqualTo(0.9).Within(1e-9));
        Assert.That(s.GainB, Is.EqualTo(0.8).Within(1e-9));
        Assert.That(s.Reference, Is.EqualTo("G2V"));
        Assert.That(s.Method, Is.EqualTo("SPCC"));
        Assert.That(s.Stars, Is.EqualTo(5));
    }
}
