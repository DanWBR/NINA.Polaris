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
using NUnit.Framework;
using NINA.Polaris.Services;

namespace NINA.Polaris.Test;

/// <summary>The band the LIVE colour histogram bins over. Full-scale bins put a
/// stacked sky into two buckets of 256 (measured: 301k samples, std 124 ADU),
/// and two points draw as a hairline whatever window the panel picks.</summary>
[TestFixture]
public class LiveStackHistogramBandTests {

    private const int Coarse = 1024;

    /// <summary>Coarse histogram of a set of ADU values, exactly as
    /// ComputeColorHistogram's first pass builds it.</summary>
    private static int[] Bucket(params (int Adu, int Count)[] samples) {
        var c = new int[Coarse];
        foreach (var (adu, n) in samples) c[adu * (Coarse - 1) / 65535] += n;
        return c;
    }

    private static long Total(int[] coarse) {
        long t = 0;
        foreach (var v in coarse) t += v;
        return t;
    }

    [Test]
    public void StackedSky_BandHugsThePeak_NotTheFullScale() {
        // The shape the field rig actually produces: everything within a few
        // hundred ADU of 1277, plus a sparse star tail out to 43738.
        var coarse = Bucket((1150, 40_000), (1277, 200_000), (1400, 60_000),
                            (1550, 1_400), (8000, 120), (43738, 30));

        var (lo, hi) = LiveStackingService.HistogramBand(coarse, Total(coarse));

        Assert.That(lo, Is.LessThan(1150), "the band must contain the low side of the peak");
        Assert.That(hi, Is.GreaterThan(1400), "the band must contain the high side of the peak");
        Assert.That(hi - lo, Is.LessThan(8000),
            "a handful of stars must not stretch the band back over the whole scale");
        // 256 bins over the band have to resolve the sky noise (std ~124 ADU).
        Assert.That((hi - lo) / 255.0, Is.LessThan(124.0 / 4),
            "bins must be several to a sigma, or the curve is a spike again");
    }

    [Test]
    public void FullRangeData_KeepsTheWholeScale() {
        var coarse = new int[Coarse];
        for (int k = 0; k < Coarse; k++) coarse[k] = 1000;

        var (lo, hi) = LiveStackingService.HistogramBand(coarse, Total(coarse));

        Assert.That(lo, Is.EqualTo(0));
        Assert.That(hi, Is.EqualTo(65535));
    }

    [Test]
    public void FlatFrame_GetsANominalWidth() {
        var coarse = Bucket((4096, 300_000));

        var (lo, hi) = LiveStackingService.HistogramBand(coarse, Total(coarse));

        Assert.That(hi - lo, Is.GreaterThanOrEqualTo(255),
            "a degenerate band would divide by zero downstream");
        Assert.That(lo, Is.LessThanOrEqualTo(4096));
        Assert.That(hi, Is.GreaterThanOrEqualTo(4096));
    }

    [Test]
    public void BandAlwaysInsideTheSixteenBitScale() {
        foreach (var adu in new[] { 0, 40, 32768, 65400, 65535 }) {
            var (lo, hi) = LiveStackingService.HistogramBand(
                Bucket((adu, 100_000)), 100_000);
            Assert.That(lo, Is.GreaterThanOrEqualTo(0), $"lo for {adu}");
            Assert.That(hi, Is.LessThanOrEqualTo(65535), $"hi for {adu}");
            Assert.That(hi, Is.GreaterThan(lo), $"width for {adu}");
        }
    }
}
