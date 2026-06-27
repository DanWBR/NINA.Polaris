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

using System.Diagnostics;
using NINA.Image.ImageData;
using NINA.Polaris.Services.External;
using NINA.Polaris.Services.Onnx;
using NINA.Polaris.Services.Rknn;

namespace NINA.Polaris.Services.Qnn;

/// <summary>
/// Host-side GraXpert AI inference on the Qualcomm Hexagon NPU (HTP), the
/// Hexagon counterpart to <see cref="RknnInferenceService"/>. When the board is
/// a Qualcomm SBC (<see cref="QnnRuntime.IsAvailable"/>) and a pre-built HTP
/// context binary exists for the requested family/version + Hexagon arch
/// (e.g. <c>qnn/denoise-ai-models/3.0.2/denoise_v68_int16.bin</c> — the QCS6490
/// HTP is integer-only, int16 is the production precision), the model runs on
/// the NPU; otherwise the caller falls back to the GraXpert CLI.
///
/// The tiling / normalization / artifact-correction math is the canonical
/// <see cref="RknnPipelines"/> (shared GraXpert-NPU logic), reused unchanged via
/// the record/replay runners in <see cref="QnnTileBatch"/> so the only
/// QNN-specific code is the batched <c>qnn-net-run</c> executor. Only BGE and
/// Denoise are accelerated (decon stays on the CLI, as on the Rockchip path).
/// </summary>
public sealed class QnnInferenceService {
    private readonly OnnxModelRegistry _registry;
    private readonly ILogger<QnnInferenceService> _logger;
    private readonly Func<string, int, int, IQnnTileBatch> _batchFactory;

    private const int Tile = 256;
    private const int ModelChannels = 3;

    public QnnInferenceService(OnnxModelRegistry registry, ILogger<QnnInferenceService> logger,
                               Func<string, int, int, IQnnTileBatch>? batchFactory = null) {
        _registry = registry;
        _logger = logger;
        // Default: drive qnn-net-run; tests inject a fake batch.
        _batchFactory = batchFactory
            ?? ((bin, tile, ch) => new QnnNetRunBatch(bin, tile, ch, logger));
    }

    /// <summary>True when this machine can run QNN/HTP models at all.</summary>
    public bool IsAvailable => QnnRuntime.IsAvailable;

    /// <summary>One-line description of the NPU probe result, for status/logs.</summary>
    public string Diagnostics => QnnRuntime.Diagnostics;

    /// <summary>The Hexagon HTP architecture tag used to pick a context binary
    /// (the QCS6490 on the Q6A is <c>v68</c>). <c>POLARIS_QNN_ARCH</c> overrides
    /// for future SoCs (v73/v75/v81).</summary>
    public static string Arch =>
        Environment.GetEnvironmentVariable("POLARIS_QNN_ARCH") is { Length: > 0 } a ? a : "v68";

    /// <summary>
    /// Whether the NPU path can serve this operation: NPU present, op is BGE or
    /// Denoise (not decon), and a context binary for this arch exists for the
    /// requested (or newest local) version. Resolves the model on success.
    /// </summary>
    public bool CanHandle(GraXpertOperation op, string? aiVersion,
                          out string? binPath, out string version) {
        binPath = null;
        version = "";
        if (!IsAvailable) return false;
        if (!TryFamily(op, out var family)) return false;
        var resolved = ResolveModel(family, aiVersion);
        if (resolved == null) return false;
        (binPath, version) = resolved.Value;
        return true;
    }

