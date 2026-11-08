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
using NINA.Polaris.Services;
using NUnit.Framework;
using Verdict = NINA.Polaris.Services.GuidePerformanceMonitor.Verdict;

namespace NINA.Polaris.Test;

[TestFixture]
public class GuidePerformanceMonitorTests {
    private const int Exp = 2000;     // 2 s guide exposures: stall window = max(60 s, 30 s) = 60 s

    private static GuidePerformanceMonitor Fresh(bool big = false) {
        var m = new GuidePerformanceMonitor();
        m.Reset(0, big);
        return m;
    }

    // ---- stall ----

    [Test]
    public void NoCorrectionForTheWindow_IsAStall() {
        var m = Fresh();
        for (long t = 2000; t <= 58_000; t += 2000) m.NoteNotApplied(t);
        Assert.That(m.Evaluate(58_000, Exp).Verdict, Is.EqualTo(Verdict.None));
        m.NoteNotApplied(60_000);
        Assert.That(m.Evaluate(60_000, Exp).Verdict, Is.EqualTo(Verdict.Stalled));
    }

    [Test]
    public void CorrectionsKeepTheStallAway() {
        var m = Fresh();
        var rnd = new Random(1);
        for (long t = 2000; t <= 300_000; t += 2000) {
            m.NoteApplied(t, rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5);
            Assert.That(m.Evaluate(t, Exp).Verdict, Is.EqualTo(Verdict.None), $"t={t}");
        }
    }

    [Test]
    public void LongGuideExposures_StretchTheStallWindow() {
        Assert.That(GuidePerformanceMonitor.StallWindowMs(2000), Is.EqualTo(60_000));
        Assert.That(GuidePerformanceMonitor.StallWindowMs(8000), Is.EqualTo(120_000));
    }

    // A pause (a meridian flip) or a settle is not a stall: the clock restarts.
    [Test]
    public void Rearm_RestartsTheStallClock() {
        var m = Fresh();
        m.Rearm(100_000);
        Assert.That(m.Evaluate(150_000, Exp).Verdict, Is.EqualTo(Verdict.None));
        Assert.That(m.Evaluate(160_000, Exp).Verdict, Is.EqualTo(Verdict.Stalled));
    }

    // ---- frozen ----

    [Test]
    public void OffsetStuckAtZero_IsFrozen() {
        var m = Fresh();
        for (int i = 1; i < GuidePerformanceMonitor.FrozenFrames; i++) m.NoteApplied(i * 2000, 0, 0);
        Assert.That(m.Evaluate(30_000, Exp).Verdict, Is.EqualTo(Verdict.None));
        m.NoteApplied(32_000, 0.001, -0.002);
        Assert.That(m.Evaluate(32_000, Exp).Verdict, Is.EqualTo(Verdict.Frozen));
    }

    [Test]
    public void TheSameOffsetEveryFrame_IsFrozen() {
        var m = Fresh();
        for (int i = 0; i < GuidePerformanceMonitor.FrozenFrames + 1; i++) m.NoteApplied(i * 2000, 0.731, -0.412);
        Assert.That(m.Evaluate(30_000, Exp).Verdict, Is.EqualTo(Verdict.Frozen));
    }

    [Test]
    public void ALostFrameBreaksAFrozenRun() {
        var m = Fresh();
        for (int i = 0; i < 8; i++) m.NoteApplied(i * 2000, 0, 0);
        m.NoteNotApplied(18_000);
        for (int i = 10; i < 18; i++) m.NoteApplied(i * 2000, 0.3 * (i % 3), 0.1);
        Assert.That(m.Evaluate(36_000, Exp).Verdict, Is.EqualTo(Verdict.None));
    }

    // ---- big star ----

    [Test]
    public void BigStarWithHighError_IsFlagged_ASmallOneIsNot() {
        var big = Fresh(big: true);
        var small = Fresh(big: false);
        for (int i = 0; i < GuidePerformanceMonitor.BigStarWindow; i++) {
            double e = i % 2 == 0 ? 1.4 : -1.4;
            big.NoteApplied(i * 2000, e, -e);
            small.NoteApplied(i * 2000, e, -e);
        }
        Assert.That(big.Evaluate(40_000, Exp).Verdict, Is.EqualTo(Verdict.BigStar));
        Assert.That(small.Evaluate(40_000, Exp).Verdict, Is.EqualTo(Verdict.None));
    }

    [Test]
    public void BigStarGuidingWell_IsLeftAlone() {
        var m = Fresh(big: true);
        for (int i = 0; i < 40; i++) m.NoteApplied(i * 2000, 0.2 * Math.Sin(i), 0.15 * Math.Cos(i));
        Assert.That(m.Evaluate(80_000, Exp).Verdict, Is.EqualTo(Verdict.None));
    }

    // ---- rate limit ----

