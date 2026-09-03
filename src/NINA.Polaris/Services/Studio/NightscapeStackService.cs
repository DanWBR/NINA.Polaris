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
using NINA.Core.Enum;
using NINA.Image.Editor;
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;

namespace NINA.Polaris.Services.Studio;

/// <summary>
/// Nightscape stack: a fixed-tripod Milky Way landscape sequence, stacked two
/// ways over the same frames and joined by a horizon mask. The SKY is
/// registered on the stars and averaged (clean, low-noise sky); the FOREGROUND
/// is averaged WITHOUT alignment so the landscape stays in the tripod's
/// position instead of smearing; the two are composited through the drawn
/// horizon line (<see cref="HorizonMask"/> + <see cref="NightscapeBlend"/>).
///
/// <para>Reuses the same on-disk stacking primitives as
/// <see cref="BatchStackingService"/> (FITS read, <see cref="BayerDebayer"/>,
/// <see cref="StarDetector"/>, <see cref="StarMatcher"/>,
/// <see cref="ImageResampler"/>) and the same job shape as
/// <see cref="PreprocessOrchestrator"/> (start → poll status → abort). The two
/// stacks run in ONE pass over the frames to keep memory to two running-sum
/// buffers rather than holding every frame.</para>
/// </summary>
public class NightscapeStackService {
    private readonly ILogger<NightscapeStackService> _logger;
    private readonly ConcurrentDictionary<string, NightscapeProgress> _jobs = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cts = new();

    public NightscapeStackService(ILogger<NightscapeStackService> logger) {
        _logger = logger;
    }

    public string StartJob(NightscapeRequest req) {
        var jobId = Guid.NewGuid().ToString("N")[..8];
        _jobs[jobId] = new NightscapeProgress {
            JobId = jobId, InProgress = true, Phase = "Preparing",
            Total = req.Frames?.Count ?? 0
        };
        var cts = new CancellationTokenSource();
        _cts[jobId] = cts;
        _ = Task.Run(() => RunAsync(jobId, req, cts.Token));
        return jobId;
    }

    public NightscapeProgress? GetStatus(string jobId)
        => _jobs.TryGetValue(jobId, out var p) ? p : null;

    public void Abort(string jobId) {
        if (_cts.TryGetValue(jobId, out var cts)) { try { cts.Cancel(); } catch { } }
    }

    /// <summary>Render one auto-stretched preview JPEG of a single frame, so the
    /// operator can draw the horizon over the real scene. Any frame works: the
    /// tripod is fixed, so the horizon sits at the same place in every sub.</summary>
    public byte[]? RenderPreview(string framePath, int maxDim = 1600) {
        try {
            using var fs = File.OpenRead(framePath);
            var img = FITSReader.Read(fs);
            int w = img.Properties.Width, h = img.Properties.Height;
            var pat = img.Properties.BayerPattern;
            if (pat != BayerPatternEnum.None) {
                var ch = BayerDebayer.Bilinear(img.Data, w, h, pat);
                var planar = new ushort[w * h * 3];
                Array.Copy(ch.R, 0, planar, 0, w * h);
                Array.Copy(ch.G, 0, planar, w * h, w * h);
                Array.Copy(ch.B, 0, planar, 2 * w * h, w * h);
                return FitsThumbnailer.RenderJpegFromRgbPlanes(planar, w, h, img.Properties.BitDepth, maxDim, 88);
            }
            return FitsThumbnailer.RenderJpegFromBuffer(img.Data, w, h, img.Properties.BitDepth, maxDim, 88);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Nightscape preview render failed for {Path}", framePath);
            return null;
        }
    }

