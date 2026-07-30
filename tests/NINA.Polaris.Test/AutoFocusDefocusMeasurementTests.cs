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
using NINA.Image.ImageAnalysis.AutoFocus;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// The reported failure: as a star defocuses, the measured HFR gets SMALLER
/// than at best focus, so the V-curve inverts and autofocus walks the wrong
/// way. It has been "fixed" several times in the detector without a test that
/// reproduces it, which is why it keeps coming back.
///
/// These tests measure with the SAME detector settings AutoFocusService uses,
/// on synthetic stars whose true size is known by construction, so the
/// expected answer is not a matter of opinion.
/// </summary>
[TestFixture]
public class AutoFocusDefocusMeasurementTests {

    /// <summary>Detector configured exactly as MeasureFrame does.</summary>
    private static StarDetector AfDetector() => new StarDetector {
        EightConnected = true,
        CurveOfGrowthHfr = true,
        MaxStarSize = 20000,
        MaxHfr = 200
    };

    /// <summary>
    /// A defocused star: an annulus (donut) of outer radius r with a central
    /// hole, on a noisy background. Total flux is held CONSTANT as r grows,
    /// which is what really happens when you defocus: the same photons spread
    /// over more pixels, so the peak drops as the area grows. That coupling is
    /// the whole difficulty, so the fixture has to reproduce it.
    /// </summary>
    private static ushort[] DonutField(int w, int h, double outerR, double flux,
                                       int seed = 7, ushort background = 800) {
        var img = new ushort[w * h];
        var rng = new Random(seed);
        for (int i = 0; i < img.Length; i++) {
            img[i] = (ushort)Math.Clamp(background + (int)(rng.NextDouble() * 40 - 20), 0, 65535);
        }
        // Place a few identical donuts, as a real field would have.
        var centres = new (int x, int y)[] {
            (w / 4, h / 4), (3 * w / 4, h / 4), (w / 2, h / 2),
            (w / 4, 3 * h / 4), (3 * w / 4, 3 * h / 4)
        };
        double innerR = outerR * 0.45;                       // central obstruction
        double area = Math.PI * (outerR * outerR - innerR * innerR);
        double perPixel = flux / Math.Max(1, area);
        foreach (var (cx, cy) in centres) {
            int r = (int)Math.Ceiling(outerR) + 2;
            for (int dy = -r; dy <= r; dy++) {
                for (int dx = -r; dx <= r; dx++) {
                    int x = cx + dx, y = cy + dy;
                    if (x < 0 || y < 0 || x >= w || y >= h) continue;
                    double d = Math.Sqrt(dx * dx + dy * dy);
                    if (d > outerR || d < innerR) continue;
                    int idx = y * w + x;
                    img[idx] = (ushort)Math.Clamp(img[idx] + perPixel, 0, 65535);
                }
            }
        }
        return img;
    }

    /// <summary>An in-focus star: a tight Gaussian, same total flux.</summary>
    private static ushort[] GaussianField(int w, int h, double sigma, double flux,
                                          int seed = 7, ushort background = 800) {
        var img = new ushort[w * h];
        var rng = new Random(seed);
        for (int i = 0; i < img.Length; i++) {
            img[i] = (ushort)Math.Clamp(background + (int)(rng.NextDouble() * 40 - 20), 0, 65535);
        }
        var centres = new (int x, int y)[] {
            (w / 4, h / 4), (3 * w / 4, h / 4), (w / 2, h / 2),
            (w / 4, 3 * h / 4), (3 * w / 4, 3 * h / 4)
        };
        double peak = flux / (2 * Math.PI * sigma * sigma);
        foreach (var (cx, cy) in centres) {
            int r = (int)Math.Ceiling(sigma * 5);
            for (int dy = -r; dy <= r; dy++) {
                for (int dx = -r; dx <= r; dx++) {
                    int x = cx + dx, y = cy + dy;
                    if (x < 0 || y < 0 || x >= w || y >= h) continue;
                    double v = peak * Math.Exp(-(dx * dx + dy * dy) / (2 * sigma * sigma));
                    int idx = y * w + x;
                    img[idx] = (ushort)Math.Clamp(img[idx] + v, 0, 65535);
                }
            }
        }
        return img;
    }

