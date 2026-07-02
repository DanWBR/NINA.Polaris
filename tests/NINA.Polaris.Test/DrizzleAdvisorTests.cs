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

/// <summary>Pins the drizzle scale recommendation from sampling + sub count.</summary>
[TestFixture]
public class DrizzleAdvisorTests {

    [Test]
    public void Undersampled_ManySubs_Recommends2x() {
        var a = DrizzleAdvisor.Recommend(1.5, 40);
        Assert.That(a.RecommendedScale, Is.EqualTo(2));
        Assert.That(a.Reason, Does.Contain("Undersampled"));
    }

    [Test]
    public void Undersampled_FewSubs_Recommends2xButWarns() {
        var a = DrizzleAdvisor.Recommend(1.4, 8);
        Assert.That(a.RecommendedScale, Is.EqualTo(2));
        Assert.That(a.Reason, Does.Contain("8 subs"));
    }

    [Test]
    public void Borderline_Recommends1x() {
        var a = DrizzleAdvisor.Recommend(2.3, 50);
        Assert.That(a.RecommendedScale, Is.EqualTo(1));
        Assert.That(a.Reason, Does.Contain("Borderline"));
    }

    [Test]
    public void WellSampled_Recommends1x() {
        var a = DrizzleAdvisor.Recommend(3.5, 50);
        Assert.That(a.RecommendedScale, Is.EqualTo(1));
        Assert.That(a.Reason, Does.Contain("Well sampled"));
    }

    [Test]
    public void NoMeasurement_Defaults1x() {
        var a = DrizzleAdvisor.Recommend(0, 30);
        Assert.That(a.RecommendedScale, Is.EqualTo(1));
    }

    [Test]
    public void FwhmFromHfr_IsTwiceHfr() {
        Assert.That(DrizzleAdvisor.FwhmFromHfr(0.9), Is.EqualTo(1.8).Within(1e-9));
    }
}