    private async Task RunAsync(string jobId, NightscapeRequest req, CancellationToken ct) {
        try {
            var frames = req.Frames;
            if (frames == null || frames.Count < 2)
                throw new InvalidOperationException("Add at least two frames.");

            Update(jobId, p => p with { Phase = "Stacking", Stage = "reading reference", Total = frames.Count });

            // Reference frame fixes the geometry and the star catalogue.
            int w, h; BayerPatternEnum pattern; ushort[] refData;
            using (var fs = File.OpenRead(frames[0])) {
                var img = FITSReader.Read(fs);
                refData = img.Data; w = img.Properties.Width; h = img.Properties.Height;
                pattern = img.Properties.BayerPattern;
            }
            int channels = pattern == BayerPatternEnum.None ? 1 : 3;
            int wh = w * h;

            var detector = new StarDetector();
            var refStars = detector.Detect(Luminance(refData, w, h, pattern), w, h);

            var skySum = new float[channels * wh];
            var fgSum = new float[channels * wh];
            int skyUsed = 0;

            for (int i = 0; i < frames.Count; i++) {
                ct.ThrowIfCancellationRequested();
                Update(jobId, p => p with { Stage = $"frame {i + 1}/{frames.Count}", Done = i });

                ushort[] data;
                using (var fs = File.OpenRead(frames[i])) {
                    var img = FITSReader.Read(fs);
                    if (img.Properties.Width != w || img.Properties.Height != h)
                        throw new InvalidOperationException("All frames must be the same size.");
                    data = img.Data;
                }
                var planes = Planes(data, w, h, pattern);

                // FOREGROUND: no alignment, every frame contributes to every pixel.
                for (int c = 0; c < channels; c++) {
                    var pl = planes[c]; int off = c * wh;
                    for (int k = 0; k < wh; k++) fgSum[off + k] += pl[k];
                }

                // SKY: register to the reference (frame 0 is the identity).
                AffineTransform? t = null;
                bool aligned = i == 0;
                if (i != 0) {
                    var stars = detector.Detect(Luminance(data, w, h, pattern), w, h);
                    t = StarMatcher.Match(refStars, stars);
                    if (t == null) t = StarMatcher.Match(refStars, stars, maxSearchRadius: 250.0);
                    aligned = t != null;
                }
                if (aligned) {
                    for (int c = 0; c < channels; c++) {
                        var warped = (i == 0 || t == null)
                            ? planes[c]
                            : ImageResampler.ApplyTransform(planes[c], w, h, t);
                        int off = c * wh;
                        for (int k = 0; k < wh; k++) skySum[off + k] += warped[k];
                    }
                    skyUsed++;
                }
                await Task.Yield();
            }

            if (skyUsed == 0)
                throw new InvalidOperationException(
                    "Could not align any frame on the stars. Check the frames have visible stars.");

            // Build the two masters (running means).
            var sky = new ushort[channels * wh];
            var fg = new ushort[channels * wh];
            float invSky = 1f / skyUsed, invFg = 1f / frames.Count;
            for (int k = 0; k < channels * wh; k++) {
                sky[k] = Clamp(skySum[k] * invSky);
                fg[k] = Clamp(fgSum[k] * invFg);
            }

            Update(jobId, p => p with { Phase = "Blending", Stage = "horizon mask", Done = frames.Count });
            var line = (req.Horizon ?? new List<NightscapePoint>())
                .Select(pt => (pt.X, pt.Y)).ToList();
            var coverage = HorizonMask.BuildCoverage(line, w, h, req.FeatherPx);
            var blended = NightscapeBlend.Composite16(sky, fg, coverage, w, h, channels);

            Update(jobId, p => p with { Phase = "Rendering", Stage = "writing output" });
            var dir = string.IsNullOrWhiteSpace(req.OutputDir)
                ? Path.GetDirectoryName(frames[0]) ?? "." : req.OutputDir!;
            Directory.CreateDirectory(dir);
            var baseName = Sanitize(string.IsNullOrWhiteSpace(req.OutputName) ? "nightscape" : req.OutputName!);

            var jpgPath = UniquePath(dir, baseName, ".jpg");
            byte[] jpg = channels == 3
                ? FitsThumbnailer.RenderJpegFromRgbPlanes(blended, w, h, 16, Math.Max(w, h), 92)
                : FitsThumbnailer.RenderJpegFromBuffer(blended, w, h, 16, Math.Max(w, h), 92);
            await File.WriteAllBytesAsync(jpgPath, jpg, ct);

            // A 16-bit FITS master for anyone who wants to keep processing.
            string? fitsPath = null;
            try {
                var props = new ImageProperties {
                    Width = w, Height = h, BitDepth = 16, Channels = channels,
                    BayerPattern = BayerPatternEnum.None, IsBayered = false
                };
                var meta = new ImageMetaData {
                    CreationTime = DateTime.UtcNow,
                    Camera = new ImageMetaData.CameraInfo(),
                    Telescope = new ImageMetaData.TelescopeInfo(),
                    Observer = new ImageMetaData.ObserverInfo(),
                    Target = new ImageMetaData.TargetInfo { Name = baseName },
                    Exposure = new ImageMetaData.ExposureInfo { ImageType = "MASTERLIGHT" }
                };
                fitsPath = UniquePath(dir, baseName, ".fits");
                FITSWriter.Write(new BaseImageData(blended, props, meta), fitsPath,
                    customKeywords: new List<KeyValuePair<string, string>> {
                        new("NCOMBINE", frames.Count.ToString()),
                        new("NSKYALGN", skyUsed.ToString()),
                        new("STACKTYP", "NIGHTSCAPE")
                    });
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Nightscape FITS master write failed (JPEG still saved)");
            }

            _logger.LogInformation(
                "Nightscape job {Job}: {Sky}/{Total} frames aligned for the sky → {Path}",
                jobId, skyUsed, frames.Count, jpgPath);

            Update(jobId, p => p with {
                InProgress = false, Phase = "Done", Stage = "",
                Done = frames.Count, SkyAligned = skyUsed,
                OutputPath = jpgPath, FitsPath = fitsPath
            });
        } catch (OperationCanceledException) {
            Update(jobId, p => p with { InProgress = false, Phase = "Cancelled" });
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Nightscape stack job {Job} failed", jobId);
            Update(jobId, p => p with { InProgress = false, Phase = "Failed", Error = ex.Message });
        }
    }

