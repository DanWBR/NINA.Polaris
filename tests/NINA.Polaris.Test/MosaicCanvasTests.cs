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
using System.Linq;
using NUnit.Framework;
using NINA.Core.Enum;
using NINA.Image.FileFormat.FITS;
using NINA.Polaris.Services.Mosaic;

namespace NINA.Polaris.Test;

[TestFixture]
public class MosaicCanvasTests {
    private const double Scale = 2.0 / 3600;   // 2"/px
    private const int W = 600, H = 400;

    /// <summary>A CD matrix rotated by <paramref name="rotDeg"/>, mirrored
    /// when asked (the parity of a train with a diagonal or a flip).</summary>
    private static WcsInfo Cd(double raDeg, double decDeg, double rotDeg, bool mirrored = false,
            double scale = Scale, int w = W, int h = H) {
        double c = Math.Cos(rotDeg * Math.PI / 180), s = Math.Sin(rotDeg * Math.PI / 180);
        double m = mirrored ? -1 : 1;
        return new WcsInfo {
            RaDeg = raDeg, DecDeg = decDeg,
            RefPixelX = (w + 1) / 2.0, RefPixelY = (h + 1) / 2.0,
            CD11 = -scale * c * m, CD12 = scale * s,
            CD21 = scale * s * m, CD22 = scale * c,
        };
    }

    [TestCase(1.0, 0.2, 1)]
    [TestCase(1.8, 0.2, 2)]
    [TestCase(2.0, 0.2, 3)]
    [TestCase(3.0, 0.2, 4)]
    [TestCase(1.5, 0.1, 2)]
    public void PanelCount_CoversTheField(double zoom, double overlap, int expected) {
        int n = MosaicLayout.PanelCount(zoom, overlap);
        Assert.That(n, Is.EqualTo(expected));
        if (n > 1) {
            Assert.That(n * (1 - overlap) + overlap, Is.GreaterThanOrEqualTo(zoom - 1e-9));
            Assert.That(MosaicLayout.ActualOverlap(zoom, n), Is.GreaterThanOrEqualTo(overlap - 1e-9));
        }
    }

    [Test]
    public void Plan_StepsAlongTheSensorAxes_SerpentineFromTheTop() {
        var reference = Cd(10, 41, 30);
        var g = MosaicLayout.Plan(10 / 15.0, 41, reference, W, H, 2.5, 1.6, 20);

        Assert.That(g.Cols, Is.EqualTo(3));
        Assert.That(g.Rows, Is.EqualTo(2));
        Assert.That(g.Panels.Select(p => (p.Row, p.Col)), Is.EqualTo(new[] {
            (0, 0), (0, 1), (0, 2), (1, 2), (1, 1), (1, 0) }));

        // Neighbours along a row sit one step apart along the sensor's X axis:
        // project both into the target frame and compare pixel offsets.
        var target = Cd(10, 41, 30);
        var (ax, ay) = target.RaDecToPixel(g.Panels[0].RaHours * 15, g.Panels[0].DecDeg);
        var (bx, by) = target.RaDecToPixel(g.Panels[1].RaHours * 15, g.Panels[1].DecDeg);
        double stepPx = MosaicLayout.Step(2.5, 3) * W;
        Assert.That(bx - ax, Is.EqualTo(stepPx).Within(0.5));
        Assert.That(by - ay, Is.EqualTo(0).Within(0.5));

        // The planned rectangles span the canvas exactly.
        Assert.That(g.Panels.Min(p => p.X0), Is.EqualTo(0).Within(1));
        Assert.That(g.Panels.Max(p => p.X1), Is.EqualTo(g.CanvasWidth).Within(1));
        Assert.That(g.Panels.Min(p => p.Y0), Is.EqualTo(0).Within(1));
        Assert.That(g.Panels.Max(p => p.Y1), Is.EqualTo(g.CanvasHeight).Within(1));
    }

    [Test]
    public void Plan_LargeField_CanvasStaysWithinTheScreenBudget() {
        var g = MosaicLayout.Plan(5, 20, Cd(75, 20, 0, scale: 1.0 / 3600, w: 6248, h: 4176),
            6248, 4176, 4, 3, 15);
        Assert.That(Math.Max(g.CanvasWidth, g.CanvasHeight), Is.LessThanOrEqualTo(MosaicLayout.MaxCanvasSide));
        Assert.That(g.CanvasWidth / (double)g.CanvasHeight, Is.EqualTo(4.0 * 6248 / (3.0 * 4176)).Within(0.01));
    }

