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

using System.Collections.Concurrent;
using System.Diagnostics;
using NINA.Image.ImageData;
using NINA.Polaris.Services.External;
using NINA.Polaris.Services.Onnx;
using NINA.Polaris.Services.Rknn;   // reuse RknnPipelines (backend-agnostic tiling math)

namespace NINA.Polaris.Services.Ncnn;

/// <summary>
/// Host-side GraXpert AI inference on a Vulkan GPU via ncnn — the open,
/// vendor-neutral counterpart of <see cref="RknnInferenceService"/> (which is
/// Rockchip-NPU-only). Runs on the Adreno 643 of the Radxa Dragon Q6A (Turnip),
/// Mali, etc. When a converted <c>model.ncnn.param</c>/<c>.bin</c> exists for the
/// requested family/version it runs on the GPU (~5x faster than the CPU in fp16,
/// and it frees the CPU cores for stacking). Otherwise the caller falls back to
/// the GraXpert CLI.
///
/// Scope (validated in the polaris-ai/ncnn spike): <b>BGE and Denoise v2 only.</b>
/// Denoise v3 converts but produces NaN on ncnn's Vulkan path (its LayerNorm/
/// Div/Sqrt chain), and deconvolution isn't numerically faithful — both stay on
/// the CLI/NPU. The tile math is shared with the RKNN lane via
/// <see cref="IRknnTileRunner"/>; only the backend (NcnnSession) differs.
/// Sessions are cached for the app lifetime, keyed by the <c>.param</c> path.
/// </summary>
public sealed class NcnnInferenceService : IDisposable {
    private readonly OnnxModelRegistry _registry;
    private readonly ILogger<NcnnInferenceService> _logger;
    private readonly ConcurrentDictionary<string, NcnnSession> _sessions = new();

    public NcnnInferenceService(OnnxModelRegistry registry, ILogger<NcnnInferenceService> logger) {
        _registry = registry;
        _logger = logger;
    }

    /// <summary>True when this machine can run ncnn-Vulkan models at all.</summary>
    public bool IsAvailable => NcnnRuntime.IsAvailable;

    /// <summary>One-line description of the GPU probe result, for status/logs.</summary>
    public string Diagnostics => NcnnRuntime.Diagnostics;

    /// <summary>
    /// Whether the GPU path can serve this operation: ncnn-Vulkan present, the
    /// operation is BGE or Denoise-v2 (not v3, not decon), and a converted model
    /// exists for the requested (or newest compatible local) version.
    /// </summary>
    public bool CanHandle(GraXpertOperation op, string? aiVersion,
                          out string? paramPath, out string version) {
        paramPath = null;
        version = "";
        if (!IsAvailable) return false;
        if (!TryFamily(op, out var family)) return false;
        var resolved = ResolveModel(family, aiVersion);
        if (resolved == null) return false;
        (paramPath, version) = resolved.Value;
        return true;
    }

    /// <summary>
    /// Run BGE or Denoise on the GPU. Caller should have checked
    /// <see cref="CanHandle"/> first. Throws <see cref="NcnnException"/> (or
    /// other) on failure so the caller can fall back to the CLI.
    /// </summary>
    public NcnnResult Run(BaseImageData img, GraXpertOptions opts) {
        if (!TryFamily(opts.Operation, out var family))
            throw new NcnnException($"GPU path does not support {opts.Operation}");
        var resolved = ResolveModel(family, opts.AiVersion)
            ?? throw new NcnnException($"no ncnn model for {family}");
        var (paramPath, version) = resolved;

        var session = GetSession(paramPath);
        int w = img.Properties.Width;
        int h = img.Properties.Height;
        int channels = img.Properties.Channels >= 3 ? 3 : 1;
        var sw = Stopwatch.StartNew();

        ushort[] outPixels;
        ushort[]? bgPixels = null;
        int tiles;

        if (opts.Operation == GraXpertOperation.BackgroundExtraction) {
            outPixels = RknnPipelines.RunBge(session, img.Data, w, h, channels,
                opts.Correction, opts.SaveBackground, out bgPixels);
            tiles = 1;
        } else {
            // Denoise v2 clips at 10.0 (v3 is excluded — NaN on Vulkan).
            outPixels = RknnPipelines.RunDenoise(session, img.Data, w, h, channels,
                opts.DenoiseStrength, 10.0);
            int itw = (int)Math.Ceiling((double)w / (session.TileSize / 2));
            int ith = (int)Math.Ceiling((double)h / (session.TileSize / 2));
            tiles = itw * ith;
        }

        sw.Stop();
        var outImage = new BaseImageData(outPixels, img.Properties, img.MetaData);
        BaseImageData? bgImage = bgPixels != null
            ? new BaseImageData(bgPixels, img.Properties, img.MetaData)
            : null;
        _logger.LogInformation("ncnn {Op} {Family}/{Ver} {W}x{H}c{Ch} {Tiles} tiles in {Ms} ms",
            opts.Operation, family, version, w, h, channels, tiles, sw.ElapsedMilliseconds);
        return new NcnnResult(outImage, bgImage, sw.Elapsed.TotalMilliseconds, tiles, version);
    }