    // ---- helpers ----

    private static ushort[][] Planes(ushort[] data, int w, int h, BayerPatternEnum pattern) {
        if (pattern == BayerPatternEnum.None) return new[] { data };
        var ch = BayerDebayer.Bilinear(data, w, h, pattern);
        return new[] { ch.R, ch.G, ch.B };
    }

    private static ushort[] Luminance(ushort[] data, int w, int h, BayerPatternEnum pattern) {
        if (pattern == BayerPatternEnum.None) return data;
        return BayerDebayer.ToLuminance(BayerDebayer.Bilinear(data, w, h, pattern));
    }

    private static ushort Clamp(float v) {
        if (v <= 0) return 0;
        if (v >= ushort.MaxValue) return ushort.MaxValue;
        return (ushort)(v + 0.5f);
    }

    private static string Sanitize(string s) {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }

    private static string UniquePath(string dir, string baseName, string ext) {
        var path = Path.Combine(dir, baseName + ext);
        int copy = 1;
        while (File.Exists(path))
            path = Path.Combine(dir, baseName + $"_{copy++}" + ext);
        return path;
    }

    private void Update(string jobId, Func<NightscapeProgress, NightscapeProgress> f) {
        _jobs.AddOrUpdate(jobId,
            _ => f(new NightscapeProgress { JobId = jobId }),
            (_, prev) => f(prev));
    }
}

/// <param name="Frames">Absolute paths of the light subs (FITS/XISF).</param>
/// <param name="Horizon">The drawn horizon line, points in NORMALISED frame
/// coordinates (x, y in 0..1, y downward).</param>
/// <param name="FeatherPx">Half-width of the soft transition band, in pixels.</param>
/// <param name="OutputDir">Where the result lands; defaults to the frames' folder.</param>
public record NightscapeRequest(
    List<string> Frames,
    List<NightscapePoint> Horizon,
    double FeatherPx,
    string? OutputDir,
    string? OutputName);

public record NightscapePoint(double X, double Y);

public record NightscapeProgress {
    public string JobId { get; init; } = "";
    public bool InProgress { get; init; }
    public string Phase { get; init; } = "";   // Preparing/Stacking/Blending/Rendering/Done/Failed/Cancelled
    public string Stage { get; init; } = "";
    public int Done { get; init; }
    public int Total { get; init; }
    public int SkyAligned { get; init; }
    public string? OutputPath { get; init; }
    public string? FitsPath { get; init; }
    public string? Error { get; init; }
}
