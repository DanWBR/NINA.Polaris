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

namespace NINA.Polaris.Test;

[TestFixture]
public class SensorConstantsTests {
    private static SensorAnalysisRow Row(int gain, double eadu, double read, double fw) =>
        new(gain, eadu, read, fw, 1, 0, 12, 5, 0.99, true, null);

    [Test]
    public void NearestRow_PicksClosestValidGain() {
        var sa = new SensorAnalysisResult("t", "cam", 16, 1, 100, 100,
            new List<SensorAnalysisRow> { Row(0, 1.0, 3.0, 50000), Row(100, 0.25, 1.5, 18000), Row(300, 0.05, 1.0, 4000) });
        Assert.That(SensorConstants.NearestRow(sa, 90)!.Gain, Is.EqualTo(100));
        Assert.That(SensorConstants.NearestRow(sa, 10)!.Gain, Is.EqualTo(0));
        Assert.That(SensorConstants.NearestRow(sa, 280)!.Gain, Is.EqualTo(300));
    }

    [Test]
    public void NearestRow_SkipsInvalidRows() {
        var sa = new SensorAnalysisResult("t", "cam", 16, 1, 100, 100,
            new List<SensorAnalysisRow> { Row(0, 1.0, 3.0, 50000),
                new(100, 0, 0, 0, 1, 0, 0, 0, 0, false, "bad") });
        Assert.That(SensorConstants.NearestRow(sa, 100)!.Gain, Is.EqualTo(0));
    }

    [Test]
    public void Fallback_ASI585_Gain200_MatchesChart() {
        Assert.That(SensorConstants.TryFallback("ZWO ASI585MC Pro", 200, out var c), Is.True);
        Assert.That(c.ElectronsPerAdu, Is.EqualTo(0.0625).Within(1e-4));
        Assert.That(c.ReadNoiseE, Is.EqualTo(1.05).Within(1e-4));
        Assert.That(c.FullWellE, Is.EqualTo(4096).Within(1));
    }

    [Test]
    public void Fallback_ASI2600_Gain100() {
        Assert.That(SensorConstants.TryFallback("ZWO ASI2600MC Pro", 100, out var c), Is.True);
        Assert.That(c.ElectronsPerAdu, Is.EqualTo(0.25).Within(1e-4));
        Assert.That(c.ReadNoiseE, Is.EqualTo(1.5).Within(1e-4));
    }

    [Test]
    public void Fallback_ASI183_Gain111_MatchesChart() {
        Assert.That(SensorConstants.TryFallback("ZWO ASI183MM Pro", 111, out var c), Is.True);
        Assert.That(c.ElectronsPerAdu, Is.EqualTo(0.066).Within(1e-4));
        Assert.That(c.ReadNoiseE, Is.EqualTo(2.15).Within(1e-4));
        Assert.That(c.FullWellE, Is.EqualTo(4100).Within(1));
    }

    [TestCase("ZWO ASI4400MC Pro", 136, 0.238, 1.60)]
    [TestCase("ZWO ASI533MC Pro", 100, 0.250, 1.50)]
    [TestCase("ZWO ASI294MM Pro", 120, 0.058, 1.77)]
    [TestCase("ZWO ASI6200MC Pro", 100, 0.260, 1.35)]
    public void Fallback_MoreCameras_MatchCharts(string cam, int gain, double eadu, double read) {
        Assert.That(SensorConstants.TryFallback(cam, gain, out var c), Is.True);
        Assert.That(c.ElectronsPerAdu, Is.EqualTo(eadu).Within(1e-4));
        Assert.That(c.ReadNoiseE, Is.EqualTo(read).Within(1e-4));
    }

    [Test]
    public void Fallback_UnknownCamera_False() {
        Assert.That(SensorConstants.TryFallback("QHY268C", 100, out _), Is.False);
    }

    [Test]
    public void Fallback_GainOutOfTolerance_False() {
        // ASI585 anchors at gain 0 and 200; gain 120 is >60 from both.
        Assert.That(SensorConstants.TryFallback("ZWO ASI585MC Pro", 120, out _), Is.False);
    }

    [Test]
    public void Fallback_EmptyCamera_False() {
        Assert.That(SensorConstants.TryFallback("", 100, out _), Is.False);
        Assert.That(SensorConstants.TryFallback(null, 100, out _), Is.False);
    }
}
