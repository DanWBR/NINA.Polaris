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
public class AnnotationProjectorTests {
    // 2"/px over 1000×800, centred at RA 6h, Dec +20°, no rotation/flip.
    const double Scale = 2.0;
    const int W = 1000, H = 800;
    const double Ra0 = 6.0, Dec0 = 20.0;

    [Test]
    public void Center_MapsToFrameCentre() {
        var p = AnnotationProjector.Project(Ra0, Dec0, Scale, 0, W, H, false, Ra0, Dec0);
        Assert.That(p, Is.Not.Null);
        Assert.That(p!.Value.x, Is.EqualTo(W / 2.0).Within(0.01));
        Assert.That(p.Value.y, Is.EqualTo(H / 2.0).Within(0.01));
    }

    [Test]
    public void North_IsUp() {
        // A point slightly north (higher Dec) lands above centre (smaller Y).
        var p = AnnotationProjector.Project(Ra0, Dec0, Scale, 0, W, H, false, Ra0, Dec0 + 0.1);
        Assert.That(p, Is.Not.Null);
        Assert.That(p!.Value.x, Is.EqualTo(W / 2.0).Within(0.5));
        Assert.That(p.Value.y, Is.LessThan(H / 2.0));
    }

    [Test]
    public void East_IsLeft_WhenNotFlipped() {
        // Higher RA = east; on a non-mirrored north-up image east is to the left.
        var p = AnnotationProjector.Project(Ra0, Dec0, Scale, 0, W, H, false, Ra0 + 0.01, Dec0);
        Assert.That(p, Is.Not.Null);
        Assert.That(p!.Value.x, Is.LessThan(W / 2.0));
    }

    [Test]
    public void Flip_MirrorsEastToRight() {
        var noFlip = AnnotationProjector.Project(Ra0, Dec0, Scale, 0, W, H, false, Ra0 + 0.01, Dec0)!.Value;
        var flip = AnnotationProjector.Project(Ra0, Dec0, Scale, 0, W, H, true, Ra0 + 0.01, Dec0)!.Value;
        // Mirror about the vertical centreline.
        Assert.That(flip.x, Is.EqualTo(W - noFlip.x).Within(0.01));
        Assert.That(flip.y, Is.EqualTo(noFlip.y).Within(0.01));
    }

    [Test]
    public void Rotation90_NorthGoesToSide() {
        // With a 90° rotation the "north" offset rotates off the vertical axis.
        var p = AnnotationProjector.Project(Ra0, Dec0, Scale, 90, W, H, false, Ra0, Dec0 + 0.1);
        Assert.That(p, Is.Not.Null);
        Assert.That(System.Math.Abs(p!.Value.x - W / 2.0), Is.GreaterThan(5));
        Assert.That(p.Value.y, Is.EqualTo(H / 2.0).Within(1.0));
    }

    [Test]
    public void BehindTangentPlane_ReturnsNull() {
        // A point ~180° away can't be projected.
        var p = AnnotationProjector.Project(Ra0, Dec0, Scale, 0, W, H, false, Ra0 + 12, -Dec0);
        Assert.That(p, Is.Null);
    }
}
