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

using System.IO;
using System.Runtime.CompilerServices;
using NINA.Polaris.Services.PlateSolving;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// The geometry a polar alignment solve hands the solver.
///
/// The polar path used to pass a pixel scale and nothing else: no field of
/// view, and a temporary FITS with no FOCALLEN in it, so ASTAP could neither
/// be told the scale nor derive it. It guesses in that case, and a guess that
/// is far enough out makes it widen the search until it asks for a star
/// database covering a field nobody photographs. Reported from a TPPA refresh:
/// a 9.5 degree guess, widened to 10 and 24 degrees, ending in "no star
/// database found" on a host carrying V50 and D20 that had solved three points
/// minutes earlier.
///
/// The same helper the SKY tab's solve already used fixes it, which is the
/// point of these: the two paths now answer the same.
/// </summary>
[TestFixture]
public class PolarSolveHintTests {

    private static string Source() {
        var here = Path.GetDirectoryName(Here())!;
        var path = Path.GetFullPath(Path.Combine(here, "..", "..",
            "src", "NINA.Polaris", "Services", "PolarAlignmentService.cs"));
        Assert.That(File.Exists(path), $"nao achei {path}");
        return File.ReadAllText(path);
    }

    private static string Here([CallerFilePath] string p = "") => p;

    [Test]
    public void ThePolarSolveUsesTheSharedGeometryHelper() {
        var src = Source();

        Assert.Multiple(() => {
            Assert.That(src, Does.Contain("PlateSolveHints.From("),
                "the field of view and the scale come from one place now");
            Assert.That(src, Does.Contain("PlateSolveHints.Apply("));
        });
    }

    /// <summary>The keyword matters on its own: with FOCALLEN present a solver
    /// derives the scale even when no hint reaches it.</summary>
    [Test]
    public void TheTemporaryFitsCarriesTheFocalLength() {
        Assert.That(Source(), Does.Contain("PlateSolveHints.StampFocalLength("));
    }

    [Test]
    public void TheOldLocalScaleComputationIsGone() {
        Assert.That(Source(), Does.Not.Contain("private double ComputePixelScaleHint"),
            "two ways to compute the same hint is how they drift apart");
    }

    /// <summary>A guide scope at 200 mm with 3.75 um pixels over a 1080 pixel
    /// frame: about 3.9 arcsec per pixel and a bit over a degree of height.
    /// The numbers matter because ASTAP picks its database from them.</summary>
    [Test]
    public void TheGeometryIsTheOneASTAPNeeds() {
        var g = PlateSolveHints.From(focalLengthMm: 200, pixelSizeUm: 3.75,
                                     imageHeightPx: 1080, sensorHeightPx: 1080);

        Assert.Multiple(() => {
            Assert.That(g.ScaleArcsecPerPixel, Is.EqualTo(3.87).Within(0.02));
            Assert.That(g.FovDeg, Is.EqualTo(1.16).Within(0.02));
        });
    }

    /// <summary>With no focal length there is nothing to say, and saying
    /// nothing is right: a wrong field of view is worse than none, because it
    /// is what sends ASTAP looking for a database that is not there.</summary>
    [Test]
    public void WithNoFocalLengthNothingIsClaimed() {
        var g = PlateSolveHints.From(0, 3.75, 1080, 1080);

        Assert.Multiple(() => {
            Assert.That(g.FovDeg, Is.Zero);
            Assert.That(g.ScaleArcsecPerPixel, Is.Zero);
        });
    }
}
