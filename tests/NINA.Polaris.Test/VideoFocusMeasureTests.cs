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

using NINA.Polaris.Services;
using NINA.Polaris.Services.Focus;
using NINA.Polaris.Services.Planetary;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Tap to focus on the VIDEO stream: the focus region, the contrast measure
/// the video autofocus fits, and the options a video run resolves to.
/// </summary>
[TestFixture]
public class VideoFocusMeasureTests {

    // ─── FocusRegion ──────────────────────────────────────────────

    [Test]
    public void Region_NearTheEdge_IsMovedInsideTheFrame() {
        var r = FocusRegion.From(0.9, -0.1, 0.25, 0.25)!.Value;
        Assert.That(r.X, Is.EqualTo(0.75).Within(1e-9));
        Assert.That(r.Y, Is.EqualTo(0).Within(1e-9));
        Assert.That(r.W, Is.EqualTo(0.25).Within(1e-9));
    }

    [Test]
    public void Region_MissingOrEmpty_IsNull() {
        Assert.That(FocusRegion.From(null, 0.1, 0.2, 0.2), Is.Null);
        Assert.That(FocusRegion.From(0.1, 0.1, 0, 0.2), Is.Null);
        Assert.That(FocusRegion.From(double.NaN, 0.1, 0.2, 0.2), Is.Null);
    }

    [Test]
    public void Region_OnAMosaic_KeepsTheBayerPhase() {
        var r = new FocusRegion(0.301, 0.401, 0.2, 0.2);
        var (x, y, w, h) = r.ToPixels(641, 481, evenAlign: true);
        Assert.That(x % 2, Is.Zero);
        Assert.That(y % 2, Is.Zero);
        Assert.That(w % 2, Is.Zero);
        Assert.That(h % 2, Is.Zero);
        Assert.That(x + w, Is.LessThanOrEqualTo(641));
        Assert.That(y + h, Is.LessThanOrEqualTo(481));
    }

    [Test]
    public void Region_TinyBox_GetsTheMinimumSide() {
        var (_, _, w, h) = new FocusRegion(0.5, 0.5, 0.001, 0.001).ToPixels(1000, 800, evenAlign: false);
        Assert.That(w, Is.EqualTo(FocusRegion.MinSidePx));
        Assert.That(h, Is.EqualTo(FocusRegion.MinSidePx));
    }

