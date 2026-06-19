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
using NUnit.Framework;
using NINA.Polaris.Services;

namespace NINA.Polaris.Test;

/// <summary>
/// Pins the native guider's dark library + bad-pixel-map math: per-pixel
/// master-dark averaging, robust hot-pixel detection, dark subtraction, and
/// defect interpolation. These run on every guide frame when the user enables
/// dark/bpm calibration, so a regression silently corrupts guiding.
/// </summary>
[TestFixture]
public class GuideDarkMathTests {

    [Test]
    public void MeanStack_AveragesPerPixel() {
        var frames = new List<ushort[]> {
            new ushort[] { 10, 20, 30 },
            new ushort[] { 30, 40, 50 },
        };
        var master = GuideDarkMath.MeanStack(frames, 3);
        Assert.That(master, Is.EqualTo(new ushort[] { 20, 30, 40 }));
    }

    [Test]
    public void MeanStack_RoundsToNearest() {
        var frames = new List<ushort[]> {
            new ushort[] { 10 },
            new ushort[] { 11 },
        };
        Assert.That(GuideDarkMath.MeanStack(frames, 1)[0], Is.EqualTo(11));   // 10.5 -> 11
    }

    [Test]
    public void DetectBadPixels_FindsHotPixelOnFlatDark() {
        // 5x5 flat ~100 ADU background with one blazing hot pixel.
        var dark = new ushort[25];
        for (int i = 0; i < dark.Length; i++) dark[i] = 100;
        dark[12] = 60000;   // center, hot

        var bad = GuideDarkMath.DetectBadPixels(dark, sigmaK: 8.0);
        Assert.That(bad, Does.Contain(12));
        Assert.That(bad.Length, Is.EqualTo(1), "only the hot pixel should be flagged");
    }

    [Test]
    public void DetectBadPixels_FlatDarkHasNoDefects() {
        var dark = new ushort[16];
        for (int i = 0; i < dark.Length; i++) dark[i] = 250;
        Assert.That(GuideDarkMath.DetectBadPixels(dark, sigmaK: 8.0), Is.Empty);
    }

    [Test]
    public void SubtractDarkInPlace_ClampsAtZero() {
        var frame = new ushort[] { 500, 100, 50 };
        var dark = new ushort[] { 200, 300, 50 };
        GuideDarkMath.SubtractDarkInPlace(frame, dark);
        Assert.That(frame, Is.EqualTo(new ushort[] { 300, 0, 0 }));
    }

    [Test]
    public void SubtractDarkInPlace_NoOpOnSizeMismatch() {
        var frame = new ushort[] { 500, 100 };
        var dark = new ushort[] { 200 };
        GuideDarkMath.SubtractDarkInPlace(frame, dark);
        Assert.That(frame, Is.EqualTo(new ushort[] { 500, 100 }));   // untouched
    }

    [Test]
    public void ApplyBadPixelsInPlace_ReplacesWithNeighbourMedian() {
        // 3x3, uniform 100 except the center (index 4) is a stuck hot pixel.
        var frame = new ushort[] { 100, 100, 100, 100, 60000, 100, 100, 100, 100 };
        var bad = new HashSet<int> { 4 };
        GuideDarkMath.ApplyBadPixelsInPlace(frame, 3, 3, bad);
        Assert.That(frame[4], Is.EqualTo(100), "hot pixel replaced by the median of its neighbours");
        // Non-defect pixels are untouched.
        Assert.That(frame[0], Is.EqualTo(100));
    }

    [Test]
    public void ApplyBadPixelsInPlace_NoOpWhenEmpty() {
        var frame = new ushort[] { 1, 2, 3, 4 };
        GuideDarkMath.ApplyBadPixelsInPlace(frame, 2, 2, new HashSet<int>());
        Assert.That(frame, Is.EqualTo(new ushort[] { 1, 2, 3, 4 }));
    }
}
