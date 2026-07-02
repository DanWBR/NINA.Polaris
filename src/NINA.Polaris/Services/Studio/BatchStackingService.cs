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
///   1. <b>Detect</b>: read each input once, detect its stars, then drop
///      the pixels. Only the star lists (small) are retained, so this
///      phase holds ~one frame in RAM at a time.
///   2. Pick the reference frame (most detected stars — the most robust
///      target for affine fitting).
///   3. <b>Align + spill</b>: re-read each frame, match it to the
///      reference, resample onto the reference grid, and write the
///      aligned plane(s) to a temp file on disk. Frames whose transform
///      fits below the star-match threshold are dropped (still counted).
///   4. <b>Integrate (tiled)</b>: stream the spilled aligned planes back
///      in horizontal strips and reduce per-pixel across frames via the
///      chosen IntegrationMethod (Mean / Median / SigmaClippedMean).
///   5. Write {rig}/integrated/{target}/{filter}/master_light_*.fits
///      with NCOMBINE / EXPTOTAL / INTMETH / REJECT custom keywords.
///   6. Trigger a library rescan so the master shows up in the browser.
///
/// Memory model: the old implementation kept every aligned frame in RAM
/// for the per-pixel integration — O(N · W · H), tripled for OSC (debayer
/// to 3 planes), which pushed a 4 GB Pi past its limit on a 20-30 × 24 MP
/// OSC stack (~4 GB). Now the aligned frames are spilled to disk and the
/// integration streams them one strip at a time, so peak RAM is ~one
/// frame during alignment plus one tile (≈ N · tileRows · W) during
/// integration — flat regardless of frame count or sensor size. The cost
/// is N× a frame of temp disk and a second read of each input.
/// </summary>
public class BatchStackingService {
    private readonly FrameLibraryService _library;
    private readonly ProfileService _profile;
    private readonly ILogger<BatchStackingService> _logger;
    // Optional: drives the pre-flight RAM guard. Null in unit tests that
    // construct the service directly — the guard then fails open.
    private readonly HostMetricsService? _metrics;
    private readonly ConcurrentDictionary<string, IntegrationProgress> _jobs = new();

    // Tiled-integration RAM budget for the spilled aligned planes (same
    // reasoning as MasterFrameService). One plane is integrated at a time,
    // holding ~this many bytes of strip data across all N frames.
    private const long StripBudgetBytes = 96L * 1024 * 1024;

    public BatchStackingService(FrameLibraryService library, ProfileService profile,
                                ILogger<BatchStackingService> logger,
                                HostMetricsService? metrics = null) {
        _library = library;
        _profile = profile;
        _logger = logger;
        _metrics = metrics;
    }

    /// <summary>Currently-available system memory in bytes, or 0 when the
    /// metrics sampler hasn't produced a snapshot yet (guard fails open).</summary>
    private long AvailableBytes() {
        var s = _metrics?.Latest;
        if (s == null || s.MemoryTotalMB <= 0) return 0;
        return Math.Max(0, s.MemoryTotalMB - s.MemoryUsedMB) * 1024L * 1024L;
    }

    // UNIF-3a: switched FrameIds -> FramePaths; service opens FITS by
    // path and reads target/filter from headers directly. Decouples
    // from FrameLibrary SQLite cache.
    public record IntegrationRequest(
        List<string> FramePaths,
        string Method,
        // Drizzle: 1 = off (native-resolution resample + combine), 2/3 = drizzle
        // integrate onto a scale× finer grid. Pixfrac is the drop shrink (0,1].
        int DrizzleScale = 1,
        double DrizzlePixfrac = 1.0);

    public string StartJob(IntegrationRequest req) {
        if (!Enum.TryParse<IntegrationMethod>(req.Method, true, out var method)) {
            method = IntegrationMethod.SigmaClippedMean;
        }
        int scale = Math.Clamp(req.DrizzleScale, 1, 3);
        double pixfrac = Math.Clamp(req.DrizzlePixfrac, 0.1, 1.0);
        var jobId = Guid.NewGuid().ToString("N")[..8];
        _jobs[jobId] = new IntegrationProgress {
            JobId = jobId,
            InProgress = true,
            Total = req.FramePaths.Count,
            Stage = "queued"
        };
        _ = Task.Run(() => RunJob(jobId, req.FramePaths, method, scale, pixfrac));
        return jobId;
    }

