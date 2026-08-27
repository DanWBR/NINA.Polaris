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
/// Stack N raw calibration frames into a single master via mean, median,
/// or sigma-clipped mean.
///
/// Pipeline per job:
///   1. Load + validate all input FITS files. They must agree on
///      width / height / bit depth (a mismatch usually means the user
///      selected frames from two different rigs by mistake).
///   2. Allocate the output ushort[] (same size as a single frame).
///   3. Walk every pixel coordinate; gather the N values from the
///      loaded frames; reduce via the chosen <see cref="IntegrationMethod"/>;
///      write into the output.
///   4. Build a synthetic <c>BaseImageData</c> with IMAGETYP =
///      MASTER{TYPE}, NSUBS = N, INTMETH = method.
///   5. Write to {rig}/calibration/masters/master_{type}_{key}_{N}.fits.
///   6. Trigger a FrameLibraryService rescan so the new file shows
///      up in the browser.
///
/// Memory model: the integer fast path (BITPIX 8/16/32, the camera-native
/// format of every raw bias/dark/flat) streams the inputs in horizontal
/// strips via <see cref="FitsStripReader"/>, so only one strip of each of
/// the N frames is resident at a time instead of all N full-resolution
/// buffers. A 30 × 24 MP OSC stack drops from ~1.4 GB to ~100 MB, which
/// is what keeps a 4 GB Pi 5 from running into the OOM killer. The tile
/// height is chosen from a fixed RAM budget so the footprint stays flat
/// regardless of frame count or sensor size.
///
/// Float (BITPIX -32/-64) or RGB-cube inputs fall back to the original
/// "load every frame whole" path: the float decode in FITSReader needs a
/// global min/max scan that can't be reproduced strip-by-strip. Those are
/// rare as calibration-frame inputs (they're already-processed masters).
/// </summary>
public class MasterFrameService {
    private readonly FrameLibraryService _library;
    private readonly ProfileService _profile;
    private readonly ILogger<MasterFrameService> _logger;
    // Optional: drives the pre-flight RAM guard. Null in unit tests that
    // construct the service directly — the guard then fails open.
    private readonly HostMetricsService? _metrics;
    private readonly ConcurrentDictionary<string, MasterProgress> _jobs = new();

