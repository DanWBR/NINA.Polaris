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
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NINA.Image.ImageData;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// A meridian flip in a real session, not the ideal one: a sensor of normal
/// size, field rotation that has grown since the reference frame (polar
/// alignment is never perfect), and the shift the recentering leaves. The
/// pre-flip frames absorb the rotation through the predicted alignment; the
/// post-flip frames arrive rotated by 180 degrees plus that same rotation.
/// </summary>
[TestFixture]
public class LiveStackFlipFieldRotationTests {

    private const int W = 1600, H = 1100, Bg = 100;
    private static readonly (double x, double y)[] Field = MakeField(70, seed: 11);

    private static (double x, double y)[] MakeField(int n, int seed) {
        var rng = new Random(seed);
        var f = new (double, double)[n];
        for (int i = 0; i < n; i++) f[i] = (40 + rng.NextDouble() * (W - 80), 40 + rng.NextDouble() * (H - 80));
        return f;
    }

    /// <summary>The field as the camera sees it: rotated by <paramref name="deg"/>
    /// about the frame centre, then shifted.</summary>
    private static (double x, double y)[] View(double deg, double dx, double dy) {
        double a = deg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        double cx = (W - 1) / 2.0, cy = (H - 1) / 2.0;
        var v = new (double, double)[Field.Length];
        for (int i = 0; i < Field.Length; i++) {
            double x = Field[i].x - cx, y = Field[i].y - cy;
            v[i] = (cx + c * x - s * y + dx, cy + s * x + c * y + dy);
        }
        return v;
    }

    [TestCase(0.0, 0, 0)]
    [TestCase(0.6, 40, -25)]
    [TestCase(1.0, 140, -90)]
    [TestCase(1.2, 320, 180)]
    [TestCase(-1.0, -200, 60)]
    [TestCase(2.5, 100, 100)]
    public async Task PostFlipFramesWithFieldRotationAreIntegrated(double rotationDeg, double shiftX, double shiftY)
        => await RunSession(rotationDeg, jumpAtFlipDeg: 0, shiftX, shiftY);

    /// <summary>A flip that isn't exactly 180 degrees: a camera that isn't
    /// square to the axis, or a rotator nudged during the flip. The rotation
    /// jumps at the flip, so the prediction from before it is off and the
    /// angle search has to find it.</summary>
    [TestCase(0.0, 1.0)]
    [TestCase(0.5, -1.4)]
    [TestCase(0.8, 2.6)]
    public async Task APostFlipRotationJumpIsFoundByTheAngleSearch(double rotationDeg, double jumpDeg)
        => await RunSession(rotationDeg, jumpDeg, 150, -80);

    private static async Task RunSession(double rotationDeg, double jumpAtFlipDeg, double shiftX, double shiftY) {
        var relay = new ImageRelayService(NullLogger<ImageRelayService>.Instance);
        var svc = new LiveStackingService(relay, NullLogger<LiveStackingService>.Instance);
        svc.Start();

        // Reference, then the rotation growing frame by frame up to the flip.
        await svc.AddFrameAsync(MakeFrame(View(0, 0, 0)));
        int steps = 6;
        for (int i = 1; i <= steps; i++)
            await svc.AddFrameAsync(MakeFrame(View(rotationDeg * i / steps, 2 * i, -i)));
        Assert.That(svc.FrameCount, Is.EqualTo(1 + steps), "pre-flip frames: " + svc.LastRejectReason);

        // After the flip: 180 degrees plus the rotation, and the recentering shift.
        for (int i = 0; i < 3; i++)
            await svc.AddFrameAsync(MakeFrame(View(180 + rotationDeg + jumpAtFlipDeg + 0.02 * i, shiftX + i, shiftY - i)));

        Assert.That(svc.FrameCount, Is.EqualTo(1 + steps + 3),
            $"post-flip frames rejected: {svc.RejectedFrames}, last reason: {svc.LastRejectReason}");
        Assert.That(svc.MeridianFlipsHandled, Is.EqualTo(1));
    }

    private static BaseImageData MakeFrame((double x, double y)[] stars) {
        var data = new ushort[W * H];
        Array.Fill(data, (ushort)Bg);
        int k = 0;
        foreach (var (cx, cy) in stars) {
            // Brightness varies from star to star, as in the sky, so the
            // brightest-N selection isn't a coin toss.
            double amp = 1500 + (k++ * 7919 % 97) * 60;
            if (cx >= 0 && cy >= 0 && cx < W && cy < H) PlantStar(data, cx, cy, 2.0, amp);
        }
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
                d[y * W + x] = (ushort)Math.Min(65535, d[y * W + x] + (int)g);
            }
        }
    }
}
