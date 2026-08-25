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

using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

[TestFixture]
public class SubExposureCalculatorTests {
    // Reference: readNoise 3 e-, sky 5 e-/px/s, p = 5%.
    //   denom = 5 · ((1.05)² − 1) = 5 · 0.1025 = 0.5125
    //   optimal = 3² / 0.5125 = 9 / 0.5125 = 17.5610 s
    [Test]
    public void Recommend_KnownInputs_MatchesSwampFormula() {
        var r = SubExposureCalculator.Recommend(readNoiseE: 3.0, skyRateEPerSec: 5.0);
        Assert.That(r, Is.Not.Null);
        Assert.That(r!.OptimalSeconds, Is.EqualTo(17.5610).Within(0.001));
        Assert.That(r.RecommendedSeconds, Is.EqualTo(17.5610).Within(0.001));
        Assert.That(r.SaturationCapSeconds, Is.Null);
        Assert.That(r.SaturationLimited, Is.False);
    }

    [Test]
    public void Recommend_SaturationCap_PullsDownAndFlags() {
        // Same optimal (~17.56 s), but the brightest pixel (20000 e-/s) hits a
        // 50000 e- full well at 2.5 s, so the cap wins.
        var r = SubExposureCalculator.Recommend(
            readNoiseE: 3.0, skyRateEPerSec: 5.0,
            fullWellE: 50000, peakRateEPerSec: 20000);
        Assert.That(r, Is.Not.Null);
        Assert.That(r!.OptimalSeconds, Is.EqualTo(17.5610).Within(0.001));
        Assert.That(r.SaturationCapSeconds, Is.EqualTo(2.5).Within(1e-9));
        Assert.That(r.RecommendedSeconds, Is.EqualTo(2.5).Within(1e-9));
        Assert.That(r.SaturationLimited, Is.True);
    }

    [Test]
    public void Recommend_SaturationCapAboveOptimal_DoesNotLimit() {
        // Full well reached only at 100 s, well past the 17.56 s optimal.
        var r = SubExposureCalculator.Recommend(
            readNoiseE: 3.0, skyRateEPerSec: 5.0,
            fullWellE: 50000, peakRateEPerSec: 500);
        Assert.That(r!.RecommendedSeconds, Is.EqualTo(17.5610).Within(0.001));
        Assert.That(r.SaturationLimited, Is.False);
    }

    [TestCase(0.0, 5.0)]
    [TestCase(-3.0, 5.0)]
    [TestCase(3.0, 0.0)]
    [TestCase(3.0, -5.0)]
    [TestCase(double.NaN, 5.0)]
    [TestCase(3.0, double.PositiveInfinity)]
    public void Recommend_NonPhysicalInputs_ReturnsNull(double readNoise, double skyRate) {
        Assert.That(SubExposureCalculator.Recommend(readNoise, skyRate), Is.Null);
    }

    [Test]
    public void Recommend_BrightSky_ClampsToMin() {
        // Very bright sky → sub-second optimal, clamped up to the 0.5 s floor.
        var r = SubExposureCalculator.Recommend(readNoiseE: 1.0, skyRateEPerSec: 100000.0);
        Assert.That(r, Is.Not.Null);
        Assert.That(r!.OptimalSeconds, Is.LessThan(0.5));
        Assert.That(r.RecommendedSeconds, Is.EqualTo(SubExposureCalculator.MinRecommendedSec));
    }

    [Test]
    public void Recommend_DarkSky_ClampsToMax() {
        // Extremely dark sky → huge optimal, clamped down to the 900 s ceiling.
        var r = SubExposureCalculator.Recommend(readNoiseE: 5.0, skyRateEPerSec: 0.001);
        Assert.That(r, Is.Not.Null);
        Assert.That(r!.OptimalSeconds, Is.GreaterThan(900.0));
        Assert.That(r.RecommendedSeconds, Is.EqualTo(SubExposureCalculator.MaxRecommendedSec));
    }

    [Test]
    public void Recommend_LargerNoiseTolerance_ShortensExposure() {
        var strict = SubExposureCalculator.Recommend(3.0, 5.0, allowedNoiseIncrease: 0.05);
        var loose = SubExposureCalculator.Recommend(3.0, 5.0, allowedNoiseIncrease: 0.20);
        Assert.That(loose!.OptimalSeconds, Is.LessThan(strict!.OptimalSeconds));
    }

    [Test]
    public void Recommend_NonPositiveNoiseIncrease_FallsBackToDefault() {
        var def = SubExposureCalculator.Recommend(3.0, 5.0, allowedNoiseIncrease: 0.05);
        var zero = SubExposureCalculator.Recommend(3.0, 5.0, allowedNoiseIncrease: 0.0);
        Assert.That(zero!.OptimalSeconds, Is.EqualTo(def!.OptimalSeconds).Within(1e-9));
    }

    // SkyRateEPerSec: (background − bias) · e-/ADU / exposure.
    [Test]
    public void SkyRate_KnownInputs() {
        // (1000 − 200) · 0.5 / 10 = 40 e-/px/s
        Assert.That(SubExposureCalculator.SkyRateEPerSec(1000, 200, 0.5, 10),
                    Is.EqualTo(40.0).Within(1e-9));
    }

    [Test]
    public void SkyRate_ZeroExposure_IsZero() {
        Assert.That(SubExposureCalculator.SkyRateEPerSec(1000, 200, 0.5, 0), Is.EqualTo(0));
    }

    [Test]
    public void SkyRate_BackgroundBelowBias_ClampsToZero() {
        Assert.That(SubExposureCalculator.SkyRateEPerSec(100, 200, 0.5, 10), Is.EqualTo(0));
    }
}
