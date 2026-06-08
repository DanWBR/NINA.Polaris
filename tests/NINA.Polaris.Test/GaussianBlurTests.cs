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

using NUnit.Framework;
using NINA.Image.ImageAnalysis;

namespace NINA.Polaris.Test;

[TestFixture]
public class GaussianBlurTests {

    [Test]
    public void Apply_RadiusZero_ReturnsClone() {
        var data = new ushort[] { 100, 200, 300 };
        var blurred = GaussianBlur.Apply(data, 3, 1, radius: 0);
        Assert.That(blurred, Is.EqualTo(data));
        Assert.That(blurred, Is.Not.SameAs(data));   // it's a clone
    }

    [Test]
    public void Apply_FlatField_StaysFlat() {
        // Constant input → constant output (kernel sums to 1).
        var data = new ushort[64 * 64];
        Array.Fill(data, (ushort)1000);
        var blurred = GaussianBlur.Apply(data, 64, 64, radius: 3);
        foreach (var v in blurred) {
            Assert.That(v, Is.InRange((ushort)999, (ushort)1001),
                "Gaussian blur of a flat field must preserve the level");
        }
    }

    [Test]
    public void Apply_SinglePixelSpike_SpreadsToNeighbours() {
        // Lone spike in the middle of a 9×9 dark background. After
        // r=2 blur, the centre is dimmer than the input and the
        // immediate neighbours are brighter than 0.
        var data = new ushort[9 * 9];
        const ushort Spike = 10000;
        data[4 * 9 + 4] = Spike;
        var blurred = GaussianBlur.Apply(data, 9, 9, radius: 2);
        Assert.That(blurred[4 * 9 + 4], Is.LessThan(Spike),
            "Spike must dim after blur");
        Assert.That(blurred[4 * 9 + 5], Is.GreaterThan(0),
            "Right neighbour must receive energy");
        Assert.That(blurred[3 * 9 + 4], Is.GreaterThan(0),
            "Top neighbour must receive energy");
    }

    [Test]
    public void Apply_BlurredImageIsLowerVariance() {
        // Random noise field → blurred copy should have visibly lower
        // standard deviation (low-pass filter property).
        var data = new ushort[64 * 64];
        var rnd = new Random(42);
        for (int i = 0; i < data.Length; i++) data[i] = (ushort)rnd.Next(0, 10000);
        var blurred = GaussianBlur.Apply(data, 64, 64, radius: 3);
        Assert.That(StDev(blurred), Is.LessThan(StDev(data) * 0.5),
            "Blurred noise should be at least 50% lower stdev");
    }

    private static double StDev(ushort[] data) {
        double mean = 0;
        for (int i = 0; i < data.Length; i++) mean += data[i];
        mean /= data.Length;
        double sq = 0;
        for (int i = 0; i < data.Length; i++) {
            var d = data[i] - mean;
            sq += d * d;
        }
        return Math.Sqrt(sq / data.Length);
    }
}