    private readonly record struct Star(double RaDeg, double DecDeg, double Peak);

    private static List<Star> SkyAround(double raDeg, double decDeg, double radiusDeg, int count, int seed) {
        var rng = new Random(seed);
        var stars = new List<Star>();
        double cosDec = Math.Cos(decDeg * Math.PI / 180);
        for (int i = 0; i < count; i++) {
            double dra = (rng.NextDouble() * 2 - 1) * radiusDeg / cosDec;
            double ddec = (rng.NextDouble() * 2 - 1) * radiusDeg;
            stars.Add(new Star(raDeg + dra, decDeg + ddec, 4000 + rng.NextDouble() * 20000));
        }
        return stars;
    }

    /// <summary>Render the sky as a camera with this WCS would see it.</summary>
    private static ushort[] Render(IEnumerable<Star> sky, WcsInfo wcs, int w, int h, double sigma = 1.4) {
        var img = new double[w * h];
        Array.Fill(img, 1000.0);
        foreach (var s in sky) {
            var (px, py) = wcs.RaDecToPixel(s.RaDeg, s.DecDeg);
            if (!double.IsFinite(px)) continue;
            double cx = px - 1, cy = py - 1;   // 0-based
            if (cx < -5 || cy < -5 || cx > w + 5 || cy > h + 5) continue;
            for (int y = (int)cy - 5; y <= (int)cy + 5; y++)
                for (int x = (int)cx - 5; x <= (int)cx + 5; x++) {
                    if (x < 0 || y < 0 || x >= w || y >= h) continue;
                    double d2 = (x - cx) * (x - cx) + (y - cy) * (y - cy);
                    img[y * w + x] += s.Peak * Math.Exp(-d2 / (2 * sigma * sigma));
                }
        }
        return img.Select(v => (ushort)Math.Min(65535, v)).ToArray();
    }

    [TestCase(25.0, false)]
    [TestCase(-110.0, true)]
    public void Composite_PlacesEveryStarWhereTheSkySaysItIs(double rotDeg, bool mirrored) {
        double ra0 = 83.8, dec0 = -5.4;
        var reference = Cd(ra0, dec0, rotDeg, mirrored);
        var g = MosaicLayout.Plan(ra0 / 15, dec0, reference, W, H, 2.5, 1.6, 20);
        var canvas = new MosaicCanvas(g.CanvasWidth, g.CanvasHeight, g.CanvasWcs);
        var sky = SkyAround(ra0, dec0, 0.6, 1500, 7);

        // Each panel lands a little off (pointing error) and a little turned
        // (field rotation); its own WCS says exactly how.
        var rng = new Random(3);
        foreach (var p in g.Panels) {
            double errRa = (rng.NextDouble() - 0.5) * 60 / 3600;
            double errDec = (rng.NextDouble() - 0.5) * 60 / 3600;
            var wcs = Cd(p.RaHours * 15 + errRa, p.DecDeg + errDec,
                rotDeg + (rng.NextDouble() - 0.5) * 0.6, mirrored);
            var tile = MosaicTile.FromFrame(Render(sky, wcs, W, H), W, H, BayerPatternEnum.None,
                wcs, canvas.ReductionFor(wcs));
            Assert.That(canvas.Add(tile), Is.Not.Null);
        }

        // Nothing left uncovered inside the field. The pointing error (up to
        // 15 px here) can only nibble at the outer edge.
        int holes = 0;
        for (int y = 20; y < canvas.Height - 20; y++)
            for (int x = 20; x < canvas.Width - 20; x++)
                if (float.IsNaN(canvas.ValueAt(x, y).r)) holes++;
        Assert.That(holes, Is.Zero);

        // Every bright star well inside the canvas: centroid within a third of
        // a pixel of its catalogue position, including the ones in overlaps.
        int checkedStars = 0;
        foreach (var s in sky.Where(s => s.Peak > 8000)) {
            var (px, py) = canvas.Wcs.RaDecToPixel(s.RaDeg, s.DecDeg);
            double cx = px - 1, cy = py - 1;
            if (cx < 10 || cy < 10 || cx > canvas.Width - 10 || cy > canvas.Height - 10) continue;
            // Skip blends: another star within 8 px confuses the centroid.
            if (sky.Any(o => !o.Equals(s) && Dist(canvas.Wcs, s, o) < 8)) continue;
            double sw = 0, sx = 0, sy = 0;
            for (int y = (int)cy - 3; y <= (int)cy + 3; y++)
                for (int x = (int)cx - 3; x <= (int)cx + 3; x++) {
                    float v = canvas.ValueAt(x, y).r;
                    if (float.IsNaN(v) || v <= 0) continue;
                    sw += v; sx += v * x; sy += v * y;
                }
            Assert.That(sw, Is.GreaterThan(0));
            Assert.That(sx / sw, Is.EqualTo(cx).Within(0.35), $"star at {s.RaDeg:F4},{s.DecDeg:F4}");
            Assert.That(sy / sw, Is.EqualTo(cy).Within(0.35), $"star at {s.RaDeg:F4},{s.DecDeg:F4}");
            checkedStars++;
        }
        Assert.That(checkedStars, Is.GreaterThan(100));

        var rgb = canvas.RenderRgb();
        Assert.That(rgb.Length, Is.EqualTo(canvas.Width * canvas.Height * 3));
        Assert.That(canvas.RenderJpeg().Length, Is.GreaterThan(1000));
    }