    [Test]
    public void Crop_TakesTheTappedArea() {
        // Left half dark, right half bright: a region on the right reads bright.
        const int W = 200, H = 100;
        var px = new ushort[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                px[y * W + x] = (ushort)(x < W / 2 ? 10 : 1000);
        var (data, w, h) = VideoFocusMeasure.Crop(px, W, H, new FocusRegion(0.6, 0.2, 0.3, 0.5), bayered: false);
        Assert.That(w, Is.EqualTo(60));
        Assert.That(h, Is.EqualTo(50));
        Assert.That(data.All(v => v == 1000), Is.True);
    }

    // ─── Contrast measure ─────────────────────────────────────────

    /// <summary>A crater field: bright discs with dark rims on a grey
    /// plain, then box-blurred by <paramref name="blur"/> pixels to stand in
    /// for defocus.</summary>
    private static ushort[] Craters(int w, int h, int blur, double brightness = 1.0) {
        var img = new double[w * h];
        var rng = new Random(7);
        for (int i = 0; i < img.Length; i++) img[i] = 800;
        for (int c = 0; c < 25; c++) {
            int cx = rng.Next(w), cy = rng.Next(h), r = rng.Next(3, 10);
            for (int y = Math.Max(0, cy - r - 2); y < Math.Min(h, cy + r + 2); y++)
                for (int x = Math.Max(0, cx - r - 2); x < Math.Min(w, cx + r + 2); x++) {
                    double d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                    if (d < r) img[y * w + x] = 1400;
                    else if (d < r + 2) img[y * w + x] = 300;
                }
        }
        var outp = new ushort[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) {
                double s = 0; int n = 0;
                for (int j = -blur; j <= blur; j++)
                    for (int i = -blur; i <= blur; i++) {
                        int xx = Math.Clamp(x + i, 0, w - 1), yy = Math.Clamp(y + j, 0, h - 1);
                        s += img[yy * w + xx]; n++;
                    }
                outp[y * w + x] = (ushort)Math.Round(s / n * brightness);
            }
        return outp;
    }

    [Test]
    public void Contrast_FallsAsTheImageDefocuses() {
        double sharp = VideoFocusMeasure.RelativeContrast(Craters(128, 128, 0), 128, 128);
        double soft = VideoFocusMeasure.RelativeContrast(Craters(128, 128, 2), 128, 128);
        double softer = VideoFocusMeasure.RelativeContrast(Craters(128, 128, 4), 128, 128);
        Assert.That(sharp, Is.GreaterThan(soft));
        Assert.That(soft, Is.GreaterThan(softer));
    }

    [Test]
    public void Contrast_IgnoresAThinCloud() {
        // A cloud dims everything by the same factor: the edges and the plain
        // dim together, so the ratio stays where it was.
        double clear = VideoFocusMeasure.RelativeContrast(Craters(128, 128, 1), 128, 128);
        double cloud = VideoFocusMeasure.RelativeContrast(Craters(128, 128, 1, brightness: 0.6), 128, 128);
        Assert.That(cloud, Is.EqualTo(clear).Within(clear * 0.02));
    }

    [Test]
    public void Contrast_FlatOrBlackRegion_IsZero() {
        Assert.That(VideoFocusMeasure.RelativeContrast(new ushort[64 * 64], 64, 64), Is.Zero);
        var flat = Enumerable.Repeat((ushort)500, 64 * 64).ToArray();
        Assert.That(VideoFocusMeasure.RelativeContrast(flat, 64, 64), Is.Zero.Within(1e-12));
    }

    [Test]
    public void BetterHalf_KeepsTheSharpestFrames() {
        var half = VideoFocusMeasure.BetterHalf(new[] { 1.0, 5.0, 2.0, 4.0, 3.0 });
        Assert.That(half.OrderBy(v => v), Is.EqualTo(new[] { 3.0, 4.0, 5.0 }));
    }

    // ─── Sharpness bar window ─────────────────────────────────────

    [Test]
    public void LaplacianVariance_CentredSquare_MatchesTheSameWindow() {
        var px = Craters(120, 90, 1);
        double centred = FrameQualityAnalyzer.LaplacianVariance(px, 120, 90, 40);
        double window = FrameQualityAnalyzer.LaplacianVariance(px, 120, 90, 40, 25, 80, 65);
        Assert.That(window, Is.EqualTo(centred));
    }

    [Test]
    public void LaplacianVariance_WindowAtTheEdge_StaysInsideTheFrame() {
        var px = Craters(60, 60, 0);
        Assert.DoesNotThrow(() => FrameQualityAnalyzer.LaplacianVariance(px, 60, 60, 0, 0, 60, 60));
        Assert.That(FrameQualityAnalyzer.LaplacianVariance(px, 60, 60, 0, 0, 60, 60), Is.GreaterThan(0));
    }

    // ─── Options ──────────────────────────────────────────────────

    [Test]
    public void Resolve_FromStream_MeasuresContrastWithoutBinningOrCrop() {
        var o = AutoFocusRunOptions.Resolve(new AutoFocusRequest {
            FromStream = true, Metric = "HFR", Binning = 2, InnerCropRatio = 0.5,
            RegionX = 0.1, RegionY = 0.2, RegionW = 0.25, RegionH = 0.3
        }, new AutoFocusSettings());
        Assert.That(o.FromStream, Is.True);
        Assert.That(o.Metric, Is.EqualTo(AutoFocusMetric.ContrastLaplace));
        Assert.That(o.Method, Is.EqualTo(NINA.Image.ImageAnalysis.AutoFocus.AFCurveFittingMethod.Parabolic));
        Assert.That(o.Binning, Is.EqualTo(1));
        Assert.That(o.InnerCropRatio, Is.EqualTo(1.0));
        Assert.That(o.FramesPerPoint, Is.EqualTo(8));
        Assert.That(o.Region, Is.EqualTo(new FocusRegion(0.1, 0.2, 0.25, 0.3)));
    }

    [Test]
    public void Resolve_FromStream_KeepsSobelAndClampsTheFrames() {
        var o = AutoFocusRunOptions.Resolve(new AutoFocusRequest {
            FromStream = true, Metric = "CONTRAST_SOBEL", StreamFramesPerPoint = 100
        }, new AutoFocusSettings());
        Assert.That(o.Metric, Is.EqualTo(AutoFocusMetric.ContrastSobel));
        Assert.That(o.FramesPerPoint, Is.EqualTo(30));
        Assert.That(o.Region, Is.Null);
    }

    [Test]
    public void Resolve_WithoutStream_IsUnchanged() {
        var o = AutoFocusRunOptions.Resolve(new AutoFocusRequest { Binning = 2 }, new AutoFocusSettings());
        Assert.That(o.FromStream, Is.False);
        Assert.That(o.Binning, Is.EqualTo(2));
        Assert.That(o.Region, Is.Null);
    }
}
