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
using NINA.Polaris.Services;

namespace NINA.Polaris.Test;

/// <summary>Unit tests for the pure photon-transfer-curve math behind the
/// sensor analysis. The capture loop needs a camera and is exercised
/// end-to-end against the simulator, not here.</summary>
[TestFixture]
public class SensorAnalysisServiceTests {

    // ----- LinFit -----

    [Test]
    public void LinFit_PerfectLine_RecoversSlopeInterceptR2() {
        var xs = new double[] { 0, 1, 2, 3, 4 };
        var ys = new double[] { 1, 3, 5, 7, 9 }; // y = 2x + 1
        var (slope, intercept, r2) = SensorAnalysisService.LinFit(xs, ys);
        Assert.That(slope, Is.EqualTo(2.0).Within(1e-9));
        Assert.That(intercept, Is.EqualTo(1.0).Within(1e-9));
        Assert.That(r2, Is.EqualTo(1.0).Within(1e-9));
    }

    // ----- BuildRow (PTC -> gain/read noise/full well/DR) -----

    [Test]
    public void BuildRow_ShotNoiseLine_RecoversGainAndDerived() {
        // Var = signal / g, with g = 2 e/ADU -> slope 0.5.
        double g = 2.0;
        var sig = new double[] { 100, 200, 300, 400, 500, 600 };
        var var = new double[sig.Length];
        for (int i = 0; i < sig.Length; i++) var[i] = sig[i] / g;
        double readNoiseAdu = 3.0, satAdu = 4095;

        var row = SensorAnalysisService.BuildRow(100, sig, var, readNoiseAdu, satAdu);

        Assert.That(row.Valid, Is.True);
        Assert.That(row.ElectronsPerAdu, Is.EqualTo(2.0).Within(0.01));
        Assert.That(row.ReadNoiseE, Is.EqualTo(6.0).Within(0.05));     // 3 ADU * 2
        Assert.That(row.FullWellE, Is.EqualTo(8190).Within(1));        // 4095 * 2
        Assert.That(row.DynamicRangeStops, Is.EqualTo(System.Math.Log2(8190.0 / 6.0)).Within(0.02));
    }

    [Test]
    public void BuildRow_FlatVariance_MarksInvalid() {
        var sig = new double[] { 100, 200, 300 };
        var var = new double[] { 5, 5, 5 }; // no rise with signal
        var row = SensorAnalysisService.BuildRow(100, sig, var, 3.0, 4095);
        Assert.That(row.Valid, Is.False);
        Assert.That(row.Note, Is.Not.Null);
    }

    [Test]
    public void BuildRow_TooFewPoints_MarksInvalid() {
        var row = SensorAnalysisService.BuildRow(100, new double[] { 100 }, new double[] { 50 }, 3.0, 4095);
        Assert.That(row.Valid, Is.False);
    }

    // ----- DetectQuantStep -----

    [Test]
    public void DetectQuantStep_LeftJustified12in16_Returns16() {
        var d = new ushort[] { 0, 16, 32, 48, 4096, 8192 }; // all multiples of 16
        Assert.That(SensorAnalysisService.DetectQuantStep(d), Is.EqualTo(16));
    }

    [Test]
    public void DetectQuantStep_RightJustified_Returns1() {
        var d = new ushort[] { 0, 1, 2, 3, 4095 };
        Assert.That(SensorAnalysisService.DetectQuantStep(d), Is.EqualTo(1));
    }

    // ----- BuildGainList -----

    [Test]
    public void BuildGainList_LogSpaced_AscendingAndBounded() {
        var list = SensorAnalysisService.BuildGainList(0, 1000, 8);
        Assert.That(list.First(), Is.EqualTo(0));     // zero prepended
        Assert.That(list.Last(), Is.EqualTo(1000));
        for (int i = 1; i < list.Count; i++)
            Assert.That(list[i], Is.GreaterThan(list[i - 1]), "must be strictly ascending");
    }

    [Test]
    public void BuildGainList_MaxLeMin_SinglePoint() {
        var list = SensorAnalysisService.BuildGainList(100, 100, 8);
        Assert.That(list, Has.Count.EqualTo(1));
        Assert.That(list[0], Is.EqualTo(100));
    }

    // ----- FindUnityGain -----

    [Test]
    public void FindUnityGain_CrossingInterpolated() {
        var rows = new List<SensorAnalysisRow> {
            Row(100, 2.0), Row(1000, 0.5)
        };
        var unity = SensorAnalysisService.FindUnityGain(rows);
        Assert.That(unity, Is.Not.Null);
        // 1.0 e/ADU lies between gain 100 (2.0) and 1000 (0.5), log-interp.
        Assert.That(unity!.Value, Is.GreaterThan(100).And.LessThan(1000));
    }

    [Test]
    public void FindUnityGain_NoCrossing_ReturnsNull() {
        var rows = new List<SensorAnalysisRow> { Row(100, 2.0), Row(1000, 1.5) };
        Assert.That(SensorAnalysisService.FindUnityGain(rows), Is.Null);
    }

    // ----- region stats -----

    [Test]
    public void RegionDiffVar_IdenticalFrames_IsZero() {
        int w = 64, h = 64;
        var a = new ushort[w * h];
        for (int i = 0; i < a.Length; i++) a[i] = 1000;
        Assert.That(SensorAnalysisService.RegionDiffVar(a, (ushort[])a.Clone(), w, h), Is.EqualTo(0).Within(1e-9));
    }

    private static SensorAnalysisRow Row(int gain, double eAdu) =>
        new(gain, eAdu, 0, 0, 1, 0, 0, 5, 1, true, null);
}