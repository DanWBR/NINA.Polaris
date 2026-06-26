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
using NUnit.Framework;

namespace NINA.Polaris.Test;

[TestFixture]
public class StarDetectorTests {

    private static ushort[] MakeFrame(int width, int height, ushort background = 200) {
        var data = new ushort[width * height];
        if (background == 0) return data;
        for (int i = 0; i < data.Length; i++) data[i] = background;
        return data;
    }

    private static void PaintStar(ushort[] data, int width, int cx, int cy,
                                   int radius, ushort peak, ushort background) {
        // Crude Gaussian-ish blob, peak at centre, falls off to
        // background at radius. Good enough for the detector to
        // pick up but not so wide it triggers the MaxStarSize cap.
        for (int dy = -radius; dy <= radius; dy++) {
            for (int dx = -radius; dx <= radius; dx++) {
                int x = cx + dx, y = cy + dy;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist > radius) continue;
                double t = 1 - dist / radius;
                ushort v = (ushort)(background + (peak - background) * t * t);
                int idx = y * width + x;
                if (data[idx] < v) data[idx] = v;
            }
        }
    }

    [Test]
    public void Detect_FindsObviousStarsOnFlatBackground() {
        const int w = 256, h = 256;
        var data = MakeFrame(w, h, background: 500);
        PaintStar(data, w, 50,  50,  4, 30000, 500);
        PaintStar(data, w, 200, 50,  4, 25000, 500);
        PaintStar(data, w, 50,  200, 4, 20000, 500);
        PaintStar(data, w, 200, 200, 4, 30000, 500);
        PaintStar(data, w, 128, 128, 5, 35000, 500);

        var det = new StarDetector();
        var stars = det.Detect(data, w, h);

        Assert.That(stars.Count, Is.GreaterThanOrEqualTo(5),
            "Five painted stars should all be found on a flat 500-ADU background.");
    }

    /// <summary>
    /// Reproduces the regression where a frame with a large zero
    /// border (live-stack accumulator regions that never got
    /// written, subframe black bars) yields median = 0, MAD = 0,
    /// threshold = 0, and the flood-fill consumes the entire image
    /// into one over-sized blob — returning 0 stars even though
    /// the picture is full of obvious ones.
    /// </summary>
    [Test]
    public void Detect_StillFindsStars_WhenMostOfFrameIsZero() {
        const int w = 512, h = 512;
        var data = new ushort[w * h];        // all zeros
        // Active region: ~25% of the frame, centred. Mimics a
        // subframe sitting inside a zero-padded sensor buffer.
        const int activeX = 128, activeY = 128, activeW = 256, activeH = 256;
        for (int y = activeY; y < activeY + activeH; y++) {
            for (int x = activeX; x < activeX + activeW; x++) {
                data[y * w + x] = 600;     // realistic sky background
            }
        }
        // A handful of stars inside the active region.
        PaintStar(data, w, activeX + 30,  activeY + 30,  3, 28000, 600);
        PaintStar(data, w, activeX + 220, activeY + 30,  3, 32000, 600);
        PaintStar(data, w, activeX + 30,  activeY + 220, 4, 26000, 600);
        PaintStar(data, w, activeX + 128, activeY + 128, 5, 35000, 600);

        var det = new StarDetector();
        var stars = det.Detect(data, w, h);

        Assert.That(stars.Count, Is.GreaterThanOrEqualTo(3),
            "Detector must still find the painted stars when the zero " +
            "border dominates the histogram (regression: it used to " +
            "collapse threshold to 0 and return zero stars).");
    }

    /// <summary>
    /// A perfectly flat frame (uniform background, no stars) must
    /// not crash and must return an empty list — not invent stars
    /// from random noise interpreted as signal.
    /// </summary>
    [Test]
    public void Detect_ReturnsEmpty_OnPerfectlyFlatFrame() {
        const int w = 256, h = 256;
        var data = MakeFrame(w, h, background: 1000);

        var det = new StarDetector();
        var stars = det.Detect(data, w, h);

        Assert.That(stars, Is.Empty);
    }

    [Test]
    public void Detect_HandlesAllZeroFrame() {
        const int w = 256, h = 256;
        var data = new ushort[w * h];        // all zeros

        var det = new StarDetector();
        var stars = det.Detect(data, w, h);

        // Detector must not throw and must return an empty list.
        Assert.That(stars, Is.Empty);
    }

    // Paint a defocused-star DONUT: a bright annulus (ring) of given radius and
    // thickness on a flat background, with a dim/empty centre — exactly the
    // shape the HFR measurement used to misread as ~1.
    private static void PaintDonut(ushort[] data, int width, int cx, int cy,
                                   double radius, double thickness, ushort peak, ushort background) {
        int r = (int)Math.Ceiling(radius + thickness);
        for (int dy = -r; dy <= r; dy++) {
            for (int dx = -r; dx <= r; dx++) {
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (Math.Abs(dist - radius) > thickness) continue; // ring only
                int x = cx + dx, y = cy + dy;
                int idx = y * width + x;
                if (data[idx] < peak) data[idx] = peak;
            }
        }
    }

    [Test]
    public void CurveOfGrowthHfr_DefocusedDonut_MeasuresLargeRadiusNotOne() {
        // The core auto-focus failure: a big defocused donut. With the AF-tuned
        // detector (8-connectivity + curve-of-growth over the bbox) the measured
        // HFR must land near the ring radius (large), NOT collapse to ~1.
        const int w = 256, h = 256;
        var data = MakeFrame(w, h, background: 500);
        PaintDonut(data, w, 128, 128, radius: 12, thickness: 1.5, peak: 25000, background: 500);

        var af = new StarDetector {
            EightConnected = true, CurveOfGrowthHfr = true,
            MaxStarSize = 20000, MaxHfr = 200, MinStarSize = 5
        };
        var stars = af.Detect(data, w, h);

        Assert.That(stars.Count, Is.GreaterThanOrEqualTo(1), "the donut should be detected as a star");
        var hfr = stars[0].HFR;
        Assert.That(hfr, Is.GreaterThan(8.0),
            $"donut HFR should be near the ~12px ring radius, was {hfr:0.0}");
        Assert.That(hfr, Is.LessThan(20.0), $"donut HFR should not blow up, was {hfr:0.0}");
    }

    [Test]
    public void CurveOfGrowthHfr_InFocusStar_StaysSmall() {
        // A tight in-focus star must still measure a small HFR under the same
        // AF-tuned path (the fix must not inflate good stars).
        const int w = 128, h = 128;
        var data = MakeFrame(w, h, background: 500);
        PaintStar(data, w, 64, 64, radius: 3, peak: 30000, background: 500);

        var af = new StarDetector {
            EightConnected = true, CurveOfGrowthHfr = true,
            MaxStarSize = 20000, MaxHfr = 200
        };
        var stars = af.Detect(data, w, h);

        Assert.That(stars.Count, Is.GreaterThanOrEqualTo(1));
        Assert.That(stars[0].HFR, Is.LessThan(3.0),
            $"in-focus star HFR should stay small, was {stars[0].HFR:0.0}");
    }
}