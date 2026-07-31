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
using NINA.Polaris.Services.PlateSolving;
using System.Collections.Generic;
using System.Linq;
using static NINA.Polaris.Services.PlateSolving.SolverDatabaseAdvisor;

namespace NINA.Polaris.Test;

/// <summary>
/// The recommendation is only worth anything if it matches the publishers'
/// tables, so the fixtures below ARE those tables: ASTAP's FOV floors per
/// D-grade, and astrometry.net's skymark bands. The rig from the field report
/// (ASI715MC on an SV503 102) is used as the worked example.
/// </summary>
[TestFixture]
public class SolverDatabaseAdvisorTests {

    private static readonly List<AstapDatabase> Astap = new() {
        new("W08", "W08", 20.0, 180.0, 15_000_000, null, null),
        new("G05", "G05", 3.0, 20.0, 100_000_000, null, null),
        new("D05", "D05", 0.6, 6.0, 130_000_000, null, null),
        new("D20", "D20", 0.3, 6.0, 400_000_000, null, null),
        new("D50", "D50", 0.2, 6.0, 800_000_000, null, null),
        new("D80", "D80", 0.15, 6.0, 1_250_000_000, null, null),
    };

    private static readonly List<AstrometryScale> Scales = new() {
        new(0, 2.0, 2.8),      new(1, 2.8, 4.0),      new(2, 4.0, 5.6),
        new(3, 5.6, 8.0),      new(4, 8.0, 11.0),     new(5, 11.0, 16.0),
        new(6, 16.0, 22.0),    new(7, 22.0, 30.0),    new(8, 30.0, 42.0),
        new(9, 42.0, 60.0),    new(10, 60.0, 85.0),   new(11, 85.0, 120.0),
        new(12, 120.0, 170.0), new(13, 170.0, 240.0), new(14, 240.0, 340.0),
        new(15, 340.0, 480.0), new(16, 480.0, 680.0), new(17, 680.0, 1000.0),
        new(18, 1000.0, 1400.0), new(19, 1400.0, 2000.0),
    };

    [Test]
    public void Astap_PicksTheSmallestDatabaseThatCoversTheField() {
        // A one-degree field is covered by every D grade, so the 130 MB one is
        // the right answer: density above what the field needs only costs disk.
        Assert.That(RecommendAstap(Astap, 1.0)!.Id, Is.EqualTo("D05"));
        // Below D05's floor it has to step up, and so on down.
        Assert.That(RecommendAstap(Astap, 0.45)!.Id, Is.EqualTo("D20"));
        Assert.That(RecommendAstap(Astap, 0.25)!.Id, Is.EqualTo("D50"));
        Assert.That(RecommendAstap(Astap, 0.17)!.Id, Is.EqualTo("D80"));
    }

    [Test]
    public void Astap_WideFieldsGetTheWideDatabases() {
        Assert.That(RecommendAstap(Astap, 30.0)!.Id, Is.EqualTo("W08"));
        Assert.That(RecommendAstap(Astap, 8.0)!.Id, Is.EqualTo("G05"));
    }

    [Test]
    public void Astap_NarrowerThanEveryPublishedRange_HasNoAnswer() {
        // 0.1 degree is below D80's floor. Saying "D80" anyway would be a guess
        // dressed as advice; the UI should say the field is off the table.
        Assert.That(RecommendAstap(Astap, 0.10), Is.Null);
    }

    [Test]
    public void Astrometry_CoversTenPercentToAllOfTheField() {
        // The reported rig: 1.09 arcsec/px over 3864 px is a 1.17 degree field,
        // so the useful skymarks run from ~7 arcmin to ~70 arcmin.
        double fov = FovDegrees(1.09, 3864, 2192);
        Assert.That(fov, Is.EqualTo(1.17).Within(0.02));

        var picked = RecommendAstrometryScales(Scales, fov).Select(s => s.Scale).ToList();
        Assert.That(picked, Does.Contain(3), "the 5.6-8 arcmin band is the 10% end");
        Assert.That(picked, Does.Contain(10), "the 60-85 arcmin band straddles the full field");
        Assert.That(picked, Does.Not.Contain(0), "2 arcmin skymarks are far too small to be useful");
        Assert.That(picked, Does.Not.Contain(12), "and 120 arcmin ones do not fit in the frame");
        Assert.That(picked, Is.Ordered);
    }

    [Test]
    public void Astrometry_ANarrowFieldStillGetsTheSmallestBand() {
        // Off the bottom of the table: returning nothing would leave the solver
        // with no index at all, which fails worse than a slightly coarse one.
        var picked = RecommendAstrometryScales(Scales, 0.02);
        Assert.That(picked, Has.Count.EqualTo(1));
        Assert.That(picked[0].Scale, Is.EqualTo(0));
    }

    [Test]
    public void PixelScaleAndFov_MatchTheOpticsFormula() {
        // ASI715MC (1.45 um) on the SV503 102 at 714 mm.
        var scale = PixelScale(1.45, 714);
        Assert.That(scale, Is.EqualTo(0.419).Within(0.002));
        Assert.That(FovDegrees(scale, 3864, 2192), Is.EqualTo(0.45).Within(0.01));
        // Guards: no optics, no answer.
        Assert.That(PixelScale(0, 714), Is.EqualTo(0));
        Assert.That(FovDegrees(0.4, 0, 0), Is.EqualTo(0));
    }
}