    private static double Dist(WcsInfo wcs, Star a, Star b) {
        var (ax, ay) = wcs.RaDecToPixel(a.RaDeg, a.DecDeg);
        var (bx, by) = wcs.RaDecToPixel(b.RaDeg, b.DecDeg);
        return Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));
    }

    [Test]
    public void Tile_SuperpixelDebayer_SeparatesTheChannels() {
        // RGGB with a bright patch on the red sites only.
        int w = 64, h = 64;
        var cfa = new ushort[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) {
                bool red = (y & 1) == 0 && (x & 1) == 0;
                bool patch = x >= 20 && x < 40 && y >= 20 && y < 40;
                cfa[y * w + x] = (ushort)(1000 + (red && patch ? 4000 : 0));
            }
        var tile = MosaicTile.FromFrame(cfa, w, h, BayerPatternEnum.RGGB, Cd(0, 0, 0, w: w, h: h), 1);

        Assert.That(tile.Bin, Is.EqualTo(2));
        Assert.That(tile.IsColour, Is.True);
        Assert.That(tile.Width, Is.EqualTo(32));
        int inside = 15 * tile.Width + 15;
        Assert.That(tile.R[inside], Is.EqualTo(4000).Within(1));
        Assert.That(tile.G[inside], Is.EqualTo(0).Within(1));
        Assert.That(tile.B[inside], Is.EqualTo(0).Within(1));
        Assert.That(tile.R[2 * tile.Width + 2], Is.EqualTo(0).Within(1));
    }

    [Test]
    public void Composite_ReducedTile_KeepsPlacement() {
        // A frame twice as fine as the canvas: binned 2x2 on the way in, and
        // the star still lands on its catalogue position.
        double ra0 = 150, dec0 = 60;
        var canvasWcs = Cd(ra0, dec0, 10, scale: 2 * Scale, w: 400, h: 300);
        var canvas = new MosaicCanvas(400, 300, canvasWcs);
        var frame = Cd(ra0, dec0, 12, scale: Scale, w: 700, h: 500);
        var sky = new List<Star> { new(ra0 + 0.02, dec0 + 0.01, 30000) };
        var tile = MosaicTile.FromFrame(Render(sky, frame, 700, 500, sigma: 2.5), 700, 500,
            BayerPatternEnum.None, frame, canvas.ReductionFor(frame));
        Assert.That(tile.Bin, Is.EqualTo(2));
        canvas.Add(tile);

        var (px, py) = canvasWcs.RaDecToPixel(sky[0].RaDeg, sky[0].DecDeg);
        double cx = px - 1, cy = py - 1, sw = 0, sx = 0, sy = 0;
        for (int y = (int)cy - 4; y <= (int)cy + 4; y++)
            for (int x = (int)cx - 4; x <= (int)cx + 4; x++) {
                float v = canvas.ValueAt(x, y).r;
                if (v <= 0) continue;
                sw += v; sx += v * x; sy += v * y;
            }
        Assert.That(sx / sw, Is.EqualTo(cx).Within(0.3));
        Assert.That(sy / sw, Is.EqualTo(cy).Within(0.3));
    }
}
