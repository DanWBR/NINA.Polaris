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
using NINA.Polaris.Services.Qnn;
using NINA.Polaris.Services.Rknn;

namespace NINA.Polaris.Test;

/// <summary>
/// Tests for the Qualcomm Hexagon (QNN/HTP) inference lane. The NPU itself isn't
/// available on the dev box, so these cover the parts that run anywhere:
///
///  * <see cref="QnnRuntime"/> probe is false off a Qualcomm SBC (with a reason).
///  * <see cref="QnnInferenceService.QnnBinaryFor"/> resolves the arch-tagged
///    context binary in the parallel <c>qnn/</c> subtree, preferring fp16.
///  * The record/replay runners (<see cref="RecordingTileRunner"/> /
///    <see cref="ReplayingTileRunner"/>) behave correctly, and — the key one —
///    a record→replay round-trip reproduces EXACTLY what running the shared
///    <see cref="RknnPipelines"/> directly produces. That faithfulness is what
///    lets the QNN lane batch one <c>qnn-net-run</c> per image while reusing the
///    validated GraXpert tile math unchanged.
/// </summary>
[TestFixture]
public class QnnLaneTests {
    /// <summary>Echoes the input tile back unchanged (a perfect "model").</summary>
    private sealed class IdentityRunner : IRknnTileRunner {
        public int TileSize => 256;
        public int Channels => 3;
        public float[] RunTile(float[] nhwcInput) => (float[])nhwcInput.Clone();
        public void Dispose() { }
    }

    private static ushort[] Gradient(int w, int h, int lo, int hi) {
        var px = new ushort[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) {
                double t = (x + y) / (double)(w + h);
                px[y * w + x] = (ushort)(lo + t * (hi - lo));
            }
        return px;
    }

    // ----- runtime probe -----

    [Test]
    public void QnnRuntime_NotAvailable_OffQualcommSbc() {
        // The dev/CI box is not a Qualcomm arm64 SBC with the cDSP + QAIRT, so
        // the probe must decline and explain why (never throw).
        Assert.That(QnnRuntime.IsAvailable, Is.False);
        Assert.That(QnnRuntime.Diagnostics, Is.Not.Empty);
    }

    [Test]
    public void QnnRuntime_Arch_DefaultsToV68() {
        Assert.That(QnnInferenceService.Arch, Is.EqualTo("v68"));
    }

    // ----- model resolver -----

    [Test]
    public void QnnBinaryFor_ResolvesArchBinary_PrefersFp16() {
        var root = Directory.CreateTempSubdirectory("polaris-qnn-resolve-");
        try {
            var verDir = Path.Combine(root.FullName, "denoise-ai-models", "3.0.2");
            Directory.CreateDirectory(verDir);
            var onnx = Path.Combine(verDir, "model.onnx");
            File.WriteAllText(onnx, "x");

            var qnnDir = Path.Combine(root.FullName, "qnn", "denoise-ai-models", "3.0.2");
            Directory.CreateDirectory(qnnDir);
            File.WriteAllText(Path.Combine(qnnDir, "denoise_v68_int8.bin"), "x");
            File.WriteAllText(Path.Combine(qnnDir, "denoise_v68_fp16.bin"), "x");

            var resolved = QnnInferenceService.QnnBinaryFor(onnx);
            Assert.That(resolved, Is.Not.Null);
            Assert.That(Path.GetFileName(resolved!), Is.EqualTo("denoise_v68_fp16.bin"),
                "fp16 must win over int8 (quality)");
        } finally { root.Delete(recursive: true); }
    }

    [Test]
    public void QnnBinaryFor_NullWhenNoMatchingArch() {
        var root = Directory.CreateTempSubdirectory("polaris-qnn-resolve-");
        try {
            var verDir = Path.Combine(root.FullName, "denoise-ai-models", "3.0.2");
            Directory.CreateDirectory(verDir);
            var onnx = Path.Combine(verDir, "model.onnx");
            File.WriteAllText(onnx, "x");
            var qnnDir = Path.Combine(root.FullName, "qnn", "denoise-ai-models", "3.0.2");
            Directory.CreateDirectory(qnnDir);
            File.WriteAllText(Path.Combine(qnnDir, "denoise_v75_fp16.bin"), "x");  // wrong arch

            Assert.That(QnnInferenceService.QnnBinaryFor(onnx), Is.Null);
        } finally { root.Delete(recursive: true); }
    }

    [Test]
    public void QnnBinaryFor_NullWhenNoQnnSubtree() {
        var root = Directory.CreateTempSubdirectory("polaris-qnn-resolve-");
        try {
            var verDir = Path.Combine(root.FullName, "denoise-ai-models", "3.0.2");
            Directory.CreateDirectory(verDir);
            var onnx = Path.Combine(verDir, "model.onnx");
            File.WriteAllText(onnx, "x");
            Assert.That(QnnInferenceService.QnnBinaryFor(onnx), Is.Null);
        } finally { root.Delete(recursive: true); }
    }

    // ----- record / replay runners -----

    [Test]
    public void RecordingRunner_CapturesCopies_ReturnsZeroOutput() {
        var rec = new RecordingTileRunner(4, 3);
        var input = new float[] { 1, 2, 3, 4 };
        var outp = rec.RunTile(input);

        Assert.That(outp.Length, Is.EqualTo(4 * 4 * 3));
        Assert.That(outp, Is.All.EqualTo(0f));
        input[0] = 999f;                                   // mutate after the call
        Assert.That(rec.Inputs[0][0], Is.EqualTo(1f), "must capture a defensive copy");
    }

