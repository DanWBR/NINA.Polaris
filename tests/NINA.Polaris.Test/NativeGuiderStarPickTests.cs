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
/// Choosing the guide star: a saturation level that is read from the driver
/// and never guessed from the data, a detector the operator can retune per
/// rig, and a tap that lands on the star that was tapped.
/// </summary>
[TestFixture]
public class NativeGuiderStarPickTests {

    private static DetectedStar Star(double x, double y, double flux = 100, double peak = 1000)
        => new DetectedStar { X = x, Y = y, Flux = flux, Peak = peak, HFR = 2.0 };

    /// <summary>THE bug that closed this loop. The level used to be inferred
    /// from the frame's brightest pixel: "the smallest standard depth that
    /// holds it". That is circular, because the brightest pixel belongs to the
    /// brightest star, so a frame peaking at 4000 was read as 12-bit (4095) and
    /// the saturation test, peak within 5% of full scale, then flagged that
    /// very star. The operator taps the obvious star and is told it is
    /// saturated, on a frame nowhere near clipping.
    ///
    /// Without a reported significant depth the answer is now "unknown" (0),
    /// and unknown means never saturated.</summary>
    [Test]
    public void SaturationLevel_WithoutASignificantDepth_DoesNotGuessFromTheData() {
        var data = new ushort[100]; data[7] = 4000; data[9] = 3000;
        Assert.That(NativeGuider.SaturationLevel(16, 0, data), Is.EqualTo(0),
            "a 4000-count peak is not evidence of a 12-bit container");
        Assert.That(NativeGuider.IsSaturated(Star(0, 0, peak: 4000), 0), Is.False,
            "and an unknown level can never make a star saturated");
    }

    /// <summary>A frame that really does contain the container's maximum is
    /// clipped whatever the driver says about depth.</summary>
    [Test]
    public void SaturationLevel_DataAtTheContainerMaximumIsClipped() {
        var data = new ushort[100]; data[3] = 65535;
        Assert.That(NativeGuider.SaturationLevel(16, 0, data), Is.EqualTo(65535));
        var eight = new ushort[100]; eight[3] = 255;
        Assert.That(NativeGuider.SaturationLevel(8, 0, eight), Is.EqualTo(255));
    }

    [Test]
    public void SaturationLevel_SignificantBitsWinOverTheData() {
        var data = new ushort[100]; data[3] = 1000;
        Assert.That(NativeGuider.SaturationLevel(16, 12, data), Is.EqualTo(4095));
    }

    [Test]
    public void SaturationLevel_NeverAboveTheDriverDepth() {
        var data = new ushort[100]; data[3] = 250;
        // 250 is under the 8-bit container's 255, so the depth is unknown
        // rather than guessed; with the significant depth reported it is 255.
        Assert.That(NativeGuider.SaturationLevel(8, 0, data), Is.EqualTo(0));
        Assert.That(NativeGuider.SaturationLevel(8, 8, data), Is.EqualTo(255));
        Assert.That(NativeGuider.SaturationLevel(8, 12, data), Is.EqualTo(255),
            "and never above what the container can hold");
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

    /// <summary>A tapped star is honoured even when saturated: the operator
    /// pointed at it, a flat-topped core costs some centroid precision, and
    /// that is their call. Refusing handed back nothing and left them with a
    /// frame full of stars they could not guide on.</summary>
    [Test]
    public void Tap_OnASaturatedStar_LocksItWithAWarning() {
        var stars = new List<DetectedStar> { Star(300, 300, peak: 4095), Star(330, 300, peak: 800) };
        var pick = NativeGuider.PickStarNear(stars, 302, 301, 1000, 1000, 20, 4095, 60);
        Assert.That(pick.Star, Is.Not.Null, "the tap is honoured");
        Assert.That(pick.Star!.X, Is.EqualTo(300), "and it is the star tapped, not the neighbour");
        Assert.That(pick.Reason, Does.Contain("saturated"), "with the cost spelled out");
    }

    // ----- the operator's knobs -----

    [Test]
    public void Detector_DefaultsWhenTheRigSaysNothing() {
        var d = NativeGuider.NewGuideStarDetector(null);
        Assert.That(d.SigmaThreshold, Is.EqualTo(5.0));
        Assert.That(d.MinStarSize, Is.EqualTo(5));
        Assert.That(d.MaxStarSize, Is.EqualTo(6000), "the guider's wide-skirt cap, not the stock 200");
        Assert.That(d.MaxHfr, Is.EqualTo(50));
    }

    /// <summary>The point of the knobs: a rig whose guide camera needs a lower
    /// threshold and a smaller minimum blob gets them, instead of "no suitable
    /// guide star" over a frame with stars in it.</summary>
    [Test]
    public void Detector_TakesTheRigsValues() {
        var rig = new EquipmentProfile {
            NativeStarSigma = 2.5, NativeStarMinSize = 2,
            NativeStarMaxSize = 900, NativeStarMaxHfd = 12
        };
        var d = NativeGuider.NewGuideStarDetector(rig);
        Assert.That(d.SigmaThreshold, Is.EqualTo(2.5));
        Assert.That(d.MinStarSize, Is.EqualTo(2));
        Assert.That(d.MaxStarSize, Is.EqualTo(900));
        Assert.That(d.MaxHfr, Is.EqualTo(12));
    }

    /// <summary>And they are clamped, so a typed 0 or 1e6 cannot turn the
    /// detector into something that finds everything or nothing.</summary>
    [Test]
    public void Detector_ClampsWhatTheRigAsksFor() {
        var low = NativeGuider.NewGuideStarDetector(new EquipmentProfile {
            NativeStarSigma = 0, NativeStarMinSize = 0, NativeStarMaxSize = 1, NativeStarMaxHfd = 0
        });
        Assert.That(low.SigmaThreshold, Is.EqualTo(1));
        Assert.That(low.MinStarSize, Is.EqualTo(1));
        Assert.That(low.MaxStarSize, Is.EqualTo(50));
        Assert.That(low.MaxHfr, Is.EqualTo(1));

        var high = NativeGuider.NewGuideStarDetector(new EquipmentProfile {
            NativeStarSigma = 1e6, NativeStarMinSize = 100000,
            NativeStarMaxSize = 100000, NativeStarMaxHfd = 1e6
        });
        Assert.That(high.SigmaThreshold, Is.EqualTo(20));
        Assert.That(high.MinStarSize, Is.EqualTo(200));
        Assert.That(high.MaxStarSize, Is.EqualTo(20000));
        Assert.That(high.MaxHfr, Is.EqualTo(100));
    }

    [Test]
    public void Tap_OnAnEdgeStar_ExplainsWhy() {
        var stars = new List<DetectedStar> { Star(5, 300) };
        var pick = NativeGuider.PickStarNear(stars, 8, 300, 1000, 1000, 20, 65535, 60);
        Assert.That(pick.Star, Is.Null);
        Assert.That(pick.Reason, Does.Contain("edge"));
    }
}
