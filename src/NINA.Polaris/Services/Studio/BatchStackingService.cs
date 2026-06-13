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

namespace NINA.Polaris.Services.Studio;

/// <summary>
/// Stack N already-calibrated (or raw) light frames into a single
/// integrated master light. The offline counterpart of
/// LiveStackingService, same star-matching alignment primitives
/// (<see cref="StarDetector"/>, <see cref="StarMatcher"/>,
/// <see cref="ImageResampler"/>) but no streaming relay; everything
/// runs to completion in a background job and produces one FITS.
///
/// Pipeline per job:
///   1. Load all inputs; detect stars in each.
///   2. Pick the reference frame (frame with the most detected stars
///     , that's the most robust target for affine fitting).
///   3. For every other frame: match its star list against the
///      reference's, compute the affine transform, resample the pixels
///      into the reference's coordinate system. Frames whose transform
///      fits below a star-match threshold are skipped (the job still
///      reports them in the dropped count).
///   4. Stack the aligned buffers via the chosen IntegrationMethod
///      from ST-3 (Mean / Median / SigmaClippedMean).
///   5. Write {rig}/integrated/{target}/{filter}/master_light_*.fits
///      with NCOMBINE / EXPTOTAL / INTMETH / REJECT custom keywords.
///   6. Trigger a library rescan so the master shows up in the
///      browser.
///
/// Memory model: same as ST-3 master integration, every input
/// (post-alignment) sits in RAM at once. For typical session sizes
/// (20-30 × 20 MP) that's ~1 GB peak; tiled / streaming integration is
/// tracked as a follow-up if anyone tries 100+ huge frames.
/// </summary>
public class BatchStackingService {
    private readonly FrameLibraryService _library;
    private readonly ProfileService _profile;
    private readonly ILogger<BatchStackingService> _logger;
    private readonly ConcurrentDictionary<string, IntegrationProgress> _jobs = new();

    public BatchStackingService(FrameLibraryService library, ProfileService profile,
                                ILogger<BatchStackingService> logger) {
        _library = library;
        _profile = profile;
        _logger = logger;
    }

    // UNIF-3a: switched FrameIds -> FramePaths; service opens FITS by
    // path and reads target/filter from headers directly. Decouples
    // from FrameLibrary SQLite cache.
    public record IntegrationRequest(
        List<string> FramePaths,
        string Method);

    public string StartJob(IntegrationRequest req) {
        if (!Enum.TryParse<IntegrationMethod>(req.Method, true, out var method)) {
            method = IntegrationMethod.SigmaClippedMean;
        }
        var jobId = Guid.NewGuid().ToString("N")[..8];
        _jobs[jobId] = new IntegrationProgress {
            JobId = jobId,
            InProgress = true,
            Total = req.FramePaths.Count,
            Stage = "queued"
        };
        _ = Task.Run(() => RunJob(jobId, req.FramePaths, method));
        return jobId;
    }

    public IntegrationProgress? GetStatus(string jobId)
        => _jobs.TryGetValue(jobId, out var p) ? p : null;