    [Test]
    public void ReplayingRunner_ReturnsInOrder_ThenOverruns() {
        var outs = new[] { new float[] { 1 }, new float[] { 2 } };
        var rep = new ReplayingTileRunner(4, 3, outs);
        Assert.That(rep.RunTile(Array.Empty<float>())[0], Is.EqualTo(1f));
        Assert.That(rep.RunTile(Array.Empty<float>())[0], Is.EqualTo(2f));
        Assert.Throws<QnnException>(() => rep.RunTile(Array.Empty<float>()));
    }

    // ----- the gold test: record/replay == direct pipeline -----

    [Test]
    public void RecordReplay_ReproducesDirectDenoise_Exactly() {
        const int w = 300, h = 220;   // not a stride multiple → exercises padding
        var plane = Gradient(w, h, 8000, 12000);

        // Direct: identity model run straight through the shared pipeline.
        using var id = new IdentityRunner();
        var direct = RknnPipelines.RunDenoiseMono(id, plane, w, h, strength: 1.0, clip: 10.0);

        // Record pass: capture the ordered input tensors (output discarded).
        var rec = new RecordingTileRunner(256, 3);
        RknnPipelines.RunDenoiseMono(rec, plane, w, h, strength: 1.0, clip: 10.0);

        // The "NPU batch" for an identity model just returns the inputs.
        var outs = rec.Inputs.ToArray();

        // Replay pass: feed those back → must equal the direct identity run.
        var rep = new ReplayingTileRunner(256, 3, outs);
        var replayed = RknnPipelines.RunDenoiseMono(rep, plane, w, h, strength: 1.0, clip: 10.0);

        Assert.That(replayed, Is.EqualTo(direct),
            "record→replay must reproduce the direct pipeline byte-for-byte");
    }

    [Test]
    public void RecordReplay_ReproducesDirectBge_Exactly() {
        const int w = 200, h = 160;
        var plane = Gradient(w, h, 5000, 30000);

        using var id = new IdentityRunner();
        var direct = RknnPipelines.RunBge(id, plane, w, h, 1, "Subtraction", false, out _);

        var rec = new RecordingTileRunner(256, 3);
        RknnPipelines.RunBge(rec, plane, w, h, 1, "Subtraction", false, out _);
        var outs = rec.Inputs.ToArray();

        var rep = new ReplayingTileRunner(256, 3, outs);
        var replayed = RknnPipelines.RunBge(rep, plane, w, h, 1, "Subtraction", false, out _);

        Assert.That(replayed, Is.EqualTo(direct));
    }

    [Test]
    public void RecordReplay_ReproducesDirectStarRemoval_Exactly() {
        const int w = 280, h = 200;   // not a stride multiple → exercises padding
        var plane = Gradient(w, h, 6000, 28000);

        // Direct: identity model run straight through the shared pipeline.
        using var id = new IdentityRunner();
        var (directStarless, directStars) = RknnPipelines.RunStarRemovalMono(id, plane, w, h);

        // Record pass: capture the ordered input tensors (output discarded).
        var rec = new RecordingTileRunner(256, 3);
        RknnPipelines.RunStarRemovalMono(rec, plane, w, h);
        var outs = rec.Inputs.ToArray();   // identity "NPU batch" returns the inputs

        // Replay pass: feed those back → must equal the direct identity run.
        var rep = new ReplayingTileRunner(256, 3, outs);
        var (replayedStarless, replayedStars) = RknnPipelines.RunStarRemovalMono(rep, plane, w, h);

        Assert.That(replayedStarless, Is.EqualTo(directStarless),
            "record→replay must reproduce the starless plane byte-for-byte");
        Assert.That(replayedStars, Is.EqualTo(directStars),
            "record→replay must reproduce the stars plane byte-for-byte");
    }

    [Test]
    public void RecordReplay_PerPassBatching_ReproducesDirectMultiPass() {
        // Multi-pass star removal feeds each pass's starless back as the next
        // pass's input, so a single all-passes record/replay is INVALID (the
        // record pass returns zeros, corrupting pass 2's input). The QNN lane
        // batches ONE PASS AT A TIME; this models that and proves it reproduces
        // the direct multi-pass result. (A single all-passes batch is the bug
        // the per-pass loop in QnnInferenceService.RunStarRemoval avoids.)
        const int w = 260, h = 260, passes = 2;
        var plane = Gradient(w, h, 4000, 32000);

        using var id = new IdentityRunner();
        var (directStarless, _) = RknnPipelines.RunStarRemovalMono(id, plane, w, h, passes);

        ushort[] cur = plane;
        for (int p = 0; p < passes; p++) {
            var rec = new RecordingTileRunner(256, 3);
            RknnPipelines.RunStarRemovalMono(rec, cur, w, h, passes: 1);
            var outs = rec.Inputs.ToArray();          // identity batch = the inputs
            var rep = new ReplayingTileRunner(256, 3, outs);
            (cur, _) = RknnPipelines.RunStarRemovalMono(rep, cur, w, h, passes: 1);
        }

        Assert.That(cur, Is.EqualTo(directStarless),
            "per-pass record→replay must reproduce the direct multi-pass result");
    }
}