    [Test]
    public void Reselections_AreSpacedAndCapped() {
        var m = Fresh();
        Assert.That(m.TryConsumeReselect(0), Is.True);
        Assert.That(m.TryConsumeReselect(60_000), Is.False, "too soon after the last one");
        Assert.That(m.TryConsumeReselect(120_000), Is.True);
        Assert.That(m.TryConsumeReselect(240_000), Is.True);
        Assert.That(m.TryConsumeReselect(360_000), Is.True);
        Assert.That(m.TryConsumeReselect(480_000), Is.False, "four inside half an hour");
        Assert.That(m.TryConsumeReselect(GuidePerformanceMonitor.ReselectWindowMs + 1), Is.True,
            "the first one has left the window");
    }

    [Test]
    public void TheLimitAlert_IsOncePerWindow() {
        var m = Fresh();
        Assert.That(m.ShouldAlertLimit(1000), Is.True);
        Assert.That(m.ShouldAlertLimit(5000), Is.False);
        Assert.That(m.ShouldAlertLimit(1000 + GuidePerformanceMonitor.ReselectWindowMs), Is.True);
    }

    // ---- star choice ----

    private static DetectedStar Star(double x, double y, double flux, double hfr, int px = 20, double peak = 3000)
        => new() { X = x, Y = y, Flux = flux, HFR = hfr, PixelCount = px, Peak = peak };

    private static NativeGuider.GuideStarPick Pick(IReadOnlyList<DetectedStar> stars, double frameMax = 4000,
            (double X, double Y, double R)? avoid = null, bool classic = false)
        => NativeGuider.PickGuideStar(stars, 1000, 800, 30, satGuard: 0, allowSat: false, classic, frameMax, avoid);

    [Test]
    public void AutoSelect_SkipsABloatedStar_ForTheBrightestTypicalOne() {
        var stars = new[] {
            Star(500, 400, 90_000, 6.0, px: 400),   // the bright blob
            Star(200, 200, 20_000, 2.0),
            Star(700, 300, 15_000, 2.1),
            Star(300, 600, 9_000, 1.9),
        };
        var p = Pick(stars);
        Assert.That((p.Star!.X, p.Star.Y), Is.EqualTo((200.0, 200.0)));
        Assert.That(p.StarIsBig, Is.False);
        Assert.That(p.Big, Is.EqualTo(1));
    }

    // Saturation level unknown (most drivers): a star at the frame's own
    // maximum that is already wider than typical is clipped.
    [Test]
    public void AutoSelect_SkipsAClippedStar_WhenSaturationIsUnknown() {
        var stars = new[] {
            Star(500, 400, 60_000, 2.8, px: 30, peak: 4000),
            Star(200, 200, 20_000, 2.0, peak: 2500),
            Star(700, 300, 15_000, 2.1, peak: 2200),
            Star(300, 600, 9_000, 1.9, peak: 1500),
        };
        Assert.That(Pick(stars, frameMax: 4000).Star!.X, Is.EqualTo(200));
    }

    [Test]
    public void AutoSelect_TakesABigStar_WhenNothingElseIsThere() {
        var stars = new[] { Star(500, 400, 90_000, 6.0, px: 400) };
        var p = Pick(stars);
        Assert.That(p.Star!.X, Is.EqualTo(500));
    }

    [Test]
    public void Reselect_AvoidsTheStarBeingLeft() {
        var stars = new[] {
            Star(200, 200, 20_000, 2.0),
            Star(700, 300, 15_000, 2.1),
            Star(300, 600, 9_000, 1.9),
        };
        var p = Pick(stars, avoid: (201, 199, 30));
        Assert.That(p.Star!.X, Is.EqualTo(700));
    }

    [Test]
    public void ClassicSelection_KeepsTheBrightest() {
        var stars = new[] {
            Star(500, 400, 90_000, 6.0, px: 400),
            Star(200, 200, 20_000, 2.0),
            Star(700, 300, 15_000, 2.1),
        };
        Assert.That(Pick(stars, classic: true).Star!.X, Is.EqualTo(500));
    }

    // ---- meridian flip retries ----

    [Test]
    public void AFailedFlip_IsTriedAgainUntilTheAttemptsRunOut() {
        var now = new DateTime(2026, 11, 8, 2, 4, 53, DateTimeKind.Utc);
        Assert.That(MeridianFlipService.NextAttemptAfter(1, now, MeridianFlipService.FailedFlipRetry),
            Is.EqualTo(now.AddMinutes(1)));
        Assert.That(MeridianFlipService.NextAttemptAfter(MeridianFlipService.MaxFlipAttempts - 1, now,
            MeridianFlipService.FailedFlipRetry), Is.Not.Null);
        Assert.That(MeridianFlipService.NextAttemptAfter(MeridianFlipService.MaxFlipAttempts, now,
            MeridianFlipService.FailedFlipRetry), Is.Null);
    }
}
