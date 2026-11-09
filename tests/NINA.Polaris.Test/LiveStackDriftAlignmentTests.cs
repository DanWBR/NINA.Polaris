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
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// A session whose pointing drifts well past the matcher's 50 px window has to
/// keep aligning every frame where it really is. On Abell 370 (2026-11-07) the
/// drift reached ~70 px, the window found no true votes, and chance
/// coincidences produced a false alignment for eight frames, each a ghost copy
/// of the field in the stack. Frames are now predicted from the last one that
/// aligned.
/// </summary>
[TestFixture]
public class LiveStackDriftAlignmentTests {
    private const int W = 700, H = 500;

    private static (double x, double y, double amp)[] Field(int seed, int n) {
        var rnd = new Random(seed);
        return Enumerable.Range(0, n)
            .Select(_ => (rnd.NextDouble() * (W + 300) - 150, rnd.NextDouble() * (H + 300) - 150,
                          800 + rnd.NextDouble() * 6000))
            .ToArray();
    }

    // The sky shifted by (dx, dy): a star at sky (x, y) lands at (x + dx, y + dy).
    private static BaseImageData Frame((double x, double y, double amp)[] stars, double dx, double dy, int seed) {
        var rnd = new Random(seed);
        var data = new ushort[W * H];
        for (int i = 0; i < data.Length; i++) data[i] = (ushort)(1000 + rnd.Next(-12, 13));
        foreach (var (sx, sy, amp) in stars) {
            double cx = sx + dx, cy = sy + dy;
            int x0 = (int)cx - 6, y0 = (int)cy - 6;
            for (int y = Math.Max(0, y0); y < Math.Min(H, y0 + 13); y++)
                for (int x = Math.Max(0, x0); x < Math.Min(W, x0 + 13); x++) {
                    double r2 = (x - cx) * (x - cx) + (y - cy) * (y - cy);
                    data[y * W + x] = (ushort)Math.Min(65535, data[y * W + x] + amp * Math.Exp(-r2 / (2 * 1.6 * 1.6)));
                }
        }
        return new BaseImageData(data, new ImageProperties { Width = W, Height = H, BitDepth = 16 });
    }

    [Test]
    public async Task DriftPastTheTightWindow_AlignsEveryFrameWhereItIs() {
        var relay = new ImageRelayService(NullLogger<ImageRelayService>.Instance);
        var svc = new LiveStackingService(relay, NullLogger<LiveStackingService>.Instance) {
            PolynomialAlignment = false, SigmaRejection = false
        };
        var aligned = new List<LiveFrameAlignment>();
        svc.FrameAligned += a => aligned.Add(a);
        svc.Start();

        var stars = Field(42, 140);
        // 4.5 px per frame in x, 1.5 in y, plus a +/-6 px dither every other
        // frame: 20 frames end ~90 px out, far past the 50 px window.
        var truth = new List<(double dx, double dy)>();
        for (int k = 0; k < 21; k++) {
            double dither = k % 2 == 0 ? 0 : (k % 4 == 1 ? 6 : -6);
            truth.Add((4.5 * k + dither, 1.5 * k - dither / 2));
            await svc.AddFrameAsync(Frame(stars, truth[k].dx, truth[k].dy, 100 + k));
        }

        Assert.That(svc.FrameCount, Is.EqualTo(21), "every frame integrated");
        // The transform maps the frame back onto the reference: its translation
        // is minus the sky shift relative to frame 0.
        for (int k = 1; k < 21; k++) {
            var a = aligned[k];
            Assert.That(a.Tx, Is.EqualTo(-(truth[k].dx - truth[0].dx)).Within(1.0), $"frame {k} x");
            Assert.That(a.Ty, Is.EqualTo(-(truth[k].dy - truth[0].dy)).Within(1.0), $"frame {k} y");
        }
    }

    /// <summary>
    /// Abell 370, 2026-11-08: a cloud-dimmed frame went in 60 px off, the next
    /// frames were predicted from that error, and a chance trio of stars put
    /// seventeen of them 130 px from the truth. A frame whose alignment lands
    /// only a few of its stars on reference stars is now dropped, and the
    /// frames after it align where they are.
    /// </summary>
    [Test]
    public async Task CloudFrame_WithFewTrueStars_IsDropped_AndTheNextFramesAlignTrue() {
        var relay = new ImageRelayService(NullLogger<ImageRelayService>.Instance);
        var svc = new LiveStackingService(relay, NullLogger<LiveStackingService>.Instance) {
            PolynomialAlignment = false, SigmaRejection = false
        };
        var aligned = new List<LiveFrameAlignment>();
        svc.FrameAligned += a => aligned.Add(a);
        svc.Start();

        var stars = Field(42, 140);
        var truth = new List<(double dx, double dy)>();
        for (int k = 0; k < 12; k++) truth.Add((4.5 * k, 1.5 * k));
        for (int k = 0; k < 12; k++) {
            if (k == 6) {
                // Cloud: four field stars get through, plus a dozen bright
                // blobs of cloud lit by the town that match nothing.
                var rnd = new Random(7);
                var cloud = stars.OrderByDescending(s => s.amp).Take(4)
                    .Concat(Enumerable.Range(0, 14).Select(_ =>
                        (rnd.NextDouble() * W, rnd.NextDouble() * H, 4000 + rnd.NextDouble() * 3000)))
                    .ToArray();
                await svc.AddFrameAsync(Frame(cloud, truth[k].dx, truth[k].dy, 100 + k));
                Assert.That(svc.FrameCount, Is.EqualTo(6), "the cloud frame is not stacked");
                continue;
            }
            await svc.AddFrameAsync(Frame(stars, truth[k].dx, truth[k].dy, 100 + k));
        }

        Assert.That(svc.FrameCount, Is.EqualTo(11));
        var kept = Enumerable.Range(0, 12).Where(k => k != 6).ToList();
        for (int j = 1; j < kept.Count; j++) {
            int k = kept[j];
            Assert.That(aligned[j].Tx, Is.EqualTo(-truth[k].dx).Within(1.0), $"frame {k} x");
            Assert.That(aligned[j].Ty, Is.EqualTo(-truth[k].dy).Within(1.0), $"frame {k} y");
        }
    }

    [Test]
    public void AlignmentSupport_TellsTheFieldFromACoincidence() {
        var rnd = new Random(3);
        var refStars = Enumerable.Range(0, 50)
            .Select(_ => new DetectedStar { X = rnd.NextDouble() * W, Y = rnd.NextDouble() * H }).ToList();
        // The frame is the reference shifted by (+20, -10); the true transform
        // takes it back.
        var cur = refStars.Select(s => new DetectedStar { X = s.X + 20, Y = s.Y - 10 }).ToList();
        var truthT = new AffineTransform { M00 = 1, M11 = 1, Tx = -20, Ty = 10 };
        var (inTrue, ovTrue) = LiveStackingService.AlignmentSupport(refStars, cur, truthT, W, H);
        Assert.That(LiveStackingService.IsSupported(inTrue, ovTrue), Is.True, $"{inTrue} of {ovTrue}");

        // 130 px off, as on the night: a few stars at most land by chance.
        var wrong = new AffineTransform { M00 = 1, M11 = 1, Tx = -20 + 40, Ty = 10 - 124 };
        var (inWrong, ovWrong) = LiveStackingService.AlignmentSupport(refStars, cur, wrong, W, H);
        Assert.That(LiveStackingService.IsSupported(inWrong, ovWrong), Is.False, $"{inWrong} of {ovWrong}");
    }
}
