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

namespace NINA.Polaris.Services.Rknn;

/// <summary>
/// Host-side GraXpert AI inference on a Rockchip NPU. When the machine has an
/// RK3588-class NPU (<see cref="RknnRuntime.IsAvailable"/>) and a converted
/// <c>model.rknn</c> sits next to the bundled <c>model.onnx</c> for the
/// requested family/version, this runs the model on the NPU (~5x faster than
/// the CPU on an Orange Pi 5 Pro, and it frees the 8 CPU cores for stacking).
/// Otherwise the caller falls back to the GraXpert CLI.
///
/// Only BGE and Denoise are accelerated here; deconvolution stays on the CLI
/// (different tile size / layout / multi-input contract). Sessions are cached
/// for the app lifetime, keyed by the <c>.rknn</c> path, so the ~100 MB model
/// is loaded once.
/// </summary>
public sealed class RknnInferenceService : IDisposable {
    private readonly OnnxModelRegistry _registry;
    private readonly ILogger<RknnInferenceService> _logger;
    private readonly ConcurrentDictionary<string, RknnSession> _sessions = new();

    public RknnInferenceService(OnnxModelRegistry registry, ILogger<RknnInferenceService> logger) {
        _registry = registry;
        _logger = logger;
    }

    /// <summary>True when this machine can run RKNN models at all.</summary>
    public bool IsAvailable => RknnRuntime.IsAvailable;

    /// <summary>One-line description of the NPU probe result, for status/logs.</summary>
    public string Diagnostics => RknnRuntime.Diagnostics;

    /// <summary>
    /// Whether the NPU path can serve this operation: NPU present, operation is
    /// BGE or Denoise (not decon), and a <c>model.rknn</c> exists for the
    /// requested (or newest local) version. On success <paramref name="rknnPath"/>
    /// and <paramref name="version"/> are the resolved model.
    /// </summary>
    public bool CanHandle(GraXpertOperation op, string? aiVersion,
                          out string? rknnPath, out string version) {
        rknnPath = null;
        version = "";
        if (!IsAvailable) return false;
        if (!TryFamily(op, out var family)) return false;
        var resolved = ResolveModel(family, aiVersion);
        if (resolved == null) return false;
        (rknnPath, version) = resolved.Value;
        return true;
    }

    /// <summary>
    /// Run BGE or Denoise on the NPU. Caller should have checked
    /// <see cref="CanHandle"/> first. Throws <see cref="RknnException"/> (or
    /// other) on NPU failure so the caller can fall back to the CLI.
    /// </summary>
    public RknnResult Run(BaseImageData img, GraXpertOptions opts) {
        if (!TryFamily(opts.Operation, out var family))
            throw new RknnException($"NPU path does not support {opts.Operation}");
        var resolved = ResolveModel(family, opts.AiVersion)
            ?? throw new RknnException($"no model.rknn for {family}");
        var (rknnPath, version) = resolved;

        var session = GetSession(rknnPath);
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
            // Denoise. v3 models clip at 1.0, v2 at 10.0.
            double clip = version.StartsWith("3.", StringComparison.Ordinal) ? 1.0 : 10.0;
            int planeLen = w * h;
            outPixels = new ushort[img.Data.Length];
            if (channels == 3) {
                for (int c = 0; c < 3; c++) {
                    var plane = img.Data.AsSpan(c * planeLen, planeLen).ToArray();
                    var dp = RknnPipelines.RunDenoiseMono(session, plane, w, h,
                        opts.DenoiseStrength, clip);
                    Array.Copy(dp, 0, outPixels, c * planeLen, planeLen);
                }
            } else {
                outPixels = RknnPipelines.RunDenoiseMono(session, img.Data, w, h,
                    opts.DenoiseStrength, clip);
            }
            int itw = (int)Math.Ceiling((double)w / (session.TileSize / 2));
            int ith = (int)Math.Ceiling((double)h / (session.TileSize / 2));
            tiles = itw * ith * channels;
        }

        sw.Stop();
        var outImage = new BaseImageData(outPixels, img.Properties, img.MetaData);
        BaseImageData? bgImage = bgPixels != null
            ? new BaseImageData(bgPixels, img.Properties, img.MetaData)
            : null;
        _logger.LogInformation("RKNN {Op} {Family}/{Ver} {W}x{H}c{Ch} {Tiles} tiles in {Ms} ms",
            opts.Operation, family, version, w, h, channels, tiles, sw.ElapsedMilliseconds);
        return new RknnResult(outImage, bgImage, sw.Elapsed.TotalMilliseconds, tiles, version);
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
    /// Resolve a usable <c>model.rknn</c> for a family. Prefers the exact
    /// requested version when it has a sibling .rknn; otherwise scans every
    /// registered version of the family (newest first) for one that does.
    /// </summary>
    private (string rknnPath, string version)? ResolveModel(string family, string? requestedVersion) {
        // Exact requested version, if its dir actually has a .rknn.
        if (!string.IsNullOrEmpty(requestedVersion)) {
            var exact = _registry.Find(family, requestedVersion);
            if (exact != null) {
                var p = SiblingRknn(exact.Path);
                if (p != null) return (p, exact.Version);
            }
        }
        // Newest registered version of this family that has a .rknn.
        var candidates = _registry.All()
            .Where(e => string.Equals(e.Family, family, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Version, Comparer<string>.Create(CompareVersions));
        foreach (var e in candidates) {
            var p = SiblingRknn(e.Path);
            if (p != null) return (p, e.Version);
        }
        return null;
    }

    private static string? SiblingRknn(string onnxPath) {
        var dir = Path.GetDirectoryName(onnxPath);
        if (dir == null) return null;
        var rknn = Path.Combine(dir, "model.rknn");
        return File.Exists(rknn) ? rknn : null;
    }

    private RknnSession GetSession(string rknnPath) {
        return _sessions.GetOrAdd(rknnPath, p => {
            _logger.LogInformation("Loading RKNN model {Path}", p);
            return RknnSession.LoadFromFile(p, 256, 3, _logger);
        });
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

/// <summary>Result of an NPU inference run.</summary>
public sealed record RknnResult(
    BaseImageData Image,
    BaseImageData? Background,
    double ElapsedMs,
    int Tiles,
    string Version);
