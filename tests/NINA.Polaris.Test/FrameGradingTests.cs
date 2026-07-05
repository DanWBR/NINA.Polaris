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

using System.Collections.Generic;
using System.Linq;
using NINA.Polaris.Services.Studio;
using NUnit.Framework;

namespace NINA.Polaris.Test;

[TestFixture]
public class FrameGradingTests {

    private static FrameGrading.FrameMetric M(string name, int stars, double hfr, double ecc = 0.2)
        => new($"/lights/{name}", name, stars, hfr, ecc);

    [Test]
    public void Rank_OrdersBySharpnessThenStars_BestFirst() {
        var metrics = new List<FrameGrading.FrameMetric> {
            M("a", 400, 3.5),   // soft
            M("b", 500, 2.0),   // sharpest + most stars -> best
            M("c", 450, 2.6),
        };
        var ranked = FrameGrading.Rank(metrics);
        Assert.That(ranked[0].FileName, Is.EqualTo("b"));
        Assert.That(ranked[2].FileName, Is.EqualTo("a"));
        // Scores are normalised into 0..1, best is the reference.
        Assert.That(ranked[0].Score, Is.GreaterThan(ranked[1].Score));
        Assert.That(ranked[1].Score, Is.GreaterThan(ranked[2].Score));
    }

    [Test]
    public void Rank_JunkFrames_NeverKept_AndScoreZero() {
        var metrics = new List<FrameGrading.FrameMetric> {
            M("good", 500, 2.0),
            M("cloudy", 0, 0),          // no stars
            M("blank", 3, 0),           // hfr 0 -> invalid
        };
        var ranked = FrameGrading.Rank(metrics);
        var byName = ranked.ToDictionary(r => r.FileName);
        Assert.That(byName["cloudy"].Score, Is.EqualTo(0));
        Assert.That(byName["cloudy"].Keep, Is.False);
        Assert.That(byName["blank"].Score, Is.EqualTo(0));
        Assert.That(byName["blank"].Keep, Is.False);
        Assert.That(byName["good"].Keep, Is.True);
    }

    [Test]
    public void Rank_KeepBest_KeepsExactlyN_TheSharpest() {
        var metrics = new List<FrameGrading.FrameMetric> {
            M("a", 400, 2.0),
            M("b", 400, 2.2),
            M("c", 400, 2.4),
            M("d", 400, 3.0),
        };
        var ranked = FrameGrading.Rank(metrics, keepBest: 2);
        var kept = FrameGrading.Selected(ranked);
        Assert.That(kept.Count, Is.EqualTo(2));
        Assert.That(kept, Does.Contain("/lights/a"));
        Assert.That(kept, Does.Contain("/lights/b"));
    }

    [Test]
    public void Rank_HfrTolerance_KeepsWithinBand() {
        // best HFR = 2.0; 10% band -> keep <= 2.2
        var metrics = new List<FrameGrading.FrameMetric> {
            M("a", 400, 2.0),
            M("b", 400, 2.1),
            M("c", 400, 2.5),   // outside band
        };
        var ranked = FrameGrading.Rank(metrics, hfrTolerancePct: 10);
        var kept = FrameGrading.Selected(ranked).ToHashSet();
        Assert.That(kept, Does.Contain("/lights/a"));
        Assert.That(kept, Does.Contain("/lights/b"));
        Assert.That(kept, Does.Not.Contain("/lights/c"));
    }

    [Test]
    public void Rank_Default_DropsCloudyLowStarSubs() {
        // Sharp, star-rich subs plus one sharp-but-cloudy sub (few stars).
        var metrics = new List<FrameGrading.FrameMetric> {
            M("a", 500, 2.0),
            M("b", 480, 2.1),
            M("c", 520, 2.05),
            M("haze", 40, 2.0),   // sharp but < half the median star count
        };
        var ranked = FrameGrading.Rank(metrics);
        var kept = FrameGrading.Selected(ranked).ToHashSet();
        Assert.That(kept, Does.Contain("/lights/a"));
        Assert.That(kept, Does.Not.Contain("/lights/haze"),
            "a low-star (cloudy) sub should be rejected by the default rule");
    }

    [Test]
    public void Rank_Empty_ReturnsEmpty() {
        var ranked = FrameGrading.Rank(new List<FrameGrading.FrameMetric>());
        Assert.That(ranked, Is.Empty);
        Assert.That(FrameGrading.Selected(ranked), Is.Empty);
    }
}
