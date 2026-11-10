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
using S = NINA.Polaris.Services.GuideDriftWatchService.Sample;
using W = NINA.Polaris.Services.GuideDriftWatchService;

namespace NINA.Polaris.Test;

/// <summary>SV503 rig, 2026-11-09: 60 s frames 64 s apart, the field moving
/// -16 to -22 px in x per frame (1.02"/px) with a dither every third frame,
/// while the guide log held the star within 0.2 px.</summary>
[TestFixture]
public class GuideDriftWatchTests {
    private static readonly DateTime T0 = new(2026, 11, 9, 22, 28, 9, DateTimeKind.Utc);

    private static List<S> Frames(params (double Dx, double Dy)[] steps) {
        var list = new List<S> { new(1, 0, 0, false, T0, true) };
        double x = 0, y = 0;
        for (int i = 0; i < steps.Length; i++) {
            x += steps[i].Dx;
            y += steps[i].Dy;
            list.Add(new S(i + 2, x, y, false, T0.AddSeconds(64 * (i + 1)), true));
        }
        return list;
    }

    [Test]
    public void TheSv503DriftIsFlagged() {
        // Frame shifts measured on the rig, the dither between 0021 and 0022 included.
        var d = W.Measure(Frames((-20, -6), (-20, -6), (-24, -26), (-16, -4), (-16, -2), (-22, 10)));
        Assert.That(d, Is.Not.Null);
        Assert.That(W.IsSignificant(d!, 1.02), Is.True);
        Assert.That(d!.SpeedPxPerMin, Is.GreaterThan(15));
        Assert.That(W.Describe(d, 1.02, 1.1), Does.Contain("X axis").And.Contain("guide scope"));
    }

    [Test]
    public void AFieldThatStaysPutBetweenDithersIsFine() {
        // Small wander, and two 24 px dithers in different directions.
        var d = W.Measure(Frames((0.5, -0.3), (24, 2), (-0.4, 0.2), (0.3, 0.4), (-3, -23), (0.2, -0.5)));
        Assert.That(d, Is.Not.Null);
        Assert.That(W.IsSignificant(d!, 1.02), Is.False);
    }

    [Test]
    public void DithersAloneDoNotReadAsDrift() {
        var d = W.Measure(Frames((24, 0), (0, 24), (-24, 0), (0, -24), (24, 24), (-24, -24)));
        Assert.That(d == null || !W.IsSignificant(d, 1.02), Is.True);
    }

    [Test]
    public void FramesThatWereNotGuidedDoNotCount() {
        var f = Frames((-20, 0), (-20, 0), (-20, 0), (-20, 0), (-20, 0), (-20, 0));
        for (int i = 0; i < f.Count; i++) f[i] = f[i] with { Guiding = i == 0 };
        Assert.That(W.Measure(f), Is.Null);
    }

    [Test]
    public void ASlowCreepBelowTheThresholdIsFine() {
        // About 1"/min: differential flexure territory, not a guider holding the wrong thing.
        var d = W.Measure(Frames((-1, 0), (-1.1, 0), (-0.9, 0), (-1, 0), (-1.2, 0), (-1, 0)));
        Assert.That(W.IsSignificant(d!, 1.02), Is.False);
    }

    [Test]
    public void AMeridianFlipBreaksThePairs() {
        var f = Frames((-20, 0), (-20, 0), (-20, 0), (-20, 0), (-20, 0), (-20, 0));
        for (int i = 0; i < f.Count; i++) f[i] = f[i] with { Flipped = i % 2 == 0 };
        Assert.That(W.Measure(f), Is.Null);
    }
}