    public IntegrationProgress? GetStatus(string jobId)
        => _jobs.TryGetValue(jobId, out var p) ? p : null;

    /// <summary>
    /// Recommend a drizzle scale for a set of frames by measuring the median
    /// star FWHM (px) on a small sample and combining it with the sub count.
    /// FWHM in pixels is the sampling measure drizzle cares about; the rig's
    /// pixel size / focal length aren't needed. Reads at most 5 frames.
    /// </summary>
    public DrizzleAdvisor.Advice AdviseDrizzle(IReadOnlyList<string> framePaths) {
        var hfrs = new List<double>();
        var detector = new StarDetector();
        int sampled = 0;
        foreach (var path in framePaths) {
            if (sampled >= 5) break;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
            try {
                BaseImageData img;
                using (var fs = File.OpenRead(path)) img = FITSReader.Read(fs);
                ushort[] src = img.Data;
                var pat = img.Properties.BayerPattern;
                if (pat != NINA.Core.Enum.BayerPatternEnum.None) {
                    var ch = BayerDebayer.Bilinear(img.Data, img.Properties.Width, img.Properties.Height, pat);
                    src = BayerDebayer.ToLuminance(ch);
                }
                var stars = detector.Detect(src, img.Properties.Width, img.Properties.Height);
                foreach (var s in stars) if (s.HFR > 0) hfrs.Add(s.HFR);
                sampled++;
            } catch (Exception ex) {
                _logger.LogDebug(ex, "Drizzle advice: skipped {Path}", path);
            }
        }
        double medianFwhm = 0;
        if (hfrs.Count > 0) {
            hfrs.Sort();
            medianFwhm = DrizzleAdvisor.FwhmFromHfr(hfrs[hfrs.Count / 2]);
        }
        return DrizzleAdvisor.Recommend(medianFwhm, framePaths.Count);
    }

