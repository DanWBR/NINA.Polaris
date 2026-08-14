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

using System;
using System.Collections.Generic;
using NUnit.Framework;
using NINA.Image.ImageAnalysis;

namespace NINA.Polaris.Test;

/// <summary>
/// The offset a live stack has to recover grows with the session: every frame
/// aligns against the FIRST frame's stars, and tracking drift plus each dither
/// walk the pointing away from where that reference sat.
///
/// Field, 2026-08-13 (SV550 + ASI585MC): a stack was stopped over a run of
/// black frames, the camera reconnected, and on resume every frame was dropped
/// as "alignment failed (200 stars detected)" — a perfect frame, rejected —
/// until a reset re-anchored the reference. The frames had drifted past the
/// 50 px default translation window while the stack was paused, and nothing
/// escalated the search. These pin the radius behaviour the fix relies on.
/// </summary>
[TestFixture]
public class StarMatcherDriftRadiusTests {

    // A fixed, irregular constellation so the offset vote has one clear winner
    // and no accidental symmetry can pair the wrong stars.
    private static readonly (double x, double y)[] Field = {
        (120, 90), (340, 150), (500, 420), (210, 610), (760, 300),
        (640, 700), (880, 520), (150, 300), (430, 260), (700, 130),
        (300, 480), (560, 590),
    };

    private static List<DetectedStar> Stars((double x, double y)[] pts, double dx = 0, double dy = 0) {
        var list = new List<DetectedStar>(pts.Length);
        foreach (var (x, y) in pts) list.Add(new DetectedStar { X = x + dx, Y = y + dy, HFR = 2.0 });
        return list;
    }

    /// <summary>Consecutive frames barely move; the tight default must keep
    /// matching them, cheaply, exactly as before the fix.</summary>
    [Test]
    public void ASmallDrift_MatchesAtTheDefaultRadius() {
        // Magnitude only: the signed warp convention (ref-minus-cur) is pinned
        // end to end by StarAlignmentTests; here the point is purely that a
        // small offset registers at the tight default.
        var t = StarMatcher.Match(Stars(Field), Stars(Field, dx: 12, dy: -9));
        Assert.That(t, Is.Not.Null);
        Assert.That(Math.Abs(t!.Tx), Is.EqualTo(12).Within(3));
        Assert.That(Math.Abs(t.Ty), Is.EqualTo(9).Within(3));
    }

    /// <summary>THE FIELD CASE. A 120 px offset — a paused, resumed stack —
    /// is invisible to the 50 px window: the correct pairs never vote, so no
    /// transform comes back at the default.</summary>
    [Test]
    public void ALargeDrift_IsInvisibleAtTheDefaultRadius() {
        var t = StarMatcher.Match(Stars(Field), Stars(Field, dx: 120, dy: 70));
        Assert.That(t, Is.Null, "a >50px pure translation cannot be found in the tight window");
    }

    /// <summary>...and is recovered once the search is widened, which is what
    /// the stacker now does on a failed match with a full star field.</summary>
    [Test]
    public void ALargeDrift_IsRecoveredAtTheWideRadius() {
        var t = StarMatcher.Match(Stars(Field), Stars(Field, dx: 120, dy: 70), maxSearchRadius: 250.0);
        Assert.That(t, Is.Not.Null, "the wide window has to find the same field, just shifted");
        Assert.That(Math.Abs(t!.Tx), Is.EqualTo(120).Within(4));
        Assert.That(Math.Abs(t.Ty), Is.EqualTo(70).Within(4));
    }

    /// <summary>The rigidity guard still holds at the wide radius: matching a
    /// field against an unrelated one must fail, not invent a warp.</summary>
    [Test]
    public void TheWideRadiusStillRejectsAnUnrelatedField() {
        var rng = new Random(7);
        var noise = new List<DetectedStar>();
        for (int i = 0; i < Field.Length; i++)
            noise.Add(new DetectedStar { X = rng.Next(1000), Y = rng.Next(800), HFR = 2.0 });

        var t = StarMatcher.Match(Stars(Field), noise, maxSearchRadius: 250.0);
        Assert.That(t, Is.Null, "no shared constellation, so no honest transform");
    }
}