    private static (double hfr, int count) Measure(ushort[] img, int w, int h) {
        var stars = AfDetector().Detect(img, w, h);
        if (stars.Count == 0) return (0, 0);
        var (mean, _, _) = AutoFocusService.RobustMeanHfr(
            stars.Select(s => (double)s.HFR).ToList());
        return (mean, stars.Count);
    }

    /// <summary>
    /// The core property: sweeping OUTWARD from focus, the measured HFR must
    /// never come back down. This is the whole contract of a V-curve, and the
    /// one the field reports violated.
    /// </summary>
    [Test]
    public void MeasuredHfr_RisesMonotonically_AsDefocusGrows() {
        const int w = 600, h = 600;
        const double flux = 900_000;   // constant total flux, as when defocusing

        var results = new List<(string label, double hfr, int count)>();

        var focused = GaussianField(w, h, sigma: 2.0, flux: flux);
        var f = Measure(focused, w, h);
        results.Add(("in focus (sigma 2px)", f.hfr, f.count));

        foreach (var r in new[] { 6.0, 10.0, 16.0, 24.0, 34.0 }) {
            var img = DonutField(w, h, outerR: r, flux: flux);
            var m = Measure(img, w, h);
            results.Add(($"donut r={r,4:F0}px", m.hfr, m.count));
        }

        TestContext.Out.WriteLine("measurement with the AutoFocus detector settings:");
        foreach (var (label, hfr, count) in results) {
            TestContext.Out.WriteLine($"  {label,-22} HFR={hfr,7:F2}  stars={count}");
        }

        // Every defocused point must read larger than focus...
        for (int i = 1; i < results.Count; i++) {
            Assert.That(results[i].hfr, Is.GreaterThan(results[0].hfr),
                $"{results[i].label} measured HFR {results[i].hfr:F2}, which is not larger "
                + $"than in-focus {results[0].hfr:F2}: the V-curve is inverted here");
        }
        // ...and each step outward must not come back down.
        for (int i = 2; i < results.Count; i++) {
            Assert.That(results[i].hfr, Is.GreaterThan(results[i - 1].hfr * 0.9),
                $"{results[i].label} ({results[i].hfr:F2}) dropped below "
                + $"{results[i - 1].label} ({results[i - 1].hfr:F2}) while defocusing further");
        }
    }

    /// <summary>
    /// The guard that is supposed to catch whatever the detector gets wrong.
    /// It picks the vertex as the GLOBAL MINIMUM HFR, which the bug itself
    /// corrupts: if a far-out point reads a bogus small HFR, that point becomes
    /// the "vertex" and the series then looks monotonic from there, so nothing
    /// is flagged and the fit is handed an inverted curve.
    /// </summary>
    [Test]
    public void MarkLowWingOutliers_FlagsTheBogusPoint_NotTheRealFocus() {
        // A sweep where the outermost left sample reads 1.1 (detector lost the
        // faint donut) while real focus is 3.0. Positions ascend.
        var pts = new List<AutoFocusPoint> {
            new() { Position = 5000, HFR = 1.1, HfrError = 0.4 },   // bogus, far out
            new() { Position = 5100, HFR = 9.0, HfrError = 0.5 },
            new() { Position = 5200, HFR = 6.0, HfrError = 0.4 },
            new() { Position = 5300, HFR = 3.0, HfrError = 0.2 },   // real focus
            new() { Position = 5400, HFR = 6.2, HfrError = 0.4 },
            new() { Position = 5500, HFR = 9.1, HfrError = 0.5 },
            new() { Position = 5600, HFR = 12.0, HfrError = 0.6 },
        };

        AutoFocusService.MarkLowWingOutliers(pts);

        var flagged = pts.Where(p => p.Rejected).Select(p => p.Position).ToList();
        TestContext.Out.WriteLine("flagged positions: "
            + (flagged.Count == 0 ? "(none)" : string.Join(", ", flagged)));

        Assert.That(pts.Single(p => p.Position == 5000).Rejected, Is.True,
            "the far-out point reading 1.1 is unphysical and must be soft-rejected");
        Assert.That(pts.Single(p => p.Position == 5300).Rejected, Is.False,
            "real focus must never be flagged");
    }

