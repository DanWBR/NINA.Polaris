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
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using SkiaSharp;

namespace NINA.Polaris.Services.Editor;

/// <summary>
/// "Image Blend" tool — the in-app equivalent of PixInsight's ImageBlend
/// script. Loads a base image and a blend image once, then re-renders a
/// downscaled JPEG preview as the user drags each image's independent
/// blackpoint/midtones/highlights sliders + blend mode + opacity. The final
/// "create new image" writes a full-resolution 16-bit FITS.
///
/// The canonical use is the starless workflow: base = stretched starless,
/// blend = stretched stars-only (auto-derived as original − starless), Screen
/// blend → stars added back on top of the processed nebulosity.
///
/// Mirrors <see cref="ImageEditService"/>'s session model (load-once, idle
/// eviction). The actual stretch/blend math lives in the portable, unit-tested
/// <see cref="ImageBlend"/>.
/// </summary>
public class ImageBlendService : IDisposable {
    private readonly ILogger<ImageBlendService> _logger;
    private readonly ProfileService _profile;
    private readonly ConcurrentDictionary<string, BlendSession> _sessions = new();
    private readonly Timer _reaper;
    private static readonly TimeSpan SessionIdleTimeout = TimeSpan.FromMinutes(30);

    public ImageBlendService(ProfileService profile, ILogger<ImageBlendService> logger) {
        _profile = profile;
        _logger = logger;
        _reaper = new Timer(_ => Reap(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    public sealed record BlendSessionInfo(
        string SessionId, string BasePath, string BlendPath,
        int Width, int Height, int Channels);

    /// <summary>256-bin per-channel histogram (R==G==B for mono) plus basic
    /// 16-bit stats, for the Image Blend adjustment histograms.</summary>
    public sealed record HistogramData(
        int[] R, int[] G, int[] B, int Min, int Max, double Avg, double Std);

    /// <summary>Linear histograms of the loaded base + blend images, so the
    /// client can draw an editor-style adjustment histogram per layer.</summary>
    public (HistogramData? Base, HistogramData? Blend) GetHistograms(string sessionId) {
        if (!_sessions.TryGetValue(sessionId, out var s)) return (null, null);
        s.LastTouch = DateTime.UtcNow;
        // Histograms of the NEUTRAL base (Stage A) so the per-layer handles sit
        // on the same display-space data the sliders adjust (Stage B).
        int plane = s.Width * s.Height;
        var baseN = ImageBlend.NeutralizePerChannel(s.Base.Data, s.Channels, plane, s.Base.Properties.BitDepth);
        var blendN = ImageBlend.NeutralizePerChannel(s.Blend.Data, s.Channels, plane, s.Blend.Properties.BitDepth);
        return (ComputeHistogram(baseN, s.Width, s.Height, s.Channels),
                ComputeHistogram(blendN, s.Width, s.Height, s.Channels));
    }

    /// <summary>256-bin per-channel histogram + stats from a decimated sample
    /// of a plane-sequential ushort buffer (cheap; ~250k samples max).</summary>
    private static HistogramData ComputeHistogram(ushort[] data, int w, int h, int channels) {
        var R = new int[256]; var G = new int[256]; var B = new int[256];
        long plane = (long)w * h;
        int step = Math.Max(1, (int)(plane / 250_000));
        int min = 65535, max = 0; double sum = 0, sumSq = 0; long n = 0;
        int[][] bins = channels == 3 ? new[] { R, G, B } : new[] { R };
        for (int c = 0; c < bins.Length; c++) {
            long off = c * plane;
            var bin = bins[c];
            for (long i = 0; i < plane; i += step) {
                int v = data[off + i];
                bin[v >> 8]++;
                if (v < min) min = v; if (v > max) max = v;
                sum += v; sumSq += (double)v * v; n++;
            }
        }
        if (channels != 3) { Array.Copy(R, G, 256); Array.Copy(R, B, 256); }
        double avg = n > 0 ? sum / n : 0;
        double var = n > 0 ? Math.Max(0, sumSq / n - avg * avg) : 0;
        return new HistogramData(R, G, B, min, max, avg, Math.Sqrt(var));
    }

    /// <summary>Per-image stretch + the blend settings for one render.</summary>
    public sealed record BlendParams(
        double BaseBlack = 0.0, double BaseMid = 0.5, double BaseWhite = 1.0,
        double BlendBlack = 0.0, double BlendMid = 0.5, double BlendWhite = 1.0,
        string Mode = "screen", double Opacity = 1.0);

    /// <summary>
    /// Open the base + blend FITS, validate matching geometry, and cache them
    /// (plus a downscaled copy for fast preview). Returns null on failure
    /// (missing file, decode error, or dimension/channel mismatch).
    /// </summary>
    public Task<BlendSessionInfo?> LoadAsync(string basePath, string blendPath,
                                             CancellationToken ct = default)
        => Task.Run<BlendSessionInfo?>(() => LoadSync(basePath, blendPath), ct);

    /// <summary>Render a downscaled JPEG preview for the current params.
    /// <paramref name="maxDim"/> caps the long side (0 = full res).</summary>
    public Task<byte[]?> RenderPreviewAsync(string sessionId, BlendParams p,
                                            int maxDim = 1600, int quality = 85,
                                            CancellationToken ct = default)
        => Task.Run<byte[]?>(() => PreviewSync(sessionId, p, maxDim, quality), ct);

    /// <summary>Render the full-resolution 16-bit blended FITS to disk.
    /// Returns the absolute output path (or null on failure).</summary>
    public Task<string?> RenderAsync(string sessionId, BlendParams p, string? outputPath,
                                     CancellationToken ct = default)
        => Task.Run<string?>(() => RenderSync(sessionId, p, outputPath), ct);

    public void Release(string sessionId) => _sessions.TryRemove(sessionId, out _);

    // ---- core ------------------------------------------------------------

    private BlendSessionInfo? LoadSync(string basePath, string blendPath) {
        try {
            if (!File.Exists(basePath) || !File.Exists(blendPath)) {
                _logger.LogWarning("ImageBlend load: missing file(s) {Base} / {Blend}", basePath, blendPath);
                return null;
            }
            var baseImg = ReadFits(basePath);
            var blendImg = ReadFits(blendPath);
            if (baseImg == null || blendImg == null) return null;

            int w = baseImg.Properties.Width, h = baseImg.Properties.Height;
            int ch = baseImg.Properties.Channels;
            if (blendImg.Properties.Width != w || blendImg.Properties.Height != h
                    || blendImg.Properties.Channels != ch) {
                _logger.LogWarning(
                    "ImageBlend load: geometry mismatch base {Bw}x{Bh}x{Bc} vs blend {Lw}x{Lh}x{Lc}",
                    w, h, ch, blendImg.Properties.Width, blendImg.Properties.Height,
                    blendImg.Properties.Channels);
                return null;
            }

            var s = new BlendSession {
                Id = Guid.NewGuid().ToString("N"),
                BasePath = basePath, BlendPath = blendPath,
                Base = baseImg, Blend = blendImg,
                Width = w, Height = h, Channels = ch,
                LastTouch = DateTime.UtcNow
            };
            _sessions[s.Id] = s;
            return new BlendSessionInfo(s.Id, basePath, blendPath, w, h, ch);
        } catch (Exception ex) {
            _logger.LogError(ex, "ImageBlend load failed");
            return null;
        }
    }

    private byte[]? PreviewSync(string sessionId, BlendParams p, int maxDim, int quality) {
        if (!_sessions.TryGetValue(sessionId, out var s)) return null;
        s.LastTouch = DateTime.UtcNow;

        // Decimate both images to preview size (integer step) so slider drags
        // stay snappy on big masters, then blend the small buffers.
        int step = (maxDim <= 0) ? 1
            : Math.Max(1, (int)Math.Ceiling(Math.Max(s.Width, s.Height) / (double)maxDim));
        var (basePrev, pw, ph) = Decimate(s.Base.Data, s.Width, s.Height, s.Channels, step);
        var (blendPrev, _, _) = Decimate(s.Blend.Data, s.Width, s.Height, s.Channels, step);

        // Stage A: per-channel neutral base (white-balances the OSC cast). The
        // user's sliders (Stage B) then adjust the neutral 16-bit base, so
        // moving them doesn't reintroduce a colour cast.
        int plane = pw * ph;
        var baseN = ImageBlend.NeutralizePerChannel(basePrev, s.Channels, plane, s.Base.Properties.BitDepth);
        var blendN = ImageBlend.NeutralizePerChannel(blendPrev, s.Channels, plane, s.Blend.Properties.BitDepth);

        var blended = ImageBlend.Combine(
            baseN, blendN,
            new ImageBlend.StretchSpec(p.BaseBlack, p.BaseMid, p.BaseWhite),
            new ImageBlend.StretchSpec(p.BlendBlack, p.BlendMid, p.BlendWhite),
            ImageBlend.ParseMode(p.Mode), p.Opacity, 16, 16);

        return EncodeJpeg(blended, pw, ph, s.Channels, quality);
    }

    private string? RenderSync(string sessionId, BlendParams p, string? outputPath) {
        if (!_sessions.TryGetValue(sessionId, out var s)) return null;
        s.LastTouch = DateTime.UtcNow;

        int plane = s.Width * s.Height;
        var baseN = ImageBlend.NeutralizePerChannel(s.Base.Data, s.Channels, plane, s.Base.Properties.BitDepth);
        var blendN = ImageBlend.NeutralizePerChannel(s.Blend.Data, s.Channels, plane, s.Blend.Properties.BitDepth);
        var blended = ImageBlend.Combine(
            baseN, blendN,
            new ImageBlend.StretchSpec(p.BaseBlack, p.BaseMid, p.BaseWhite),
            new ImageBlend.StretchSpec(p.BlendBlack, p.BlendMid, p.BlendWhite),
            ImageBlend.ParseMode(p.Mode), p.Opacity, 16, 16);

        // Output is always full-range 16-bit; carry the base image's metadata
        // (WCS, target, etc.) onto the blended result.
        var props = s.Base.Properties with { BitDepth = 16 };
        var outImg = new BaseImageData(blended, props, s.Base.MetaData);

        var outPath = string.IsNullOrWhiteSpace(outputPath)
            ? DefaultOutputPath(s.BasePath) : outputPath!;
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        var keywords = new List<KeyValuePair<string, string>> {
            new("IMGBLEND", "T"),
            new("BLENDMOD", ImageBlend.ParseMode(p.Mode).ToString()),
            new("BLENDOPA", p.Opacity.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)),
        };
        FITSWriter.Write(outImg, outPath, customKeywords: keywords);
        _logger.LogInformation("ImageBlend wrote {Path}", outPath);
        return outPath;
    }

    // ---- helpers ---------------------------------------------------------

    private static BaseImageData? ReadFits(string path) {
        using var fs = File.OpenRead(path);
        return FITSReader.Read(fs);
    }

    /// <summary>"…/foo_starless.fits" + "…/foo_stars.fits" → "…/foo_blend.fits".
    /// Falls back to appending _blend next to the base image.</summary>
    private static string DefaultOutputPath(string basePath) {
        var dir = Path.GetDirectoryName(basePath) ?? ".";
        var name = Path.GetFileNameWithoutExtension(basePath);
        foreach (var suffix in new[] { "_starless", "_stars" })
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                name = name[..^suffix.Length];
        var outName = $"{name}_blend.fits";
        var outPath = Path.Combine(dir, outName);
        int copy = 1;
        while (File.Exists(outPath))
            outPath = Path.Combine(dir, $"{name}_blend_{copy++}.fits");
        return outPath;
    }

    /// <summary>Nearest-neighbour integer decimation of a plane-sequential
    /// ushort buffer. Cheap and good enough for a slider-drag preview.</summary>
    private static (ushort[] data, int w, int h) Decimate(
            ushort[] src, int w, int h, int channels, int step) {
        if (step <= 1) return (src, w, h);
        int nw = (w + step - 1) / step, nh = (h + step - 1) / step;
        int srcPlane = w * h, dstPlane = nw * nh;
        var dst = new ushort[dstPlane * channels];
        for (int c = 0; c < channels; c++) {
            int so = c * srcPlane, doff = c * dstPlane;
            for (int y = 0; y < nh; y++) {
                int sy = Math.Min(h - 1, y * step);
                for (int x = 0; x < nw; x++) {
                    int sx = Math.Min(w - 1, x * step);
                    dst[doff + y * nw + x] = src[so + sy * w + sx];
                }
            }
        }
        return (dst, nw, nh);
    }

    /// <summary>Encode a plane-sequential 16-bit buffer to JPEG (down to 8-bit
    /// for display). Mono → grayscale-as-RGB; 3 planes → RGB.</summary>
    private static byte[] EncodeJpeg(ushort[] data, int w, int h, int channels, int quality) {
        int plane = w * h;
        var rgba = new byte[plane * 4];
        for (int i = 0; i < plane; i++) {
            byte r, g, b;
            if (channels >= 3) {
                r = (byte)(data[i] >> 8);
                g = (byte)(data[plane + i] >> 8);
                b = (byte)(data[2 * plane + i] >> 8);
            } else {
                r = g = b = (byte)(data[i] >> 8);
            }
            int o = i * 4;
            rgba[o] = r; rgba[o + 1] = g; rgba[o + 2] = b; rgba[o + 3] = 255;
        }
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var img = SKImage.FromPixelCopy(info, rgba);
        using var enc = img.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(quality, 1, 100));
        return enc.ToArray();
    }

    private void Reap() {
        var cutoff = DateTime.UtcNow - SessionIdleTimeout;
        foreach (var kv in _sessions)
            if (kv.Value.LastTouch < cutoff) _sessions.TryRemove(kv.Key, out _);
    }

    public void Dispose() {
        _reaper.Dispose();
        _sessions.Clear();
        GC.SuppressFinalize(this);
    }

    private sealed class BlendSession {
        public string Id = "";
        public string BasePath = "";
        public string BlendPath = "";
        public BaseImageData Base = null!;
        public BaseImageData Blend = null!;
        public int Width, Height, Channels;
        public DateTime LastTouch;
    }
}
