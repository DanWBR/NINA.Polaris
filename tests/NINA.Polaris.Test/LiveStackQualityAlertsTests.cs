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
using NINA.Polaris.Services;
using NUnit.Framework;
using Sample = NINA.Polaris.Services.LiveStackingService.LiveStackQualitySample;
using Kind = NINA.Polaris.Services.LiveStackQualityAlerts.Kind;

namespace NINA.Polaris.Test;

[TestFixture]
public class LiveStackQualityAlertsTests {
    // frame, cumulative snr (unused by the detector), frame snr, hfr
    private static Sample S(int f, double frameSnr, double hfr) =>
        new(f, f * 60.0, frameSnr * System.Math.Sqrt(f), frameSnr, hfr, 100, 500);

    private static List<Sample> Series(double[] frameSnr, double[] hfr) {
        var list = new List<Sample>();
        for (int i = 0; i < frameSnr.Length; i++) list.Add(S(i + 1, frameSnr[i], hfr[i]));
        return list;
    }

    private static double[] Const(double v, int n) { var a = new double[n]; for (int i = 0; i < n; i++) a[i] = v; return a; }

    [Test]
    public void TooFewSamples_None() {
        var s = Series(Const(30, 9), Const(1.5, 9));
        Assert.That(LiveStackQualityAlerts.Analyze(s).Kind, Is.EqualTo(Kind.None));
    }

    [Test]
    public void Healthy_SteadySnr_FlatHfr_None() {
        var s = Series(Const(30, 14), Const(1.5, 14));
        Assert.That(LiveStackQualityAlerts.Analyze(s).Kind, Is.EqualTo(Kind.None));
    }

    [Test]
    public void SnrCollapse_FlatHfr_Clouds() {
        // Last 10: earlier half ~30, recent half ~14 (>30% drop); HFR flat.
        var snr = new List<double>(Const(30, 9)); snr.AddRange(Const(14, 5));
        var hfr = Const(1.5, 14);
        var alert = LiveStackQualityAlerts.Analyze(Series(snr.ToArray(), hfr));
        Assert.That(alert.Kind, Is.EqualTo(Kind.Clouds));
        Assert.That(alert.Message, Does.Contain("SNR"));
    }

    [Test]
    public void HfrRise_FocusDrift() {
        // HFR earlier ~1.5, recent ~2.1 (>20% rise); SNR steady.
        var hfr = new List<double>(Const(1.5, 9)); hfr.AddRange(Const(2.1, 5));
        var alert = LiveStackQualityAlerts.Analyze(Series(Const(30, 14), hfr.ToArray()));
        Assert.That(alert.Kind, Is.EqualTo(Kind.FocusDrift));
        Assert.That(alert.Message, Does.Contain("HFR"));
    }

    [Test]
    public void HfrRiseAndSnrFall_FocusDriftTakesPrecedence() {
        var snr = new List<double>(Const(30, 9)); snr.AddRange(Const(12, 5));
        var hfr = new List<double>(Const(1.5, 9)); hfr.AddRange(Const(2.2, 5));
        Assert.That(LiveStackQualityAlerts.Analyze(Series(snr.ToArray(), hfr.ToArray())).Kind,
                    Is.EqualTo(Kind.FocusDrift));
    }
}