    private void RunJob(string jobId, IReadOnlyList<string> framePaths, IntegrationMethod method) {
        try {
            // ---- Phase 1: load + detect stars ----------------------
            _jobs[jobId] = _jobs[jobId] with { Stage = "loading", Done = 0 };
            var detector = new StarDetector();
            var loaded = new List<(BaseImageData Img, List<DetectedStar> Stars, string Name)>(framePaths.Count);
            int? width = null, height = null;
            int bitDepth = 16;
            string target = "", filter = "";
            // OSC handling: when the frames are Bayered we debayer each one
            // to RGB and stack in colour. Registering/resampling the raw CFA
            // mosaic as mono blends adjacent R/G/B sites (bilinear) and the
            // master collapses to grey with a checkerboard. `pattern` is read
            // from the first frame's BAYERPAT; None => plain mono stacking.
            var pattern = NINA.Core.Enum.BayerPatternEnum.None;

            for (int i = 0; i < framePaths.Count; i++) {
                var path = framePaths[i];
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) {
                    _logger.LogWarning("Frame missing on disk, skipping: {Path}", path);
                    continue;
                }
                var frameName = Path.GetFileName(path);
                using var fs = File.OpenRead(path);
                var img = FITSReader.Read(fs);
                if (width == null) {
                    width = img.Properties.Width;
                    height = img.Properties.Height;
                    bitDepth = img.Properties.BitDepth;
                    pattern = img.Properties.BayerPattern;
                    var t = img.MetaData.Target.Name;
                    target = string.IsNullOrEmpty(t) ? "Unknown" : t;
                    var f = img.MetaData.Exposure.Filter;
                    filter = string.IsNullOrEmpty(f) ? "L" : f;
                } else if (img.Properties.Width != width || img.Properties.Height != height) {
                    _logger.LogWarning("Frame {Name} size mismatch, skipping", frameName);
                    continue;
                }
                // Detect stars on luminance for OSC (so the matcher sees real
                // stars, not the CFA), on the raw buffer for mono.
                ushort[] starSrc = img.Data;
                if (pattern != NINA.Core.Enum.BayerPatternEnum.None) {
                    var ch = BayerDebayer.Bilinear(img.Data, img.Properties.Width, img.Properties.Height, pattern);
                    starSrc = BayerDebayer.ToLuminance(ch);
                }
                var stars = detector.Detect(starSrc, img.Properties.Width, img.Properties.Height);
                loaded.Add((img, stars, frameName));
                _jobs[jobId] = _jobs[jobId] with { Done = i + 1 };
            }

            if (loaded.Count < 2)
                throw new InvalidOperationException("Need at least 2 valid frames to integrate.");

            // ---- Phase 2: pick reference, align everything ---------
            // Reference = frame with the most detected stars. That
            // gives StarMatcher the largest catalogue to match against
            // and produces the most robust transforms.
            // Reset Done for the next phase but leave Total untouched
            // so the overall job's "done / total" headline stays
            // anchored on the input frame count for the whole run.
            _jobs[jobId] = _jobs[jobId] with { Stage = "aligning", Done = 0 };
            var refIdx = 0;
            for (int i = 1; i < loaded.Count; i++) {
                if (loaded[i].Stars.Count > loaded[refIdx].Stars.Count) refIdx = i;
            }
            var refStars = loaded[refIdx].Stars;
            // CCALB-0a: capture the reference frame's WCS (if it was
            // plate-solved upstream) so we can stamp the output master
            // with the same coordinates. Without this, the integrated
            // master loses pointing info and PCC cannot match catalog
            // stars without re-solving.
            var refWcs = loaded[refIdx].Img.Properties.Wcs;
            _logger.LogInformation("Integration job {Job}: reference frame {File} ({N} stars)",
                jobId, loaded[refIdx].Name, refStars.Count);

            int W = width!.Value;
            int H = height!.Value;
            // Debayer a raw buffer into colour planes (R,G,B) for OSC, or
            // wrap the mono buffer as a single plane. Stacking then runs
            // per-plane so colour is preserved and the resample interpolates
            // within a channel instead of across the CFA mosaic.
            ushort[][] PlanesOf(ushort[] data) {
                if (pattern == NINA.Core.Enum.BayerPatternEnum.None) return new[] { data };
                var ch = BayerDebayer.Bilinear(data, W, H, pattern);
                return new[] { ch.R, ch.G, ch.B };
            }
            var aligned = new List<ushort[][]>(loaded.Count);
            var keptNames = new List<string>(loaded.Count);
            double totalExposure = 0;
            int dropped = 0;

            for (int i = 0; i < loaded.Count; i++) {
                if (i == refIdx) {
                    // Reference goes in untouched (debayered, not resampled).
                    aligned.Add(PlanesOf(loaded[i].Img.Data));
                    keptNames.Add(loaded[i].Name);
                    totalExposure += loaded[i].Img.MetaData.Exposure.ExposureTime;
                } else {
                    var transform = StarMatcher.Match(refStars, loaded[i].Stars);
                    if (transform == null) {
                        _logger.LogWarning("Drop frame {File}: alignment failed", loaded[i].Name);
                        dropped++;
                    } else {
                        // Pre-resample alignment-quality probe: project
                        // every current-frame star through the transform
                        // and find its nearest reference star. Median
                        // residual >1px means the transform smears the
                        // master and ASTAP will fail to match quads
                        // even though raw star counts look healthy.
                        var residual = MedianAlignmentResidualPx(
                            refStars, loaded[i].Stars, transform);
                        if (residual > 2.0) {
                            _logger.LogWarning(
                                "Frame {File}: alignment residual median {Residual:F2}px " +
                                "exceeds 2px (transform: M00={M00:F3} M01={M01:F3} " +
                                "M10={M10:F3} M11={M11:F3} Tx={Tx:F1} Ty={Ty:F1}); " +
                                "expect smearing in the integrated master.",
                                loaded[i].Name, residual,
                                transform.M00, transform.M01, transform.M10, transform.M11,
                                transform.Tx, transform.Ty);
                        } else {
                            _logger.LogDebug(
                                "Frame {File}: aligned, residual median {Residual:F2}px " +
                                "(Tx={Tx:F1}, Ty={Ty:F1})",
                                loaded[i].Name, residual, transform.Tx, transform.Ty);
                        }

                        // Resample EACH colour plane with the same transform
                        // (the transform came from luminance star matching).
                        var srcPlanes = PlanesOf(loaded[i].Img.Data);
                        var resampled = new ushort[srcPlanes.Length][];
                        for (int p = 0; p < srcPlanes.Length; p++)
                            resampled[p] = ImageResampler.ApplyTransform(srcPlanes[p], W, H, transform);
                        aligned.Add(resampled);
                        keptNames.Add(loaded[i].Name);
                        totalExposure += loaded[i].Img.MetaData.Exposure.ExposureTime;
                    }
                }
                // PERF/mem: release this frame's original pixel buffer now
                // that it has been resampled into `aligned`. (The reference
                // frame's buffer is owned by aligned[] via the Add above, so
                // nulling the slot here can't collect it.) Without this the
                // originals (N frames) and the aligned copies (up to N) are
                // alive simultaneously through Phase 2 — a ~2N peak. Freeing
                // each original as we go holds peak at ~N frames, which is
                // the difference between completing and OOM-ing a large
                // batch on a 4 GB Pi. Median / SigmaClip still need all N
                // aligned frames in RAM for Phase 3 (per-pixel across
                // frames); true streaming/tiling stays a follow-up.
                loaded[i] = (null!, null!, loaded[i].Name);
                _jobs[jobId] = _jobs[jobId] with { Done = i + 1, Dropped = dropped };
            }

            if (aligned.Count < 2)
                throw new InvalidOperationException(
                    $"Only {aligned.Count} frame(s) survived alignment. Need ≥2.");

            // Free the un-aligned copies, they're no longer needed
            // and the aligned[] array now owns the working pixels.
            loaded.Clear();

            // ---- Phase 3: per-pixel integration --------------------
            // Don't fold this phase's row counter into Done / Total —
            // the previous implementation set Total = H (image
            // height) here, which made "done / total" briefly read as
            // "row 2841 of 3672 image rows" while the headline number
            // really wants to stay "X of N frames". Track row
            // progress on IntegrationPercent instead so the UI can
            // surface it as a sub-bar without overwriting the frame
            // counters. Done bumps to aligned.Count up front so the
            // headline reads "N frames" through this phase.
            _jobs[jobId] = _jobs[jobId] with {
                Stage = "integrating",
                Done = aligned.Count,
                IntegrationPercent = 0,
            };
            int N = aligned.Count;
            // 1 plane for mono, 3 (R,G,B) for OSC. Integrate each plane
            // independently across all frames; the output is plane-sequential
            // (R then G then B), the FITS RGB-cube convention.
            int nPlanes = aligned.Count > 0 ? aligned[0].Length : 1;
            int planeSize = W * H;
            var output = new ushort[planeSize * nPlanes];
            var stacks = aligned.ToArray();

            int rowsDone = 0;
            int lastReportedPct = 0;
            int totalRows = nPlanes * H;
            for (int pl = 0; pl < nPlanes; pl++) {
                int plane = pl;
                int planeOff = plane * planeSize;
                Parallel.For(0, H, () => new ushort[N], (y, _, scratch) => {
                    int rowOff = y * W;
                    for (int x = 0; x < W; x++) {
                        int idx = rowOff + x;
                        int valid = 0;
                        // Skip pixels whose value is 0, ImageResampler
                        // marks off-canvas regions as 0 after the affine
                        // shift, and rolling them into the average drags
                        // the master down at the edges.
                        for (int k = 0; k < N; k++) {
                            var v = stacks[k][plane][idx];
                            if (v > 0) { scratch[valid++] = v; }
                        }
                        int outIdx = planeOff + idx;
                        if (valid == 0) {
                            output[outIdx] = 0;
                        } else {
                            var slice = ((ReadOnlySpan<ushort>)scratch)[..valid];
                            output[outIdx] = method switch {
                                IntegrationMethod.Mean   => IntegrationMath.Mean(slice),
                                IntegrationMethod.Median => IntegrationMath.Median(slice),
                                IntegrationMethod.SigmaClippedMean
                                                         => IntegrationMath.SigmaClippedMean(slice),
                                _                        => IntegrationMath.Mean(slice)
                            };
                        }
                    }
                    var done = System.Threading.Interlocked.Increment(ref rowsDone);
                    // Throttle the status writeback to whole percent ticks.
                    int pct = (int)(done * 100L / totalRows);
                    if (pct != lastReportedPct) {
                        lastReportedPct = pct;
                        _jobs[jobId] = _jobs[jobId] with { IntegrationPercent = pct };
                    }
                    return scratch;
                }, _ => { });
            }
            _jobs[jobId] = _jobs[jobId] with { IntegrationPercent = 100 };

            // ---- Phase 4: write integrated master FITS -------------
            _jobs[jobId] = _jobs[jobId] with { Stage = "writing" };

            var rigName = _profile.ActiveEquipmentProfile?.Name ?? "Default";
            var outRoot = _profile.Active.ImageOutputDir
                ?? throw new InvalidOperationException("ImageOutputDir not set.");
            var dir = Path.Combine(outRoot, Sanitize(rigName), "integrated",
                Sanitize(target), Sanitize(filter));
            Directory.CreateDirectory(dir);

            var fileName =
                $"master_light_{Sanitize(target)}_{Sanitize(filter)}_x{N}_{totalExposure:0}s.fits";
            var outPath = Path.Combine(dir, fileName);
            int copy = 1;
            while (File.Exists(outPath))
                outPath = Path.Combine(dir,
                    Path.GetFileNameWithoutExtension(fileName) + $"_{copy++}.fits");

            var props = new ImageProperties {
                Width = W, Height = H, BitDepth = bitDepth,
                // Debayered RGB output (3 channels) for OSC, or mono (1).
                // Either way it is no longer a CFA mosaic.
                Channels = nPlanes,
                BayerPattern = NINA.Core.Enum.BayerPatternEnum.None,
                IsBayered = false,
                // CCALB-0a: carry WCS forward so the master is plate-
                // solved already from PCC's perspective. Non-reference
                // frames get resampled onto the reference's grid, so
                // the reference's WCS is correct for the output.
                Wcs = refWcs,
            };
            // Reuse the metadata from the original first input we kept
            // (target name + camera + observer survive); flag as
            // MASTERLIGHT and stamp the integration metadata via
            // custom keywords. Build a synthetic exposure that records
            // the *total* time so downstream tools display "X hours".
            var meta = new ImageMetaData {
                CreationTime = DateTime.UtcNow,
                Camera   = aligned.Count > 0 ? new ImageMetaData.CameraInfo()    : new ImageMetaData.CameraInfo(),
                Telescope = new ImageMetaData.TelescopeInfo(),
                Observer = new ImageMetaData.ObserverInfo(),
                Target   = new ImageMetaData.TargetInfo { Name = target },
                Exposure = new ImageMetaData.ExposureInfo {
                    ExposureTime = totalExposure,
                    Filter       = filter,
                    ImageType    = "MASTERLIGHT"
                }
            };
            var masterData = new BaseImageData(output, props, meta);

            var customKeywords = new List<KeyValuePair<string, string>> {
                new("NCOMBINE", N.ToString()),
                new("EXPTOTAL", totalExposure.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)),
                new("INTMETH",  method.ToString()),
                new("REJECT",   dropped.ToString()),
                new("STACKREF", keptNames.Count > 0 ? Path.GetFileName(keptNames[0]) : "")
            };
            FITSWriter.Write(masterData, outPath, customKeywords: customKeywords);