    private void RunJob(string jobId, IReadOnlyList<string> framePaths, IntegrationMethod method,
                        int drizzleScale = 1, double drizzlePixfrac = 1.0) {
        string? tempDir = null;
        try {
            // ---- Phase 1: detect stars (no pixels retained) ----------
            _jobs[jobId] = _jobs[jobId] with { Stage = "loading", Done = 0 };
            var detector = new StarDetector();
            var frames = new List<(string Path, List<DetectedStar> Stars, double Exposure, string Name)>(framePaths.Count);
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
                BaseImageData img;
                using (var fs = File.OpenRead(path)) img = FITSReader.Read(fs);
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
                frames.Add((path, stars, img.MetaData.Exposure.ExposureTime, frameName));
                // img + starSrc fall out of scope here; nothing pins the pixel
                // buffers, so peak stays ~one frame through this phase.
                _jobs[jobId] = _jobs[jobId] with { Done = i + 1 };
            }

            if (frames.Count < 2)
                throw new InvalidOperationException("Need at least 2 valid frames to integrate.");

            // Pre-flight RAM guard now that dimensions + colour are known.
            {
                int guardPlanes = pattern == NINA.Core.Enum.BayerPatternEnum.None ? 1 : 3;
                long needBytes = StackMemoryGuard.EstimateLightBytes(
                    width!.Value, height!.Value, guardPlanes, StripBudgetBytes);
                var (memOk, memMsg) = StackMemoryGuard.Check(
                    needBytes, AvailableBytes(), "integrate these lights");
                if (!memOk) {
                    _logger.LogWarning("Integration job {JobId} refused: {Msg}", jobId, memMsg);
                    _jobs[jobId] = _jobs[jobId] with {
                        InProgress = false, Stage = "error", Error = memMsg
                    };
                    return;
                }
            }

            // ---- Phase 2: pick reference, align + spill to disk -------
            // Reference = frame with the most detected stars (largest
            // catalogue for StarMatcher, most robust transforms). Reset Done
            // for this phase but leave Total on the input frame count so the
            // headline "done / total" stays anchored across the whole run.
            _jobs[jobId] = _jobs[jobId] with { Stage = "aligning", Done = 0 };
            var refIdx = 0;
            for (int i = 1; i < frames.Count; i++)
                if (frames[i].Stars.Count > frames[refIdx].Stars.Count) refIdx = i;
            var refStars = frames[refIdx].Stars;
            _logger.LogInformation("Integration job {Job}: reference frame {File} ({N} stars)",
                jobId, frames[refIdx].Name, refStars.Count);

            int W = width!.Value;
            int H = height!.Value;
            int planeSize = W * H;
            int nPlanes = pattern == NINA.Core.Enum.BayerPatternEnum.None ? 1 : 3;

            // Debayer a raw buffer into colour planes (R,G,B) for OSC, or wrap
            // the mono buffer as a single plane. (Local function, hoisted so
            // both the drizzle branch and the default path below can call it.)
            ushort[][] PlanesOf(ushort[] data) {
                if (pattern == NINA.Core.Enum.BayerPatternEnum.None) return new[] { data };
                var ch = BayerDebayer.Bilinear(data, W, H, pattern);
                return new[] { ch.R, ch.G, ch.B };
            }

            // ---- Drizzle path (scale > 1) ---------------------------------
            // Forward-project each frame's pixels ("drops") onto a scale× finer
            // grid instead of resample + combine. Accumulators live in RAM
            // (drizzle's inherent cost); frames stream one at a time. No
            // per-pixel sigma rejection here (classic drizzle is a weighted
            // mean); use scale=1 for the sigma-clipped path.
            if (drizzleScale > 1) {
                int outWd = W * drizzleScale, outHd = H * drizzleScale;
                long accBytes = (long)nPlanes * outWd * outHd * 2 * sizeof(float);
                var (dOk, dMsg) = StackMemoryGuard.Check(accBytes, AvailableBytes(),
                    $"drizzle-integrate at {drizzleScale}x");
                if (!dOk) {
                    _logger.LogWarning("Drizzle job {JobId} refused: {Msg}", jobId, dMsg);
                    _jobs[jobId] = _jobs[jobId] with { InProgress = false, Stage = "error", Error = dMsg };
                    return;
                }
                _jobs[jobId] = _jobs[jobId] with { Stage = "drizzling", Done = 0, IntegrationPercent = 0 };

                var integ = new DrizzleIntegrator[nPlanes];
                for (int p = 0; p < nPlanes; p++)
                    integ[p] = new DrizzleIntegrator(W, H, drizzleScale, drizzlePixfrac);

                var dzKept = new List<string>(frames.Count);
                double dzExposure = 0; int dzDropped = 0; WcsInfo? dzWcs = null;
                for (int i = 0; i < frames.Count; i++) {
                    AffineTransform? t;
                    if (i == refIdx) { t = AffineTransform.Identity; }
                    else {
                        t = StarMatcher.Match(refStars, frames[i].Stars);
                        if (t == null) {
                            _logger.LogWarning("Drop frame {File}: alignment failed", frames[i].Name);
                            dzDropped++;
                            _jobs[jobId] = _jobs[jobId] with { Done = i + 1, Dropped = dzDropped };
                            continue;
                        }
                    }
                    BaseImageData dimg;
                    using (var fs = File.OpenRead(frames[i].Path)) dimg = FITSReader.Read(fs);
                    if (i == refIdx) dzWcs = dimg.Properties.Wcs;
                    var dplanes = PlanesOf(dimg.Data);
                    for (int p = 0; p < nPlanes; p++) integ[p].AddFrame(dplanes[p], t);
                    dzKept.Add(frames[i].Name);
                    dzExposure += frames[i].Exposure;
                    _jobs[jobId] = _jobs[jobId] with {
                        Done = i + 1, Dropped = dzDropped,
                        IntegrationPercent = (int)((i + 1) * 100L / frames.Count)
                    };
                }
                int dzN = dzKept.Count;
                if (dzN < 2)
                    throw new InvalidOperationException($"Only {dzN} frame(s) survived alignment. Need ≥2.");

                long outPlaneSize = (long)outWd * outHd;
                var dzOut = new ushort[outPlaneSize * nPlanes];
                for (int p = 0; p < nPlanes; p++) {
                    var r = integ[p].Result();
                    Array.Copy(r, 0, dzOut, p * outPlaneSize, r.Length);
                }
                double emptyPct = integ[0].EmptyFraction() * 100;

                _jobs[jobId] = _jobs[jobId] with { Stage = "writing", IntegrationPercent = 100 };
                var rigNameD = _profile.ActiveEquipmentProfile?.Name ?? "Default";
                var outRootD = _profile.Active.ImageOutputDir
                    ?? throw new InvalidOperationException("ImageOutputDir not set.");
                var dirD = Path.Combine(outRootD, Sanitize(rigNameD), "integrated",
                    Sanitize(target), Sanitize(filter));
                Directory.CreateDirectory(dirD);
                var fileNameD =
                    $"master_light_{Sanitize(target)}_{Sanitize(filter)}_drz{drizzleScale}x_x{dzN}_{dzExposure:0}s.fits";
                var outPathD = Path.Combine(dirD, fileNameD);
                int copyD = 1;
                while (File.Exists(outPathD))
                    outPathD = Path.Combine(dirD,
                        Path.GetFileNameWithoutExtension(fileNameD) + $"_{copyD++}.fits");

                var propsD = new ImageProperties {
                    Width = outWd, Height = outHd, BitDepth = bitDepth,
                    Channels = nPlanes,
                    BayerPattern = NINA.Core.Enum.BayerPatternEnum.None,
                    IsBayered = false,
                    Wcs = dzWcs,
                };
                var metaD = new ImageMetaData {
                    CreationTime = DateTime.UtcNow,
                    Camera = new ImageMetaData.CameraInfo(),
                    Telescope = new ImageMetaData.TelescopeInfo(),
                    Observer = new ImageMetaData.ObserverInfo(),
                    Target = new ImageMetaData.TargetInfo { Name = target },
                    Exposure = new ImageMetaData.ExposureInfo {
                        ExposureTime = dzExposure, Filter = filter, ImageType = "MASTERLIGHT"
                    }
                };
                var masterD = new BaseImageData(dzOut, propsD, metaD);
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                var kwD = new List<KeyValuePair<string, string>> {
                    new("NCOMBINE", dzN.ToString()),
                    new("EXPTOTAL", dzExposure.ToString("0.##", ci)),
                    new("INTMETH",  "Drizzle"),
                    new("REJECT",   dzDropped.ToString()),
                    new("DRIZZLE",  "T"),
                    new("DRZSCALE", drizzleScale.ToString()),
                    new("DRZFRAC",  drizzlePixfrac.ToString("0.##", ci)),
                    new("DRZEMPTY", emptyPct.ToString("0.##", ci)),
                    new("STACKREF", dzKept.Count > 0 ? Path.GetFileName(dzKept[0]) : "")
                };
                FITSWriter.Write(masterD, outPathD, customKeywords: kwD);
                _logger.LogInformation(
                    "Drizzle job {Job}: {N}/{Total} frames at {S}x -> {Path} (empty {E:0.0}%)",
                    jobId, dzN, framePaths.Count, drizzleScale, outPathD, emptyPct);
                _ = Task.Run(() => _library.RescanAsync());
                _jobs[jobId] = _jobs[jobId] with {
                    InProgress = false, Stage = "done", OutputPath = outPathD,
                    Combined = dzN, Dropped = dzDropped, TotalExposureSec = dzExposure
                };
                return;
            }

            tempDir = Path.Combine(Path.GetTempPath(), $"polaris_stack_{jobId}");
            Directory.CreateDirectory(tempDir);
            // Reused full-plane byte buffer for the spill writes.
            var spillBytes = new byte[(long)planeSize * 2];

            var spilledPaths = new List<string[]>(frames.Count);   // [keptFrame][plane] -> temp path
            var keptNames = new List<string>(frames.Count);
            double totalExposure = 0;
            int dropped = 0;
            // CCALB-0a: the reference frame's WCS (if plate-solved upstream).
            // Non-reference frames are resampled onto the reference's grid, so
            // the reference's WCS is correct for the integrated master.
            WcsInfo? refWcs = null;

            // (PlanesOf is declared once above so both the drizzle branch and
            // this default path share it.)

            // Spill one frame's aligned plane(s) to temp raw files (host-order
            // ushort, no header — same process reads them back). Returns the
            // per-plane paths.
            void Spill(int keptIndex, ushort[][] planes) {
                var paths = new string[planes.Length];
                for (int p = 0; p < planes.Length; p++) {
                    paths[p] = Path.Combine(tempDir!, $"f{keptIndex}_p{p}.raw");
                    int n = planes[p].Length * 2;
                    Buffer.BlockCopy(planes[p], 0, spillBytes, 0, n);
                    using var fs = File.Create(paths[p]);
                    fs.Write(spillBytes, 0, n);
                }
                spilledPaths.Add(paths);
            }

            for (int i = 0; i < frames.Count; i++) {
                BaseImageData img;
                using (var fs = File.OpenRead(frames[i].Path)) img = FITSReader.Read(fs);

                if (i == refIdx) {
                    // Reference goes in untouched (debayered, not resampled).
                    refWcs = img.Properties.Wcs;
                    Spill(spilledPaths.Count, PlanesOf(img.Data));
                    keptNames.Add(frames[i].Name);
                    totalExposure += frames[i].Exposure;
                } else {
                    var transform = StarMatcher.Match(refStars, frames[i].Stars);
                    if (transform == null) {
                        _logger.LogWarning("Drop frame {File}: alignment failed", frames[i].Name);
                        dropped++;
                    } else {
                        // Pre-resample alignment-quality probe: project every
                        // current-frame star through the transform and find
                        // its nearest reference star. Median residual >2px
                        // means the transform smears the master and ASTAP will
                        // fail to match quads even with healthy star counts.
                        var residual = MedianAlignmentResidualPx(refStars, frames[i].Stars, transform);
                        if (residual > 2.0) {
                            _logger.LogWarning(
                                "Frame {File}: alignment residual median {Residual:F2}px " +
                                "exceeds 2px (transform: M00={M00:F3} M01={M01:F3} " +
                                "M10={M10:F3} M11={M11:F3} Tx={Tx:F1} Ty={Ty:F1}); " +
                                "expect smearing in the integrated master.",
                                frames[i].Name, residual,
                                transform.M00, transform.M01, transform.M10, transform.M11,
                                transform.Tx, transform.Ty);
                        } else {
                            _logger.LogDebug(
                                "Frame {File}: aligned, residual median {Residual:F2}px " +
                                "(Tx={Tx:F1}, Ty={Ty:F1})",
                                frames[i].Name, residual, transform.Tx, transform.Ty);
                        }

                        // Resample EACH colour plane with the same transform
                        // (the transform came from luminance star matching).
                        var srcPlanes = PlanesOf(img.Data);
                        var resampled = new ushort[srcPlanes.Length][];
                        for (int p = 0; p < srcPlanes.Length; p++)
                            resampled[p] = ImageResampler.ApplyTransform(srcPlanes[p], W, H, transform);
                        Spill(spilledPaths.Count, resampled);
                        keptNames.Add(frames[i].Name);
                        totalExposure += frames[i].Exposure;
                    }
                }
                _jobs[jobId] = _jobs[jobId] with { Done = i + 1, Dropped = dropped };
            }

            if (spilledPaths.Count < 2)
                throw new InvalidOperationException(
                    $"Only {spilledPaths.Count} frame(s) survived alignment. Need ≥2.");

            // ---- Phase 3: tiled per-pixel integration from spills -----
            // Stream the spilled aligned planes back in horizontal strips,
            // one plane at a time, and reduce across frames per pixel. Peak
            // RAM is N · tileRows · W (one tile of every frame), not the
            // whole N · W · H · nPlanes the old in-memory path needed.
            _jobs[jobId] = _jobs[jobId] with {
                Stage = "integrating",
                Done = spilledPaths.Count,
                IntegrationPercent = 0,
            };
            int N = spilledPaths.Count;
            var output = new ushort[(long)planeSize * nPlanes];
            int tileRows = (int)Math.Clamp(
                StripBudgetBytes / Math.Max(1, (long)N * W * 4), 1, H);
            var strips = new ushort[N][];
            for (int k = 0; k < N; k++) strips[k] = new ushort[(long)tileRows * W];
            var stripBytes = new byte[(long)tileRows * W * 2];

            int rowsDone = 0;
            int lastReportedPct = 0;
            int totalRows = nPlanes * H;
            for (int pl = 0; pl < nPlanes; pl++) {
                int planeOff = pl * planeSize;
                var fsArr = new FileStream[N];
                try {
                    for (int k = 0; k < N; k++)
                        fsArr[k] = File.OpenRead(spilledPaths[k][pl]);

                    for (int tileStart = 0; tileStart < H; tileStart += tileRows) {
                        int rows = Math.Min(tileRows, H - tileStart);
                        for (int k = 0; k < N; k++)
                            ReadRawStrip(fsArr[k], tileStart, rows, W, stripBytes, strips[k]);

                        int baseOff = tileStart * W;
                        Parallel.For(0, rows, () => new ushort[N], (ly, _, scratch) => {
                            int localOff = ly * W;
                            int outRowOff = planeOff + baseOff + localOff;
                            for (int x = 0; x < W; x++) {
                                int sidx = localOff + x;
                                int valid = 0;
                                // Skip pixels whose value is 0 — ImageResampler
                                // marks off-canvas regions as 0 after the affine
                                // shift, and averaging them in drags the edges.
                                for (int k = 0; k < N; k++) {
                                    var v = strips[k][sidx];
                                    if (v > 0) scratch[valid++] = v;
                                }
                                if (valid == 0) {
                                    output[outRowOff + x] = 0;
                                } else {
                                    var slice = ((ReadOnlySpan<ushort>)scratch)[..valid];
                                    output[outRowOff + x] = method switch {
                                        IntegrationMethod.Mean   => IntegrationMath.Mean(slice),
                                        IntegrationMethod.Median => IntegrationMath.Median(slice),
                                        IntegrationMethod.SigmaClippedMean
                                                                 => IntegrationMath.SigmaClippedMean(slice),
                                        _                        => IntegrationMath.Mean(slice)
                                    };
                                }
                            }
                            var done = System.Threading.Interlocked.Increment(ref rowsDone);
                            int pct = (int)(done * 100L / totalRows);
                            if (pct != lastReportedPct) {
                                lastReportedPct = pct;
                                _jobs[jobId] = _jobs[jobId] with { IntegrationPercent = pct };
                            }
                            return scratch;
                        }, _ => { });
                    }
                } finally {
                    for (int k = 0; k < N; k++) fsArr[k]?.Dispose();
                }
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
                Wcs = refWcs,
            };
            // Flag as MASTERLIGHT and stamp the integration metadata via
            // custom keywords. Build a synthetic exposure that records the
            // *total* time so downstream tools display "X hours".
            var meta = new ImageMetaData {
                CreationTime = DateTime.UtcNow,
                Camera   = new ImageMetaData.CameraInfo(),
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
        } finally {
            // Best-effort cleanup of the spilled aligned-frame temp files.
            if (tempDir != null && Directory.Exists(tempDir)) {
                try { Directory.Delete(tempDir, recursive: true); }
                catch (Exception ex) { _logger.LogDebug(ex, "Temp cleanup failed for {Dir}", tempDir); }
            }
        }
    }

    /// <summary>Read <paramref name="rows"/> rows starting at
    /// <paramref name="startRow"/> from a raw host-order ushort spill file
    /// into <paramref name="dest"/>. <paramref name="byteScratch"/> must be
    /// at least <c>rows * w * 2</c> bytes.</summary>
    private static void ReadRawStrip(FileStream fs, int startRow, int rows, int w,
                                     byte[] byteScratch, ushort[] dest) {
        long byteOffset = (long)startRow * w * 2;
        fs.Seek(byteOffset, SeekOrigin.Begin);
        int n = rows * w * 2;
        int total = 0;
        while (total < n) {
            int r = fs.Read(byteScratch, total, n - total);
            if (r == 0) throw new EndOfStreamException("Spilled frame truncated.");
            total += r;
        }
        Buffer.BlockCopy(byteScratch, 0, dest, 0, n);
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