    // ─── helpers ────────────────────────────────────────────────────────

    private static bool TryFamily(GraXpertOperation op, out string family) {
        switch (op) {
            case GraXpertOperation.BackgroundExtraction: family = "bge"; return true;
            case GraXpertOperation.Denoising: family = "denoise"; return true;
            default: family = ""; return false;   // decon → CLI
        }
    }

    /// <summary>
    /// Only versions known to run correctly on ncnn's Vulkan path. BGE: all.
    /// Denoise: v2 only — v3 (major &gt;= 3) outputs NaN on Vulkan (LayerNorm).
    /// </summary>
    private static bool IsVulkanSafe(string family, string version) {
        if (string.Equals(family, "denoise", StringComparison.OrdinalIgnoreCase)) {
            var major = ParseMajor(version);
            return major < 3;
        }
        return true;
    }

    private (string paramPath, string version)? ResolveModel(string family, string? requestedVersion) {
        // Exact requested version, if compatible and it has a converted model.
        if (!string.IsNullOrEmpty(requestedVersion)) {
            var exact = _registry.Find(family, requestedVersion);
            if (exact != null && IsVulkanSafe(family, exact.Version)) {
                var p = SiblingNcnn(exact.Path);
                if (p != null) return (p, exact.Version);
            }
        }
        // Newest compatible registered version of this family that has a model.
        var candidates = _registry.All()
            .Where(e => string.Equals(e.Family, family, StringComparison.OrdinalIgnoreCase))
            .Where(e => IsVulkanSafe(family, e.Version))
            .OrderByDescending(e => e.Version, Comparer<string>.Create(CompareVersions));
        foreach (var e in candidates) {
            var p = SiblingNcnn(e.Path);
            if (p != null) return (p, e.Version);
        }
        return null;
    }

    /// <summary>
    /// Resolve the <c>model.ncnn.param</c> for a given <c>model.onnx</c>. Two
    /// layouts accepted (the bundled one is the parallel <c>ncnn/</c> subtree):
    ///   1. sibling:  {root}/{family}-ai-models/{version}/model.ncnn.param
    ///   2. parallel: {root}/ncnn/{family}-ai-models/{version}/model.ncnn.param
    /// </summary>
    private static string? SiblingNcnn(string onnxPath) {
        var versionDir = Path.GetDirectoryName(onnxPath);
        if (versionDir == null) return null;

        var sibling = Path.Combine(versionDir, "model.ncnn.param");
        if (File.Exists(sibling)) return sibling;

        var familyDir = Path.GetDirectoryName(versionDir);
        var root = familyDir != null ? Path.GetDirectoryName(familyDir) : null;
        if (root != null) {
            var parallel = Path.Combine(root, "ncnn",
                Path.GetFileName(familyDir!), Path.GetFileName(versionDir), "model.ncnn.param");
            if (File.Exists(parallel)) return parallel;
        }
        return null;
    }

    private NcnnSession GetSession(string paramPath) {
        return _sessions.GetOrAdd(paramPath, p => {
            _logger.LogInformation("Loading ncnn model {Path}", p);
            return NcnnSession.LoadFromParam(p, 256, 3, _logger);
        });
    }

    private static int ParseMajor(string v) {
        var dash = v.IndexOf('-');
        if (dash > 0) v = v[..dash];
        var first = v.Split('.')[0];
        return int.TryParse(first, out var m) ? m : 0;
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

    public void Dispose() {
        foreach (var s in _sessions.Values) {
            try { s.Dispose(); } catch { }
        }
        _sessions.Clear();
    }
}

/// <summary>Result of a GPU inference run.</summary>
public sealed record NcnnResult(
    BaseImageData Image,
    BaseImageData? Background,
    double ElapsedMs,
    int Tiles,
    string Version);