    /// <summary>
    /// Run BGE or Denoise on the Hexagon NPU. Caller should have checked
    /// <see cref="CanHandle"/>. Throws on failure so the caller falls back to the
    /// CLI. Reuses <see cref="RknnPipelines"/> via record (capture tiles) →
    /// batch (one <c>qnn-net-run</c>) → replay (feed outputs back).
    /// </summary>
    public QnnResult Run(BaseImageData img, GraXpertOptions opts) {
        if (!TryFamily(opts.Operation, out var family))
            throw new QnnException($"NPU path does not support {opts.Operation}");
        var (binPath, version) = ResolveModel(family, opts.AiVersion)
            ?? throw new QnnException($"no QNN context binary for {family} ({Arch})");

        int w = img.Properties.Width;
        int h = img.Properties.Height;
        int channels = img.Properties.Channels >= 3 ? 3 : 1;
        var sw = Stopwatch.StartNew();

        using var batch = _batchFactory(binPath, Tile, ModelChannels);

        // Pass 1: record the ordered input tensors (output discarded).
        var rec = new RecordingTileRunner(Tile, ModelChannels);
        RunPipeline(rec, img, opts, channels, version, out _);

        // One NPU batch for the whole image.
        var outs = batch.RunBatch(rec.Inputs);
        rec.Inputs.Clear();   // recorded inputs are done; free them before replay
        int tiles = outs.Length;

        // Pass 2: replay the outputs through the same pipeline for the real result.
        var rep = new ReplayingTileRunner(Tile, ModelChannels, outs);
        var outPixels = RunPipeline(rep, img, opts, channels, version, out var bgPixels);

        sw.Stop();
        var outImage = new BaseImageData(outPixels, img.Properties, img.MetaData);
        BaseImageData? bgImage = bgPixels != null
            ? new BaseImageData(bgPixels, img.Properties, img.MetaData)
            : null;
        _logger.LogInformation("QNN {Op} {Family}/{Ver} {Arch} {W}x{H}c{Ch} {Tiles} tiles in {Ms} ms",
            opts.Operation, family, version, Arch, w, h, channels, tiles, sw.ElapsedMilliseconds);
        return new QnnResult(outImage, bgImage, sw.Elapsed.TotalMilliseconds, tiles, version);
    }

    /// <summary>
    /// Run StarNet v1 star removal on the Hexagon NPU. Resolves the <c>starnet</c>
    /// family (single-input 256³ contract) and runs it through the same record →
    /// batch → replay path as <see cref="Run"/>, reusing
    /// <see cref="RknnPipelines.RunStarRemoval"/> unchanged. Returns the starless
    /// image (<see cref="QnnResult.Image"/>) + the auto-derived stars-only image
    /// (<see cref="QnnResult.Background"/> = clamp(original − starless, 0)).
    /// Throws on failure so the caller can fall back to the browser ONNX path.
    /// </summary>
    public QnnResult RunStarRemoval(BaseImageData img, int passes = 1) {
        var (binPath, version) = ResolveModel("starnet", null)
            ?? throw new QnnException($"no QNN context binary for starnet ({Arch})");

        int w = img.Properties.Width, h = img.Properties.Height;
        int channels = img.Properties.Channels >= 3 ? 3 : 1;
        passes = Math.Clamp(passes, 1, 3);
        var original = img.Data;
        var sw = Stopwatch.StartNew();

        using var batch = _batchFactory(binPath, Tile, ModelChannels);

        // Batch ONE PASS AT A TIME. Multi-pass star removal feeds each pass's
        // starless back as the next pass's input, so a tile's input DOES depend
        // on the previous pass's output — which would break a single all-passes
        // record/replay (the record pass returns zeros). Within a single pass,
        // tile inputs are independent, so each pass is a valid record → batch →
        // replay. Stars are derived against the ORIGINAL after the last pass.
        var cur = original;
        int totalTiles = 0;
        for (int p = 0; p < passes; p++) {
            var rec = new RecordingTileRunner(Tile, ModelChannels);
            RknnPipelines.RunStarRemoval(rec, cur, w, h, channels, passes: 1);
            var outs = batch.RunBatch(rec.Inputs);
            rec.Inputs.Clear();   // free this pass's recorded inputs before replay
            totalTiles += outs.Length;
            var rep = new ReplayingTileRunner(Tile, ModelChannels, outs);
            (cur, _) = RknnPipelines.RunStarRemoval(rep, cur, w, h, channels, passes: 1);
        }

        var stars = new ushort[original.Length];
        for (int i = 0; i < original.Length; i++) {
            int d = original[i] - cur[i];
            stars[i] = d > 0 ? (ushort)d : (ushort)0;
        }

        sw.Stop();
        _logger.LogInformation("QNN StarRemoval starnet/{Ver} {Arch} {W}x{H}c{Ch} {Tiles} tiles ({Passes}p) in {Ms} ms",
            version, Arch, w, h, channels, totalTiles, passes, sw.ElapsedMilliseconds);
        return new QnnResult(
            new BaseImageData(cur, img.Properties, img.MetaData),
            new BaseImageData(stars, img.Properties, img.MetaData),
            sw.Elapsed.TotalMilliseconds, totalTiles, version);
    }

