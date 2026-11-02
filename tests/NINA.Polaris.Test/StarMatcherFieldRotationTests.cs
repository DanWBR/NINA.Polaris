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
using NINA.Image.ImageAnalysis;

namespace NINA.Polaris.Test;

/// <summary>
/// Field rotation from a poorly polar-aligned mount: by the end of a two-hour
/// run on a 6224x4168 sensor the field had turned 1.2 degrees (T Cep, SQA55,
/// ATR2600C, 2026-11-01), which moves a corner star about 78 px beyond the
/// shift of the centre. The match has to pin the corners, not only the centre.
/// </summary>
[TestFixture]
public class StarMatcherFieldRotationTests {
    private const int W = 6224, H = 4168;

    private static List<DetectedStar> Field(int count, int seed) {
        var rng = new Random(seed);
        // Brightest first, like the detector returns them, positions spread
        // over the whole sensor.
        return Enumerable.Range(0, count)
            .Select(i => new DetectedStar {
                X = 40 + rng.NextDouble() * (W - 80), Y = 40 + rng.NextDouble() * (H - 80),
                HFR = 2, Flux = 1e6 / (i + 1)
            })
            .ToList();
    }

    /// <summary>The same stars seen by a frame turned by <paramref name="deg"/>
    /// about the sensor centre and shifted, with 0.15 px centroid noise.</summary>
    private static List<DetectedStar> Rotated(List<DetectedStar> refs, double deg, double dx, double dy, int seed) {
        var rng = new Random(seed);
        double a = deg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        double cx = W / 2.0, cy = H / 2.0;
        return refs.Select(r => {
            double x = r.X - cx, y = r.Y - cy;
            return new DetectedStar {
                X = cx + c * x - s * y + dx + (rng.NextDouble() - 0.5) * 0.3,
                Y = cy + s * x + c * y + dy + (rng.NextDouble() - 0.5) * 0.3,
                HFR = r.HFR, Flux = r.Flux
            };
        }).Where(st => st.X > 0 && st.Y > 0 && st.X < W && st.Y < H).ToList();
    }

    [TestCase(0.5)]
    [TestCase(1.2)]
    [TestCase(2.0)]
    public void RotatedField_CornersLandOnTheReference(double deg) {
        var refs = Field(200, 11);
        var cur = Rotated(refs, deg, -18, 11, 5);
        var t = StarMatcher.Match(refs, cur);
        Assert.That(t, Is.Not.Null);

        // Map each corner of the current frame back to the reference with the
        // match, and compare with the true inverse of the rotation.
        double a = -deg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        double cx = W / 2.0, cy = H / 2.0;
        foreach (var (x, y) in new[] { (0.0, 0.0), (W, 0.0), (W, (double)H), (0.0, (double)H) }) {
            double ux = x + 18 - cx, uy = y - 11 - cy;
            double tx = cx + c * ux - s * uy, ty = cy + s * ux + c * uy;
            var (mx, my) = t!.Apply(x, y);
            Assert.That(Math.Sqrt((mx - tx) * (mx - tx) + (my - ty) * (my - ty)), Is.LessThan(0.3),
                $"corner ({x},{y}) at {deg} deg");
        }
    }
}
