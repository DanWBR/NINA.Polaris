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

    /// <summary>FRA400 rig, 2026-11-21: 30 s frames, a 5 px dither after every
    /// third frame, 2.41"/px. Field positions measured by phase correlation on
    /// frames 0001 to 0013 against 0001. Each dither moved the field 15 to 26 px,
    /// and the frame after it moved another 2 to 3 px in the same direction
    /// while the guider closed on the new lock position.</summary>
    private static readonly (string At, double X, double Y)[] Fra400 = {
        ("22:03:08", 0, 0), ("22:03:43", -0.10, -0.57), ("22:04:17", -0.54, -0.01),
        ("22:05:06", 7.12, -13.10), ("22:05:40", 9.06, -15.04), ("22:06:14", 9.66, -15.38),
        ("22:07:07", 6.93, -30.97), ("22:07:41", 7.70, -32.10), ("22:08:15", 8.03, -31.13),
        ("22:09:15", 22.36, -16.72), ("22:09:49", 22.90, -17.64), ("22:10:24", 22.46, -17.38),
        ("22:11:20", -1.82, -7.85),
    };

    /// <summary>The samples the watch saw, with the guider's last settle time:
    /// a dither after frames 3, 6, 9 and 12, settled before the next frame.</summary>
    private static List<S> Fra400Samples(int count) {
        var day = new DateTime(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc);
        var list = new List<S>();
        DateTime settled = day.Add(TimeSpan.Parse("22:01:30"));
        for (int i = 0; i < count; i++) {
            var at = day.Add(TimeSpan.Parse(Fra400[i].At));
            if (i > 0 && i % 3 == 0) settled = at.AddSeconds(-20);
            list.Add(new S(i + 1, Fra400[i].X, Fra400[i].Y, false, at, true, settled));
        }
        return list;
    }

    [Test]
    public void DithersAndTheirSettleAreNotADrift() {
        for (int n = 7; n <= Fra400.Length; n++) {
            var d = W.Measure(Fra400Samples(n));
            Assert.That(d == null || !W.IsSignificant(d, 2.41), Is.True,
                $"frames 1-{n}: {d}");
        }
    }

    [Test]
    public void ARealDriftIsStillFoundBetweenDithers() {
        // The SV503 drift (-20 px per 64 s frame) with a dither and its settle
        // after every third frame: only one pair in three counts, so the watch
        // needs more frames but still finds it.
        var list = new List<S>();
        DateTime settled = T0.AddSeconds(-30);
        double x = 0;
        var rng = new Random(3);
        for (int i = 0; i < 19; i++) {
            var at = T0.AddSeconds(64 * i);
            if (i > 0 && i % 3 == 0) { settled = at.AddSeconds(-20); x += rng.Next(-24, 25); }
            x -= 20;
            list.Add(new S(i + 1, x, rng.NextDouble(), false, at, true, settled));
        }
        var d = W.Measure(list);
        Assert.That(d, Is.Not.Null);
        Assert.That(W.IsSignificant(d!, 1.02), Is.True);
        Assert.That(d!.VxPxPerMin, Is.LessThan(-15));
    }

    [Test]
    public void JitterBetweenShortFramesIsNotADrift() {
        // 10 s frames 14 s apart: half a pixel of centroid jitter reads as
        // 2 px/min, but the field goes nowhere.
        var list = new List<S>();
        double[] xs = { 0, 0.5, 0.1, 0.6, 0.2, 0.7, 0.3 };
        for (int i = 0; i < xs.Length; i++)
            list.Add(new S(i + 1, xs[i], 0, false, T0.AddSeconds(14 * i), true));
        var d = W.Measure(list);
        Assert.That(d == null || !W.IsSignificant(d, null), Is.True, d?.ToString());
    }

    [Test]
    public void APairAcrossASettleDoesNotCount() {
        // Six steady pairs, but a settle before the last frame: that pair and
        // nothing after it may count, so five pairs remain.
        var f = Frames((-20, 0), (-20, 0), (-20, 0), (-20, 0), (-20, 0), (-20, 0));
        for (int i = 0; i < f.Count; i++) f[i] = f[i] with { SettledAtUtc = i == f.Count - 1 ? T0 : null };
        var d = W.Measure(f);
        Assert.That(d, Is.Not.Null);
        Assert.That(d!.Pairs, Is.EqualTo(5));
    }

    [Test]
    public void AMeridianFlipBreaksThePairs() {
        var f = Frames((-20, 0), (-20, 0), (-20, 0), (-20, 0), (-20, 0), (-20, 0));
        for (int i = 0; i < f.Count; i++) f[i] = f[i] with { Flipped = i % 2 == 0 };
        Assert.That(W.Measure(f), Is.Null);
    }
}
