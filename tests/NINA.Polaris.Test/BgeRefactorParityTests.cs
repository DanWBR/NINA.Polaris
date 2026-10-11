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
using NINA.Polaris.Services.Bge;
using NINA.Polaris.Services.Rknn;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// The gate on splitting background extraction into pieces: the output must not
/// move by one count.
///
/// <see cref="RknnPipelines.RunBge"/> was one method doing downsample,
/// normalise, infer, denormalise, blur, upsample and correct. It is now a
/// composition of <see cref="BgeTensor"/>, the forward pass, and
/// <see cref="BgeApply"/>, so the inference can happen somewhere other than this
/// process and so one background can serve several frames. None of that is worth
/// anything if the arithmetic drifted while being moved.
///
/// The checksums below were taken from the method as it stood BEFORE the split
/// (commit 715a8d8a), over a deterministic synthetic gradient with a fake tile
/// runner, so they are not a snapshot of the code they are checking. Two runner
/// shapes, mono and RGB, both corrections, and the saved background: eight
/// outputs, all pinned.
///
/// If one of these fails after a deliberate change to the algorithm, retake all
/// eight from the previous commit rather than editing the expected value that
/// moved.
/// </summary>
[TestFixture]
public class BgeRefactorParityTests {

    /// <summary>A deterministic stand-in for an accelerator: no model, no
    /// native library, same numbers on every machine.</summary>
    private sealed class FakeRunner : IRknnTileRunner {
        private readonly Func<float, float> _f;
        public FakeRunner(int tile, Func<float, float> f) { TileSize = tile; _f = f; }
        public int TileSize { get; }
        public int Channels => 3;
        public float[] RunTile(float[] nhwcInput) {
            var o = new float[nhwcInput.Length];
            for (int i = 0; i < o.Length; i++) o[i] = _f(nhwcInput[i]);
            return o;
        }
        public void Dispose() { }
    }

    private static readonly Func<float, float> Identity = v => v;
    private static readonly Func<float, float> Halve = v => v * 0.5f + 0.01f;

    private const int W = 61;
    private const int H = 43;

    /// <summary>A sky gradient with a sprinkle of stars, so the correction has
    /// both a smooth field and outliers to deal with.</summary>
    private static ushort[] Gradient(int w, int h, int channels) {
        var px = new ushort[w * h * channels];
        for (int c = 0; c < channels; c++)
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++) {
                    double v = 0.18 + 0.25 * x / w + 0.10 * y / h + 0.05 * c;
                    if ((x * 7 + y * 13) % 97 == 0) v += 0.4;
                    px[c * w * h + y * w + x] = (ushort)Math.Clamp(v * 65535.0, 0, 65535);
                }
        return px;
    }

    private static string Fnv(ushort[] a) {
        ulong h = 14695981039346656037UL;
        foreach (var v in a) {
            h ^= (byte)(v & 0xFF); h *= 1099511628211UL;
            h ^= (byte)(v >> 8);   h *= 1099511628211UL;
        }
        return h.ToString("x16");
    }

    [TestCase("identity", 1, "Subtraction", "ae223b3270ff6e72", "b5d9339b000f8c48")]
    [TestCase("identity", 1, "Division", "1b958f9d79a8f608", "b5d9339b000f8c48")]
    [TestCase("identity", 3, "Subtraction", "5b4760d18ee90dc4", "5e86c2fee4d232f9")]
    [TestCase("identity", 3, "Division", "5ad448e7c1c6edbb", "5e86c2fee4d232f9")]
    [TestCase("halve", 1, "Subtraction", "950645fc3dbaf90f", "2352ecc0de0619f1")]
    [TestCase("halve", 1, "Division", "08276681fea19e68", "2352ecc0de0619f1")]
    [TestCase("halve", 3, "Subtraction", "9976a90001cae55f", "d8ebaa0aae3dba66")]
    [TestCase("halve", 3, "Division", "f06e78e60efdb535", "d8ebaa0aae3dba66")]
    public void RunBge_MatchesThePreSplitOutput(string runner, int channels, string correction,
                                                string expectedOut, string expectedBg) {
        var px = Gradient(W, H, channels);
        using var fake = new FakeRunner(256, runner == "identity" ? Identity : Halve);

        var outp = RknnPipelines.RunBge(fake, px, W, H, channels, correction, true, out var bg);

        Assert.Multiple(() => {
            Assert.That(Fnv(outp), Is.EqualTo(expectedOut),
                "the corrected pixels moved; the split was supposed to be behaviour-neutral");
            Assert.That(Fnv(bg!), Is.EqualTo(expectedBg), "the modelled background moved");
        });
    }

    /// <summary>The pieces compose to exactly what the whole did, which is what
    /// lets a caller put the forward pass somewhere else.</summary>
    [Test]
    public void TheThreePieces_ComposeToRunBge() {
        var px = Gradient(W, H, 3);
        using var fake = new FakeRunner(256, Halve);

        var whole = RknnPipelines.RunBge(fake, px, W, H, 3, "Subtraction", false, out _);

        var (tensor, stats) = BgeTensor.Build(px, W, H, 3, 256);
        var output = fake.RunTile(tensor);
        var byParts = BgeApply.Correct(px, W, H, 3, output, stats, 256, "Subtraction", false, out _);

        Assert.That(byParts, Is.EqualTo(whole));
    }

    /// <summary>StatsOnly is the per-frame half of a reused background, so it
    /// has to agree with what Build would have measured.</summary>
    [Test]
    public void StatsOnly_AgreesWithBuild() {
        var px = Gradient(W, H, 3);

        var (_, fromBuild) = BgeTensor.Build(px, W, H, 3, 256);
        var alone = BgeTensor.StatsOnly(px, W, H, 3, 256);

        Assert.That(alone, Is.EqualTo(fromBuild));
    }
}