    /// <summary>
    /// The refinement pass fits ONLY its own points, so the coarse arms cannot
    /// drag its vertex. This checks the property that makes the second pass
    /// worth its frames: a fine cluster whose true minimum sits between two
    /// coarse samples must resolve to that minimum, not to a coarse sample.
    /// </summary>
    [Test]
    public void RefinementFit_FindsAMinimumBetweenCoarseSamples() {
        // True focus at 5312, coarse step 50 so the sweep could only ever
        // report 5300 or 5350. Fine step 12 around the coarse answer.
        const double trueFocus = 5312;
        var fine = new List<FocusPoint>();
        foreach (var pos in new[] { 5282, 5294, 5306, 5318, 5330 }) {
            double d = pos - trueFocus;
            double hfr = 2.0 + 0.0006 * d * d;       // shallow bowl, as it is near focus
            fine.Add(new FocusPoint(pos, hfr, 0.05));
        }

        var fit = new QuadraticFitting().Calculate(fine);

        Assert.That(fit.HasFit, Is.True);
        Assert.That(fit.A2, Is.GreaterThan(0), "a refinement fit has to be a bowl");
        Assert.That(fit.Minimum.X, Is.EqualTo(trueFocus).Within(3),
            $"fine fit put focus at {fit.Minimum.X:F0}, true focus was {trueFocus}: "
            + "the coarse sweep could only have said 5300 or 5350");
    }

    /// <summary>
    /// Single-star mode, which is now the default: the tracker must keep
    /// measuring the SAME star as the sweep defocuses, even though a
    /// heavily defocused donut spreads and the brightest-by-flux ranking of the
    /// field changes. Following the field average instead is what the operator
    /// reported: the number moved with whatever the detector found, not with
    /// the focus.
    /// </summary>
    [Test]
    public void SingleStarTracker_KeepsTheSameStar_AsItDefocuses() {
        var tracker = new AfStarTracker(1);

        // Frame 1, near focus: star A is the brightest.
        var near = new List<DetectedStar> {
            new() { X = 100, Y = 100, HFR = 2.0, Flux = 9000 },   // A
            new() { X = 400, Y = 300, HFR = 2.1, Flux = 8000 },   // B
        };
        var pickedNear = tracker.Filter(near);
        Assert.That(pickedNear, Has.Count.EqualTo(1));
        Assert.That(pickedNear[0].X, Is.EqualTo(100));

        // Frame 2, defocused: A has spread and now reads FAINTER than B, and A
        // has drifted a couple of pixels. Ranking by flux again would jump to
        // B and put a step in the curve that is not defocus.
        var far = new List<DetectedStar> {
            new() { X = 102, Y =  99, HFR = 9.0, Flux = 5200 },   // A, spread out
            new() { X = 400, Y = 300, HFR = 8.8, Flux = 7000 },   // B, still brighter
        };
        var pickedFar = tracker.Filter(far);
        Assert.That(pickedFar, Has.Count.EqualTo(1));
        Assert.That(pickedFar[0].X, Is.EqualTo(102),
            "the anchor must follow star A by position, not re-rank by brightness");
        Assert.That(pickedFar[0].HFR, Is.EqualTo(9.0).Within(1e-9),
            "and the point's HFR is that star's, so the curve is one star's defocus");
    }
}