    /// <summary>Run the shared GraXpert pipeline with the given tile runner.</summary>
    private static ushort[] RunPipeline(IRknnTileRunner runner, BaseImageData img,
                                        GraXpertOptions opts, int channels, string version,
                                        out ushort[]? background) {
        if (opts.Operation == GraXpertOperation.BackgroundExtraction) {
            return RknnPipelines.RunBge(runner, img.Data, img.Properties.Width, img.Properties.Height,
                channels, opts.Correction, opts.SaveBackground, out background);
        }
        background = null;
        double clip = version.StartsWith("3.", StringComparison.Ordinal) ? 1.0 : 10.0;
        return RknnPipelines.RunDenoise(runner, img.Data, img.Properties.Width, img.Properties.Height,
            channels, opts.DenoiseStrength, clip);
    }

    // ─── helpers ────────────────────────────────────────────────────────

    private static bool TryFamily(GraXpertOperation op, out string family) {
        switch (op) {
            case GraXpertOperation.BackgroundExtraction: family = "bge"; return true;
            case GraXpertOperation.Denoising: family = "denoise"; return true;
            default: family = ""; return false;   // decon → CLI
        }
    }

    /// <summary>Resolve a context binary for a family: exact requested version
    /// first, else newest registered version that has a matching-arch <c>.bin</c>.</summary>
    private (string binPath, string version)? ResolveModel(string family, string? requestedVersion) {
        if (!string.IsNullOrEmpty(requestedVersion)) {
            var exact = _registry.Find(family, requestedVersion);
            if (exact != null) {
                var p = QnnBinaryFor(exact.Path);
                if (p != null) return (p, exact.Version);
            }
        }
        var candidates = _registry.All()
            .Where(e => string.Equals(e.Family, family, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Version, Comparer<string>.Create(CompareVersions));
        foreach (var e in candidates) {
            var p = QnnBinaryFor(e.Path);
            if (p != null) return (p, e.Version);
        }
        return null;
    }

    /// <summary>
    /// Find the HTP context binary for a given <c>model.onnx</c> in the parallel
    /// <c>qnn/</c> subtree: <c>{root}/qnn/{family}-ai-models/{version}/*{arch}*.bin</c>.
    /// Precision preference is highest-quality-first: <c>fp16</c> → <c>int16</c> →
    /// <c>int8</c>. NOTE: the QCS6490 HTP (the Q6A) is **integer-only** (INT8/INT16,
    /// no FP16 — per the Qualcomm AI Hub device matrix), so on that board an fp16
    /// binary simply won't exist and int16 is the quality choice; the fp16 tier is
    /// kept for future SoCs whose HTP does support it.
    /// </summary>
    public static string? QnnBinaryFor(string onnxPath) {
        var versionDir = Path.GetDirectoryName(onnxPath);
        if (versionDir == null) return null;
        var familyDir = Path.GetDirectoryName(versionDir);
        var root = familyDir != null ? Path.GetDirectoryName(familyDir) : null;
        if (root == null) return null;

        var qnnDir = Path.Combine(root, "qnn",
            Path.GetFileName(familyDir!), Path.GetFileName(versionDir));
        if (!Directory.Exists(qnnDir)) return null;

        var matches = Directory.EnumerateFiles(qnnDir, "*.bin")
            .Where(f => Path.GetFileName(f).Contains(Arch, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0) return null;
        foreach (var pref in new[] { "fp16", "int16", "int8" }) {
            var hit = matches.FirstOrDefault(f => f.Contains(pref, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        return matches[0];
    }

    /// <summary>Compare "2.0.0" / "3.0.2-fp16" style versions numerically.</summary>
    private static int CompareVersions(string a, string b) {
        static int[] Parse(string s) {
            var dash = s.IndexOf('-');
            if (dash > 0) s = s[..dash];
            var parts = s.Split('.');
            var nums = new int[3];
            for (int i = 0; i < 3 && i < parts.Length; i++)
                int.TryParse(parts[i], out nums[i]);
            return nums;
        }
        var pa = Parse(a); var pb = Parse(b);
        for (int i = 0; i < 3; i++) {
            int c = pa[i].CompareTo(pb[i]);
            if (c != 0) return c;
        }
        return 0;
    }
}

/// <summary>Result of a QNN/HTP inference run.</summary>
public sealed record QnnResult(
    BaseImageData Image,
    BaseImageData? Background,
    double ElapsedMs,
    int Tiles,
    string Version);
