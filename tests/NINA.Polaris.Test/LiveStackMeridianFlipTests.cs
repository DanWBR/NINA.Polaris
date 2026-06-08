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
using NINA.Image.ImageData;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Part B: the live stacker must keep integrating after a GEM meridian
/// flip. A post-flip frame arrives ~180-deg rotated (plus a small
/// residual offset from an imperfect recentre); the orientation-aware
/// alignment probe must detect that, warp it back onto the reference
/// grid, integrate it (frame count advances), and flag the flip -- all
/// without the equipment / meridian services (pure alignment auto-detect).
/// </summary>
[TestFixture]
public class LiveStackMeridianFlipTests {

    private const int W = 300, H = 300, Bg = 100;

    // Spread, non-collinear, asymmetric star field so the affine fit is
    // well-conditioned and a 180 rotation is unambiguous (no accidental
    // translation-only match on the rotated set).
    private static readonly (double x, double y)[] Stars = {
        (60, 70), (220, 80), (120, 210), (200, 160),
        (90, 185), (250, 250), (150, 120), (45, 245),
    };

    private static LiveStackingService MakeService() {
        var relay = new ImageRelayService(NullLogger<ImageRelayService>.Instance);
        // equipment + meridian left null -> exercise the alignment-based
        // auto-detect path (B1) with no pier-side hint.
        return new LiveStackingService(relay, NullLogger<LiveStackingService>.Instance);
    }

    [Test]
    public async Task AddFrame_PostFlipRotatedFrame_IsReorientedAndIntegrated() {
        var svc = MakeService();
        svc.Start();

        // Reference frame.
        await svc.AddFrameAsync(MakeFrame(Stars));
        Assert.That(svc.FrameCount, Is.EqualTo(1));
        Assert.That(svc.MeridianFlipsHandled, Is.EqualTo(0));

        // Post-flip frame: every star rotated 180 about the image centre
        // plus a small residual offset (imperfect recentre).
        const int OffX = 8, OffY = -5;
        var flipped = Stars
            .Select(s => ((W - 1) - s.x + OffX, (H - 1) - s.y + OffY))
            .ToArray();
        await svc.AddFrameAsync(MakeFrame(flipped));

        // The flipped frame must have been re-oriented + integrated, not
        // rejected: frame count advances and the flip is recorded.
        Assert.That(svc.FrameCount, Is.EqualTo(2),
            "Post-flip frame should be re-oriented and stacked, not rejected.");
        Assert.That(svc.MeridianFlipsHandled, Is.EqualTo(1),
            "The orientation change should be counted as a handled flip.");
        Assert.That(svc.GetStatus().MeridianFlipsHandled, Is.EqualTo(1));

        // No ghosting: the stacked result is bright at the reference star
        // positions. Planted star peak is amp+bg ~= 5100. With BOTH frames
        // contributing a full star (running mean), the value stays ~5100;
        // if the flipped frame had landed elsewhere only frame 1 would
        // contribute and the mean would drop to ~2600. >4000 confirms the
        // post-flip frame was re-oriented onto the reference grid.
        var stacked = svc.GetStackedResult();
        foreach (var (x, y) in Stars) {
            int idx = (int)y * W + (int)x;
            Assert.That(stacked[idx], Is.GreaterThan(4000),
                $"Reference star ({x},{y}) should stay bright (both frames " +
                $"contributing) after re-orienting the flip; got {stacked[idx]}.");
        }
    }

    [Test]
    public async Task AddFrame_NonFlippedDrift_DoesNotCountAsFlip() {
        var svc = MakeService();
        svc.Start();

        await svc.AddFrameAsync(MakeFrame(Stars));
        // Same orientation, small drift -> normal alignment, no flip.
        var drifted = Stars.Select(s => (s.x + 4, s.y + 3)).ToArray();
        await svc.AddFrameAsync(MakeFrame(drifted));

        Assert.That(svc.FrameCount, Is.EqualTo(2));
        Assert.That(svc.MeridianFlipsHandled, Is.EqualTo(0),
            "A normal small drift must not be mistaken for a meridian flip.");
    }

    [Test]
    public async Task Reset_ClearsMeridianFlipCount() {
        var svc = MakeService();
        svc.Start();
        await svc.AddFrameAsync(MakeFrame(Stars));
        var flipped = Stars.Select(s => ((double)(W - 1) - s.x, (double)(H - 1) - s.y)).ToArray();
        await svc.AddFrameAsync(MakeFrame(flipped));
        Assert.That(svc.MeridianFlipsHandled, Is.EqualTo(1));

        svc.Reset();
        Assert.That(svc.MeridianFlipsHandled, Is.EqualTo(0));
    }

    // ---- helpers ----------------------------------------------------

    private static BaseImageData MakeFrame((double x, double y)[] stars) {
        var data = new ushort[W * H];
        for (int i = 0; i < data.Length; i++) data[i] = Bg;
        foreach (var (cx, cy) in stars) PlantStar(data, cx, cy, sigma: 2.0, amp: 5000);
        var props = new ImageProperties { Width = W, Height = H, BitDepth = 16, Channels = 1 };
        return new BaseImageData(data, props);
    }

    private static void PlantStar(ushort[] d, double cx, double cy, double sigma, double amp) {
        int rad = (int)Math.Ceiling(3.5 * sigma);
        for (int y = (int)cy - rad; y <= cy + rad; y++) {
            if (y < 0 || y >= H) continue;
            for (int x = (int)cx - rad; x <= cx + rad; x++) {
                if (x < 0 || x >= W) continue;
                double dx = x - cx, dy = y - cy;
                double g = amp * Math.Exp(-0.5 * (dx * dx + dy * dy) / (sigma * sigma));
                int v = d[y * W + x] + (int)g;
                d[y * W + x] = (ushort)Math.Min(65535, v);
            }
        }
    }
}