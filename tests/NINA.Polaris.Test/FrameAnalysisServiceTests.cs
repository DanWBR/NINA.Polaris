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

using Microsoft.Extensions.Logging.Abstractions;
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Polaris.Services.Studio;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Pins the FrameAnalysisService verdicts against synthetic frames with
/// planted Gaussian stars: even round field -> clean; an HFR ramp across
/// X -> tilt flagged; radially-elongated corners -> coma flagged. Also
/// covers the new StarDetector eccentricity field.
/// </summary>
[TestFixture]
public class FrameAnalysisServiceTests {

    private string _tmpDir = "";
    private FrameAnalysisService _svc = null!;
    private const int W = 600, H = 400, Bg = 100;

    [SetUp]
    public void SetUp() {
        _tmpDir = Path.Combine(Path.GetTempPath(), "polaris-analyze-" + Guid.NewGuid());
        Directory.CreateDirectory(_tmpDir);
        _svc = new FrameAnalysisService(NullLogger<FrameAnalysisService>.Instance);
    }

    [TearDown]
    public void TearDown() {
        if (Directory.Exists(_tmpDir)) Directory.Delete(_tmpDir, recursive: true);
    }

    [Test]
    public void Analyze_UniformRoundField_IsClean() {
        var data = NewBg();
        // 10x6 grid of identical round stars.
        ForEachGridStar((gx, gy) => PlantStar(data, gx, gy, 2.0, 2.0, 0, 2200));
        var r = _svc.Analyze(WriteFits(data, "uniform.fits"));

        Assert.That(r.StarCount, Is.GreaterThan(30), "should detect the planted grid");
        Assert.That(r.Tilt.Severity, Is.EqualTo("good"), r.Tilt.Verdict);
        Assert.That(r.Aberration.DominantType, Is.EqualTo("none"), r.Aberration.Verdict);
    }

    [Test]
    public void Analyze_HfrRampAcrossX_FlagsTilt() {
        var data = NewBg();
        // Sharp on the left, soft on the right -> asymmetric gradient.
        ForEachGridStar((gx, gy) => {
            double sigma = 1.5 + 2.0 * (gx / (double)(W - 1)); // 1.5 -> 3.5
            PlantStar(data, gx, gy, sigma, sigma, 0, 2200);
        });
        var r = _svc.Analyze(WriteFits(data, "ramp.fits"));

        Assert.That(r.Tilt.Severity, Is.Not.EqualTo("good"), r.Tilt.Verdict);
        Assert.That(r.Tilt.WorstCorner, Does.Contain("right"), r.Tilt.Verdict);
        // A pure focus/HFR gradient with round stars is not an aberration.
        Assert.That(r.Aberration.DominantType, Is.AnyOf("none", "field-curvature"));
    }

    [Test]
    public void Analyze_RadiallyElongatedCorners_FlagsComa() {
        var data = NewBg();
        double cx = W / 2.0, cy = H / 2.0;
        ForEachGridStar((gx, gy) => {
            double dist = Math.Sqrt((gx - cx) * (gx - cx) + (gy - cy) * (gy - cy));
            double frac = dist / Math.Sqrt(cx * cx + cy * cy); // 0 centre -> 1 corner
            if (frac < 0.35) {
                PlantStar(data, gx, gy, 2.0, 2.0, 0, 2200);      // round centre
            } else {
                double ang = Math.Atan2(gy - cy, gx - cx);       // radial axis
                PlantStar(data, gx, gy, 1.6 + 2.4 * frac, 1.6, ang, 2200); // stretched outward
            }
        });
        var r = _svc.Analyze(WriteFits(data, "coma.fits"));

        Assert.That(r.Aberration.DominantType, Is.EqualTo("coma"), r.Aberration.Verdict);
        Assert.That(r.Aberration.EdgeEcc, Is.GreaterThan(0.4));
    }

    [Test]
    public void StarDetector_ElongatedBlob_HasHigherEccentricity() {
        var round = NewBg();
        PlantStar(round, 300, 200, 2.5, 2.5, 0, 3000);
        var es1 = new StarDetector { MaxStarSize = 1500 }.Detect(round, W, H);

        var elong = NewBg();
        PlantStar(elong, 300, 200, 5.0, 1.6, 0, 3000);
        var es2 = new StarDetector { MaxStarSize = 1500 }.Detect(elong, W, H);

        Assert.That(es1, Is.Not.Empty);
        Assert.That(es2, Is.Not.Empty);
        Assert.That(es1[0].Eccentricity, Is.LessThan(0.3), "round star should be near-circular");
        Assert.That(es2[0].Eccentricity, Is.GreaterThan(es1[0].Eccentricity + 0.3),
            "elongated star should be clearly more eccentric");
    }

    // ---- helpers ----------------------------------------------------

    private static ushort[] NewBg() {
        var d = new ushort[W * H];
        for (int i = 0; i < d.Length; i++) d[i] = Bg;
        return d;
    }

    // Plant a (possibly elongated, rotated) Gaussian star. sx/sy are the
    // sigmas along the major/minor axes; theta rotates them.
    private static void PlantStar(ushort[] d, double cx, double cy,
                                  double sx, double sy, double theta, double amp) {
        double cos = Math.Cos(theta), sin = Math.Sin(theta);
        int rad = (int)Math.Ceiling(3.5 * Math.Max(sx, sy));
        for (int y = (int)cy - rad; y <= cy + rad; y++) {
            if (y < 0 || y >= H) continue;
            for (int x = (int)cx - rad; x <= cx + rad; x++) {
                if (x < 0 || x >= W) continue;
                double dx = x - cx, dy = y - cy;
                double xr = dx * cos + dy * sin;     // along major axis
                double yr = -dx * sin + dy * cos;    // along minor axis
                double g = amp * Math.Exp(-0.5 * (xr * xr / (sx * sx) + yr * yr / (sy * sy)));
                int v = d[y * W + x] + (int)g;
                d[y * W + x] = (ushort)Math.Min(65535, v);
            }
        }
    }

    // 10x6 evenly spaced grid, inset from the edges.
    private static void ForEachGridStar(Action<int, int> plant) {
        for (int gx = 1; gx <= 10; gx++)
            for (int gy = 1; gy <= 6; gy++)
                plant(gx * W / 11, gy * H / 7);
    }

    private string WriteFits(ushort[] data, string name) {
        var props = new ImageProperties { Width = W, Height = H, BitDepth = 16, Channels = 1 };
        var path = Path.Combine(_tmpDir, name);
        FITSWriter.Write(new BaseImageData(data, props), path);
        return path;
    }
}