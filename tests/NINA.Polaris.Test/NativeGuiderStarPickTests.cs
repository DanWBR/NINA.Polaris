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
using NINA.Polaris.Services;

namespace NINA.Polaris.Test;

/// <summary>
/// Choosing the guide star: the saturation level read from the frame rather
/// than from the driver's container depth, the detector keeping the bright
/// stars a guider wants, and a tap landing on the star that was tapped.
/// </summary>
[TestFixture]
public class NativeGuiderStarPickTests {

    private static DetectedStar Star(double x, double y, double flux = 100, double peak = 1000)
        => new DetectedStar { X = x, Y = y, Flux = flux, Peak = peak, HFR = 2.0 };

    [Test]
    public void SaturationLevel_ReadsRaw12BitCountsInA16BitContainer() {
        // indi_asi_ccd: BitDepth 16, samples never above 4095.
        var data = new ushort[100]; data[7] = 4095; data[9] = 3000;
        Assert.That(NativeGuider.SaturationLevel(16, 0, data), Is.EqualTo(4095));
    }

    [Test]
    public void SaturationLevel_LeftAlignedDataIsFullScale() {
        var data = new ushort[100]; data[3] = 65520;
        Assert.That(NativeGuider.SaturationLevel(16, 0, data), Is.EqualTo(65535));
    }

    [Test]
    public void SaturationLevel_SignificantBitsWinOverTheData() {
        var data = new ushort[100]; data[3] = 1000;
        Assert.That(NativeGuider.SaturationLevel(16, 12, data), Is.EqualTo(4095));
    }

    [Test]
    public void SaturationLevel_NeverAboveTheDriverDepth() {
        var data = new ushort[100]; data[3] = 250;
        Assert.That(NativeGuider.SaturationLevel(8, 0, data), Is.EqualTo(255));
    }

    [Test]
    public void IsSaturated_TwelveBitStarAtTheTop() {
        Assert.That(NativeGuider.IsSaturated(Star(0, 0, peak: 4095), 4095), Is.True);
        Assert.That(NativeGuider.IsSaturated(Star(0, 0, peak: 3000), 4095), Is.False);
        // The old rule measured a 12-bit frame against 65535 and never fired.
        Assert.That(NativeGuider.IsSaturated(Star(0, 0, peak: 4095), 65535), Is.False);
    }

    [Test]
    public void Detector_KeepsABrightStarWithAWideSkirt() {
        // A bright, slightly bloated star: its above-threshold footprint is
        // well over the stock 200-pixel cap that used to drop it.
        int w = 200, h = 200;
        var data = new ushort[w * h];
        var rnd = new Random(1);
        for (int i = 0; i < data.Length; i++) data[i] = (ushort)(500 + rnd.Next(0, 6));
        void Put(double cx, double cy, double amp, double sigma) {
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) {
                double dx = x - cx, dy = y - cy;
                double v = amp * Math.Exp(-(dx * dx + dy * dy) / (2 * sigma * sigma));
                data[y * w + x] = (ushort)Math.Min(65535, data[y * w + x] + v);
            }
        }
        Put(100, 100, 50000, 4.0);   // bright: ~600 px above threshold
        Put(40, 150, 800, 1.5);      // faint

        var stock = new StarDetector().Detect(data, w, h);
        var guide = NativeGuider.NewGuideStarDetector().Detect(data, w, h);

        Assert.That(stock.Exists(s => Math.Abs(s.X - 100) < 2 && Math.Abs(s.Y - 100) < 2), Is.False,
            "the stock detector drops the bright star (the bug being fixed)");
        Assert.That(guide.Exists(s => Math.Abs(s.X - 100) < 2 && Math.Abs(s.Y - 100) < 2), Is.True,
            "the guider's detector must keep it");
        Assert.That(guide[0].X, Is.EqualTo(100).Within(2), "and it ranks first by flux");
    }

    [Test]
    public void Tap_PicksTheStarUnderTheFinger() {
        var stars = new List<DetectedStar> { Star(300, 300, flux: 50), Star(600, 600, flux: 9000) };
        var pick = NativeGuider.PickStarNear(stars, 310, 295, 1000, 1000, 20, 65535, 60);
        Assert.That(pick.Star, Is.Not.Null);
        Assert.That(pick.Star!.X, Is.EqualTo(300));
    }

    [Test]
    public void Tap_FarFromAnyStar_SaysSoInsteadOfJumping() {
        var stars = new List<DetectedStar> { Star(600, 600, flux: 9000) };
        var pick = NativeGuider.PickStarNear(stars, 100, 100, 1000, 1000, 20, 65535, 60);
        Assert.That(pick.Star, Is.Null);
        Assert.That(pick.Reason, Does.Contain("No star near"));
    }

    [Test]
    public void Tap_OnASaturatedStar_ExplainsWhy() {
        var stars = new List<DetectedStar> { Star(300, 300, peak: 4095), Star(330, 300, peak: 800) };
        var pick = NativeGuider.PickStarNear(stars, 302, 301, 1000, 1000, 20, 4095, 60);
        Assert.That(pick.Star, Is.Null, "must not silently lock the neighbour");
        Assert.That(pick.Reason, Does.Contain("saturated"));
    }

    [Test]
    public void Tap_OnAnEdgeStar_ExplainsWhy() {
        var stars = new List<DetectedStar> { Star(5, 300) };
        var pick = NativeGuider.PickStarNear(stars, 8, 300, 1000, 1000, 20, 65535, 60);
        Assert.That(pick.Star, Is.Null);
        Assert.That(pick.Reason, Does.Contain("edge"));
    }
}