            _logger.LogInformation(
                "Integration job {Job}: {N}/{Total} frames stacked → {Path}",
                jobId, N, framePaths.Count, outPath);

            _ = Task.Run(() => _library.RescanAsync());

            _jobs[jobId] = _jobs[jobId] with {
                InProgress = false,
                Stage = "done",
                OutputPath = outPath,
                Combined = N,
                Dropped = dropped,
                TotalExposureSec = totalExposure
            };
        } catch (Exception ex) {
            _logger.LogError(ex, "Integration job {JobId} failed", jobId);
            _jobs[jobId] = _jobs[jobId] with {
                InProgress = false,
                Stage = "error",
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// Median nearest-neighbor residual (in reference-pixel space)
    /// after applying the transform to every current-frame star.
    /// Used as a post-fit alignment-quality probe in the integration
    /// log: small values mean the affine truly maps cur → ref, large
    /// values mean the matcher locked onto a wrong-but-plausible
    /// transform and the master will smear.
    /// </summary>
    private static double MedianAlignmentResidualPx(
            IReadOnlyList<DetectedStar> refStars,
            IReadOnlyList<DetectedStar> curStars,
            AffineTransform transform) {
        if (refStars.Count == 0 || curStars.Count == 0) return double.NaN;
        // PERF #366: index the reference stars in a spatial grid so each
        // projected current star finds its nearest by scanning nearby cells
        // (expanding-ring search returns the exact global nearest), instead
        // of an O(cur * ref) brute-force scan per frame.
        var refGrid = new SpatialGrid<byte>(8.0);
        foreach (var rs in refStars) refGrid.Add(rs.X, rs.Y, 0);
        var residuals = new List<double>(curStars.Count);
        foreach (var cs in curStars) {
            var (tx, ty) = transform.Apply(cs.X, cs.Y);
            if (refGrid.TryNearest(tx, ty, double.PositiveInfinity, out _, out double best2))
                residuals.Add(Math.Sqrt(best2));
        }
        if (residuals.Count == 0) return double.NaN;
        residuals.Sort();
        return residuals[residuals.Count / 2];
    }

    private static string Sanitize(string s) {
        if (string.IsNullOrWhiteSpace(s)) return "Unknown";
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Replace(' ', '_');
    }
}

public record IntegrationProgress {
    public string JobId { get; init; } = "";
    public bool InProgress { get; init; }

    // Frame-count progress. Done counts inputs the current phase has
    // touched; Total is pinned to the input frame count for the
    // entire job so the UI's "done / total" reads sensibly all the
    // way through (loading 5/20, aligning 14/20, integrating 20/20,
    // done 20/20). Don't shove image-height or any other denominator
    // into Total — fold sub-phase progress into IntegrationPercent
    // instead.
    public int Done { get; init; }
    public int Total { get; init; }

    // 0..100 progress through the integration phase's per-row sweep.
    // Reads 0 outside the integrating stage, climbs to 100 at the
    // start of the writing stage, stays at 100 thereafter.
    public int IntegrationPercent { get; init; }

    public int Combined { get; init; }
    public int Dropped { get; init; }
    public double TotalExposureSec { get; init; }
    public string Stage { get; init; } = "";
    public string? Error { get; init; }
    public string? OutputPath { get; init; }
}