    public MasterFrameService(FrameLibraryService library, ProfileService profile,
                              ILogger<MasterFrameService> logger,
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

    /// <summary>Kick off integration in the background. Returns the
    /// job id the UI polls on /api/studio/masters/{id}/status.
    /// <para>UNIF-3a: caller now passes absolute FITS paths (no
    /// dependency on the FrameLibrary SQLite index). Files don't
    /// need to be indexed before stacking, a fresh capture can be
    /// combined immediately. Metadata (filter, gain, exposure) is
    /// read from each frame's FITS header on load.</para></summary>
    public string StartJob(IReadOnlyList<string> framePaths, MasterType type, IntegrationMethod method,
                           string? outputDir = null) {
        var jobId = Guid.NewGuid().ToString("N")[..8];
        var progress = new MasterProgress {
            JobId = jobId,
            InProgress = true,
            Total = framePaths.Count,
            Stage = "queued"
        };
        _jobs[jobId] = progress;
        _ = Task.Run(() => RunJob(jobId, framePaths, type, method, outputDir));
        return jobId;
    }

    public MasterProgress? GetStatus(string jobId)
        => _jobs.TryGetValue(jobId, out var p) ? p : null;

    // Strip-tiling RAM budget. The integer fast path keeps roughly this
    // many bytes of decoded + raw strip data resident across all N frames
    // at once (output buffer + per-partition scratch are extra but small).
    // ~96 MB leaves comfortable headroom on a 2 GB SBC while still giving
    // a deep enough tile that the per-tile seek overhead is negligible.
    private const long StripBudgetBytes = 96L * 1024 * 1024;

    private void RunJob(string jobId, IReadOnlyList<string> framePaths, MasterType type, IntegrationMethod method,
                        string? outputDir) {
        try {
            for (int i = 0; i < framePaths.Count; i++) {
                var path = framePaths[i];
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    throw new InvalidOperationException($"Frame missing on disk: {path}");
            }

            int W, H, bitDepth, gain, N;
            double sumExposure;
            string filter;
            ushort[] output;
            ImageProperties firstProps;
            ImageMetaData firstMeta;

            // Probe the first frame to decide which path to take. Integer
            // 2-D frames (every camera-native bias/dark/flat) stream in
            // strips; float / RGB-cube inputs fall back to whole-frame
            // loading where the FITSReader global rescale still applies.
            bool stripable;
            int probeW, probeH;
            using (var probe = FitsStripReader.Open(framePaths[0])) {
                stripable = probe.IsStripable;
                probeW = probe.Width;
                probeH = probe.Height;
            }

            // Pre-flight RAM guard. The tiled path is light; the float/RGB
            // fallback loads every frame whole, so estimate that worst case.
            long needBytes = stripable
                ? StackMemoryGuard.EstimateMasterBytes(probeW, probeH, StripBudgetBytes)
                : (long)framePaths.Count * probeW * probeH * 2 + (long)probeW * probeH * 2;
            var (memOk, memMsg) = StackMemoryGuard.Check(
                needBytes, AvailableBytes(), $"build the {type.ToString().ToLowerInvariant()} master");
            if (!memOk) {
                _logger.LogWarning("Master frame job {JobId} refused: {Msg}", jobId, memMsg);
                _jobs[jobId] = _jobs[jobId] with {
                    InProgress = false, Stage = "error", Error = memMsg
                };
                return;
            }

            if (stripable) {
                IntegrateTiled(jobId, framePaths, method,
                    out W, out H, out bitDepth, out gain, out filter, out sumExposure,
                    out N, out output, out firstProps, out firstMeta);
            } else {
                IntegrateFullLoad(jobId, framePaths, method,
                    out W, out H, out bitDepth, out gain, out filter, out sumExposure,
                    out N, out output, out firstProps, out firstMeta);
            }

            // ---- Phase 3: write master FITS -------------------------
            _jobs[jobId] = _jobs[jobId] with { Stage = "writing" };

            // Working-folder override (WBPP orchestrator) drops masters straight
            // under {outputDir}/masters; otherwise the per-rig calibration library.
            string dir;
            if (!string.IsNullOrWhiteSpace(outputDir)) {
                dir = Path.Combine(outputDir!, "masters");
            } else {
                var rigName = _profile.ActiveEquipmentProfile?.Name ?? "Default";
                var outRoot = _profile.Active.ImageOutputDir;
                if (string.IsNullOrWhiteSpace(outRoot))
                    throw new InvalidOperationException("ImageOutputDir not set.");
                dir = Path.Combine(outRoot, Sanitize(rigName), "calibration", "masters");
            }
            Directory.CreateDirectory(dir);

            var key = type switch {
                MasterType.Bias     => $"g{gain}",
                MasterType.Dark     => $"{sumExposure / N:0.##}s_g{gain}",
                MasterType.DarkFlat => $"{sumExposure / N:0.##}s_g{gain}",
                MasterType.Flat     => $"{(string.IsNullOrEmpty(filter) ? "L" : filter)}_g{gain}",
                _                   => "master"
            };
            var fileName = $"master_{type.ToString().ToLowerInvariant()}_{key}_x{N}.fits";
            foreach (var c in Path.GetInvalidFileNameChars()) fileName = fileName.Replace(c, '_');
            var outPath = Path.Combine(dir, fileName);
            int copy = 1;
            while (File.Exists(outPath))
                outPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(fileName) + $"_{copy++}.fits");

            var props = new ImageProperties {
                Width = W, Height = H, BitDepth = bitDepth,
                BayerPattern = firstProps.BayerPattern,
                IsBayered = firstProps.IsBayered
            };
            // Carry over key headers from the first input so the master
            // looks "real" to PixInsight / Siril (same camera, gain,
            // average exposure per sub).
            var meta = new ImageMetaData {
                CreationTime = DateTime.UtcNow,
                Camera   = firstMeta.Camera,
                Telescope = firstMeta.Telescope,
                Observer = firstMeta.Observer,
                Target   = firstMeta.Target,
                Exposure = new ImageMetaData.ExposureInfo {
                    ExposureTime = sumExposure / N,
                    Filter       = filter,
                    ImageType    = MasterImageType(type)
                }
            };
            var masterData = new BaseImageData(output, props, meta);

            var customKeywords = new List<KeyValuePair<string, string>> {
                new("NSUBS",   N.ToString()),
                new("INTMETH", method.ToString())
            };
            FITSWriter.Write(masterData, outPath, customKeywords: customKeywords);

            _logger.LogInformation("Master {Type} written: {Path} (n={N}, method={Method})",
                type, outPath, N, method);

            // Drop the master into the library cache so it shows up in
            // the browser immediately. Best-effort, if the index walk
            // is busy the next user-triggered rescan will pick it up.
            _ = Task.Run(() => _library.RescanAsync());

            _jobs[jobId] = _jobs[jobId] with {
                InProgress = false,
                Stage = "done",
                OutputPath = outPath
            };
        } catch (Exception ex) {
            _logger.LogError(ex, "Master frame job {JobId} failed", jobId);
            _jobs[jobId] = _jobs[jobId] with {
                InProgress = false,
                Stage = "error",
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// Integer fast path: stream the inputs in horizontal strips so only
    /// one tile of each of the N frames is in memory at a time. Reduces
    /// peak RAM from O(N · W · H) to O(N · tileRows · W), which is what
    /// keeps deep OSC stacks off the OOM killer on a 4 GB SBC.
    /// </summary>
    private void IntegrateTiled(string jobId, IReadOnlyList<string> framePaths,
            IntegrationMethod method,
            out int W, out int H, out int bitDepth, out int gain, out string filter,
            out double sumExposure, out int N, out ushort[] output,
            out ImageProperties firstProps, out ImageMetaData firstMeta) {

        _jobs[jobId] = _jobs[jobId] with { Stage = "loading", Done = 0 };
        int n = framePaths.Count;
        var readers = new FitsStripReader[n];
        try {
            for (int i = 0; i < n; i++) {
                readers[i] = FitsStripReader.Open(framePaths[i]);
                if (!readers[i].IsStripable) {
                    // Mixed integer + float/RGB set: bail to the whole-frame
                    // path which handles every format uniformly.
                    for (int k = 0; k <= i; k++) readers[k]?.Dispose();
                    IntegrateFullLoad(jobId, framePaths, method,
                        out W, out H, out bitDepth, out gain, out filter, out sumExposure,
                        out N, out output, out firstProps, out firstMeta);
                    return;
                }
                if (i > 0 && (readers[i].Width != readers[0].Width || readers[i].Height != readers[0].Height)) {
                    throw new InvalidOperationException(
                        $"Frame {Path.GetFileName(framePaths[i])} is {readers[i].Width}×{readers[i].Height}, " +
                        $"expected {readers[0].Width}×{readers[0].Height}. Frames must agree.");
                }
                _jobs[jobId] = _jobs[jobId] with { Done = i + 1 };
            }

            // Locals (the lambda below can't close over out parameters).
            int w = readers[0].Width;
            int h = readers[0].Height;
            double exp = 0;
            for (int k = 0; k < n; k++) exp += readers[k].MetaData.Exposure.ExposureTime;
            var outBuf = new ushort[(long)w * h];

            // Tile height from the RAM budget: each tile holds N decoded
            // strips (ushort) plus each reader's raw byte scratch. Clamp to
            // at least one row and at most the full image.
            int tileRows = (int)Math.Clamp(
                StripBudgetBytes / Math.Max(1, (long)n * w * 4),
                1, h);

            // Per-frame decoded strip buffers, reused across tiles.
            var strips = new ushort[n][];
            for (int k = 0; k < n; k++) strips[k] = new ushort[(long)tileRows * w];

            _jobs[jobId] = _jobs[jobId] with {
                Stage = "integrating", Done = n, IntegrationPercent = 0,
            };

            int rowsDone = 0;
            int lastReportedPct = 0;
            for (int tileStart = 0; tileStart < h; tileStart += tileRows) {
                int rows = Math.Min(tileRows, h - tileStart);
                // Load this tile from every frame (sequential per-file
                // seek + read; the per-pixel reduce below is the parallel
                // part).
                for (int k = 0; k < n; k++) readers[k].ReadRows(tileStart, rows, strips[k]);

                int baseOff = tileStart * w;
                Parallel.For(0, rows, () => new ushort[n], (ly, _, scratch) => {
                    int localOff = ly * w;
                    int outOff = baseOff + localOff;
                    for (int x = 0; x < w; x++) {
                        int sidx = localOff + x;
                        for (int k = 0; k < n; k++) scratch[k] = strips[k][sidx];
                        outBuf[outOff + x] = method switch {
                            IntegrationMethod.Mean   => IntegrationMath.Mean(scratch),
                            IntegrationMethod.Median => IntegrationMath.Median(scratch),
                            IntegrationMethod.SigmaClippedMean
                                                     => IntegrationMath.SigmaClippedMean(scratch),
                            _                        => IntegrationMath.Mean(scratch)
                        };
                    }
                    var done = System.Threading.Interlocked.Increment(ref rowsDone);
                    int pct = (int)(done * 100L / h);
                    if (pct != lastReportedPct) {
                        lastReportedPct = pct;
                        _jobs[jobId] = _jobs[jobId] with { IntegrationPercent = pct };
                    }
                    return scratch;
                }, _ => { });
            }
            _jobs[jobId] = _jobs[jobId] with { IntegrationPercent = 100 };

            // Publish results.
            W = w;
            H = h;
            N = n;
            output = outBuf;
            bitDepth = readers[0].Properties.BitDepth;
            gain = readers[0].MetaData.Camera.Gain;
            filter = readers[0].MetaData.Exposure.Filter ?? "";
            sumExposure = exp;
            firstProps = readers[0].Properties;
            firstMeta = readers[0].MetaData;
        } finally {
            for (int k = 0; k < n; k++) readers[k]?.Dispose();
        }
    }

    /// <summary>
    /// Legacy whole-frame path: loads every input buffer at once. Used for
    /// float (BITPIX &lt; 0) or RGB-cube inputs, whose FITSReader decode
    /// needs a global pass the strip reader can't reproduce. Memory is
    /// O(N · W · H); acceptable because these inputs are rare (processed
    /// masters re-stacked) rather than the common deep raw-frame case.
    /// </summary>
    private void IntegrateFullLoad(string jobId, IReadOnlyList<string> framePaths,
            IntegrationMethod method,
            out int W, out int H, out int bitDepth, out int gain, out string filter,
            out double sumExposure, out int N, out ushort[] output,
            out ImageProperties firstProps, out ImageMetaData firstMeta) {

        _jobs[jobId] = _jobs[jobId] with { Stage = "loading", Done = 0 };
        var loaded = new List<BaseImageData>(framePaths.Count);
        int? width = null, height = null, depth = null;
        double exp = 0;
        int localGain = 0;
        string localFilter = "";

        for (int i = 0; i < framePaths.Count; i++) {
            using var fs = File.OpenRead(framePaths[i]);
            var img = FITSReader.Read(fs);
            if (width == null) {
                width = img.Properties.Width;
                height = img.Properties.Height;
                depth = img.Properties.BitDepth;
                localGain = img.MetaData.Camera.Gain;
                localFilter = img.MetaData.Exposure.Filter ?? "";
            } else if (img.Properties.Width != width || img.Properties.Height != height) {
                throw new InvalidOperationException(
                    $"Frame {Path.GetFileName(framePaths[i])} is {img.Properties.Width}×{img.Properties.Height}, " +
                    $"expected {width}×{height}. Frames must agree.");
            }
            exp += img.MetaData.Exposure.ExposureTime;
            loaded.Add(img);
            _jobs[jobId] = _jobs[jobId] with { Done = i + 1 };
        }

        // Locals (the lambda below can't close over out parameters).
        int w = width!.Value;
        int h = height!.Value;
        int n = loaded.Count;
        var outBuf = new ushort[(long)w * h];

        var stacks = new ushort[n][];
        for (int k = 0; k < n; k++) stacks[k] = loaded[k].Data;

        _jobs[jobId] = _jobs[jobId] with {
            Stage = "integrating", Done = n, IntegrationPercent = 0,
        };

        int rowsDone = 0;
        int lastReportedPct = 0;
        Parallel.For(0, h, () => new ushort[n], (y, _, scratch) => {
            int rowOff = y * w;
            for (int x = 0; x < w; x++) {
                int idx = rowOff + x;
                for (int k = 0; k < n; k++) scratch[k] = stacks[k][idx];
                outBuf[idx] = method switch {
                    IntegrationMethod.Mean   => IntegrationMath.Mean(scratch),
                    IntegrationMethod.Median => IntegrationMath.Median(scratch),
                    IntegrationMethod.SigmaClippedMean
                                             => IntegrationMath.SigmaClippedMean(scratch),
                    _                        => IntegrationMath.Mean(scratch)
                };
            }
            var done = System.Threading.Interlocked.Increment(ref rowsDone);
            int pct = (int)(done * 100L / h);
            if (pct != lastReportedPct) {
                lastReportedPct = pct;
                _jobs[jobId] = _jobs[jobId] with { IntegrationPercent = pct };
            }
            return scratch;
        }, _ => { });
        _jobs[jobId] = _jobs[jobId] with { IntegrationPercent = 100 };

        // Publish results.
        W = w;
        H = h;
        N = n;
        output = outBuf;
        bitDepth = depth!.Value;
        gain = localGain;
        filter = localFilter;
        sumExposure = exp;
        firstProps = loaded[0].Properties;
        firstMeta = loaded[0].MetaData;
    }

    private static string MasterImageType(MasterType type) => type switch {
        MasterType.Bias     => "MASTERBIAS",
        MasterType.Dark     => "MASTERDARK",
        MasterType.Flat     => "MASTERFLAT",
        MasterType.DarkFlat => "MASTERDARKFLAT",
        _                   => "MASTER"
    };

    private static string Sanitize(string s) {
        if (string.IsNullOrWhiteSpace(s)) return "Unknown";
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Replace(' ', '_');
    }
}

public enum MasterType { Bias, Dark, Flat, DarkFlat }
public enum IntegrationMethod { Mean, Median, SigmaClippedMean }

public record MasterProgress {
    public string JobId { get; init; } = "";
    public bool InProgress { get; init; }

    // Done / Total count inputs the job is processing; Total is
    // pinned to the input frame count for the whole job so the UI's
    // "done / total" stays meaningful across loading + integrating
    // + writing. Sub-phase progress goes on IntegrationPercent
    // instead of clobbering Total with image-height (the previous
    // implementation did this and "done / total" briefly read as
    // "row 1842 / 3672").
    public int Done { get; init; }
    public int Total { get; init; }

    // 0..100 progress through the integration phase's per-row sweep.
    // Reads 0 outside the integrating stage, 100 once the rows are
    // exhausted (and stays there through writing/done).
    public int IntegrationPercent { get; init; }

    public string Stage { get; init; } = "";          // queued | loading | integrating | writing | done | error
    public string? Error { get; init; }
    public string? OutputPath { get; init; }
}