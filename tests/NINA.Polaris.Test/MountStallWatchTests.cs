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
using W = NINA.Polaris.Services.MountStallWatchService;

namespace NINA.Polaris.Test;

[TestFixture]
public class MountStallWatchTests {
    private static readonly DateTime T0 = new(2026, 11, 10, 2, 39, 47, DateTimeKind.Utc);

    /// <summary>Feed one sample a second and return the first second at which
    /// the drift crosses the threshold, or null.</summary>
    private static int? FirstTrigger(Func<int, double> raAt, int seconds) {
        var samples = new List<(DateTime, double)>();
        for (int t = 0; t <= seconds; t++) {
            W.Add(samples, T0.AddSeconds(t), raAt(t));
            var d = W.DriftSeconds(samples);
            if (d != null && Math.Abs(d.Value) >= W.MinDriftSeconds) return t;
        }
        return null;
    }

    /// <summary>Reported RA polled on the SV503 AM3 on 2026-11-09 from 23:39:47
    /// local, guiding after the meridian flip: steady for 2.5 minutes, then two
    /// losses the guider made up for, 12 s of time in under a minute.</summary>
    private static readonly (int T, double Ra)[] Sv503 = {
        (0, 2.6844444), (1, 2.6841667), (5, 2.6838889), (153, 2.6836111), (156, 2.6833333),
        (158, 2.6830556), (160, 2.6827778), (162, 2.6825000), (181, 2.6822222), (184, 2.6819444),
        (186, 2.6816667), (188, 2.6813889), (189, 2.6811111), (191, 2.6808333), (194, 2.6805556),
    };

    private static double Sv503At(int t) => Sv503.Last(s => s.T <= t).Ra;

    [Test]
    public void TheSv503LossesAreCaught() {
        var t = FirstTrigger(Sv503At, 202);
        Assert.That(t, Is.Not.Null);
        Assert.That(t, Is.GreaterThan(153), "nothing to see while it was tracking");
    }

    [Test]
    public void TheSteadyStretchDoesNotTrigger() =>
        Assert.That(FirstTrigger(Sv503At, 152), Is.Null);

    /// <summary>A healthy strain-wave mount: 25" of periodic error over 430 s,
    /// reported at 1 s resolution.</summary>
    [Test]
    public void PeriodicErrorCorrectionsDoNotTrigger() {
        double At(int t) {
            double sec = 25.0 / 15.0 * Math.Sin(2 * Math.PI * t / 430.0);
            return 2.68 + Math.Round(sec) / 3600.0;
        }
        Assert.That(FirstTrigger(At, 1800), Is.Null);
    }

    [Test]
    public void ASyncStartsTheWindowAgain() {
        // A 30 s jump at t=100 (a sync after a plate solve), steady otherwise.
        Assert.That(FirstTrigger(t => 2.68 + (t >= 100 ? 30.0 / 3600 : 0), 400), Is.Null);
    }

    [Test]
    public void RaWrapsAtZero() {
        Assert.Multiple(() => {
            Assert.That(W.RaDeltaSeconds(23.9990, 0.0010), Is.EqualTo(7.2).Within(1e-6));
            Assert.That(W.RaDeltaSeconds(0.0010, 23.9990), Is.EqualTo(-7.2).Within(1e-6));
        });
    }

    [Test]
    public void TooShortASpanCannotDecide() {
        var samples = new List<(DateTime, double)>();
        for (int t = 0; t < 60; t++) W.Add(samples, T0.AddSeconds(t), 2.68 - t * 0.5 / 3600);
        Assert.That(W.DriftSeconds(samples), Is.Null);
    }
}
