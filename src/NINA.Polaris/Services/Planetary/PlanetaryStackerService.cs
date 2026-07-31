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
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageData;

namespace NINA.Polaris.Services.Planetary;

/// <summary>
/// Lucky-imaging pipeline for planetary SER files:
///   Read → Analyze (Laplacian variance per frame) → Rank → Pick top X%
///   → Align (brightest-region centroid) → Stack (mean) → Write FITS
///
/// Async job model mirrors PHD2CalibrationOrchestrator: caller fires
/// StartJob and polls / observes /ws/status for progress. Cancellable
/// via Abort.
/// </summary>
public class PlanetaryStackerService {
    private readonly ProfileService _profiles;
    private readonly ILogger<PlanetaryStackerService> _logger;
    private readonly ConcurrentDictionary<string, StackJob> _jobs = new();

    public StackJob? CurrentJob { get; private set; }
    public event Action<StackJob>? JobUpdated;

    public PlanetaryStackerService(ProfileService profiles,
                                   ILogger<PlanetaryStackerService> logger) {
        _profiles = profiles;
        _logger = logger;
    }

    public StackJob StartJob(StackConfig cfg) {
        var job = new StackJob {
            Id = Guid.NewGuid().ToString("N"),
            Config = cfg,
            Phase = StackPhase.Reading,
            StartedAt = DateTime.UtcNow
        };
        _jobs[job.Id] = job;
        CurrentJob = job;
        job.Cts = new CancellationTokenSource();
        job.Task = Task.Run(() => RunAsync(job, job.Cts.Token));
        return job;
    }

    public StackJob? GetJob(string id) => _jobs.TryGetValue(id, out var j) ? j : null;
    public void Abort(string id) { if (_jobs.TryGetValue(id, out var j)) j.Cts?.Cancel(); }

    private async Task RunAsync(StackJob job, CancellationToken ct) {
        try {
            // Phase 1: Reading -------------------------------------------------
            SetPhase(job, StackPhase.Reading);
            if (!File.Exists(job.Config.SerPath)) {
                Fail(job, $"SER file not found: {job.Config.SerPath}");
                return;
            }
            using var reader = new SerFileReader(job.Config.SerPath);
            if (reader.BitDepth != 16) {
                Fail(job, $"Only 16-bit SER supported for now (file is {reader.BitDepth}-bit)");
                return;
            }
            if (reader.ColorMode is not (SerColorMode.Mono or SerColorMode.BayerRGGB
                or SerColorMode.BayerGRBG or SerColorMode.BayerGBRG or SerColorMode.BayerBGGR)) {
                // RGB / BGR planet videos exist but the per-channel stacking
                // path isn't built yet, fail clearly instead of producing
                // garbage.
                Fail(job, $"Color mode {reader.ColorMode} not yet supported (mono / Bayer only)");
                return;
            }
            // FIELD8-3: a recording with nothing in it used to reach
            // `centroids[0]` with an empty array and die on an
            // IndexOutOfRangeException, which tells the operator nothing about
            // what is wrong with their file.
            if (reader.FrameCount <= 0) {
                Fail(job, "This recording holds no frames (the file has only a header). "
                        + "Record again; if it kept happening, check free disk space.");
                return;
            }
            if (reader.RecoveredFrameCount) {
                _logger.LogWarning(
                    "SER {Path} had no frame count in its header; recovered {N} frames from the "
                    + "file length (the recording was interrupted before it was closed)",
                    job.Config.SerPath, reader.FrameCount);
            } else if (reader.TruncatedFrameCount > 0) {
                _logger.LogWarning(
                    "SER {Path} claims {Claimed} frames but only {Actual} are present; "
                    + "stacking what is there", job.Config.SerPath,
                    reader.TruncatedFrameCount, reader.FrameCount);
            }
            job.TotalFrames = reader.FrameCount;
            job.Width = reader.Width;
            job.Height = reader.Height;

            // Phase 2: Analyze -------------------------------------------------
            SetPhase(job, StackPhase.Analyzing);
            var qualities = new double[reader.FrameCount];
            int analysed = 0;
            // Sequential read (random access via FileStream isn't
            // thread-safe), parallel compute.
            for (int i = 0; i < reader.FrameCount; i++) {
                ct.ThrowIfCancellationRequested();
                var frame = reader.ReadFrameAsUshort(i);
                qualities[i] = FrameQualityAnalyzer.LaplacianVariance(frame, reader.Width, reader.Height,
                    roiSize: Math.Min(reader.Width, reader.Height) / 2);
                analysed++;
                if (analysed % 25 == 0 || analysed == reader.FrameCount) {
                    job.FramesAnalyzed = analysed;
                    Notify(job);
                }
            }

            // Phase 3: Rank ----------------------------------------------------
            SetPhase(job, StackPhase.Ranking);
            var ranked = Enumerable.Range(0, reader.FrameCount)
                .OrderByDescending(i => qualities[i])
                .ToArray();
            int keep = Math.Max(1, (int)Math.Round(reader.FrameCount * (job.Config.KeepPercent / 100.0)));
            keep = Math.Min(keep, reader.FrameCount);
            var picked = ranked.Take(keep).ToArray();
            job.FramesPicked = picked.Length;
            job.QualityScores = qualities;
            Notify(job);

            // Phase 4: Align ---------------------------------------------------
            SetPhase(job, StackPhase.Aligning);
            var centroids = new CentroidAligner.Centroid[picked.Length];
            for (int k = 0; k < picked.Length; k++) {
                ct.ThrowIfCancellationRequested();
                var frame = reader.ReadFrameAsUshort(picked[k]);
                centroids[k] = CentroidAligner.Find(frame, reader.Width, reader.Height);
                if (k % 25 == 0) { job.FramesAligned = k + 1; Notify(job); }
            }
            // Reference centroid = first frame's. Compute integer offsets
            // for each kept frame so we can do nearest-neighbour shift
            // during stack. Sub-pixel refinement would require resampling
            // (bilinear / lanczos), deferred to follow-up.
            var refC = centroids[0];

            // Phase 5: Stack ---------------------------------------------------
            SetPhase(job, StackPhase.Stacking);
            // Accumulator: uint accumulator + count per pixel so we can mean
            // at the end. For up to 65535 frames of uint16 this fits in uint32.
            var accum = new uint[reader.Width * reader.Height];
            var counts = new ushort[reader.Width * reader.Height];
            int stacked = 0;
            // Bayer SERs are stacked as the raw CFA mosaic (no debayer), so the
            // per-frame shift MUST preserve the CFA phase: an odd dx/dy lands R
            // pixels on G positions and the mean of mis-phased mosaics scrambles
            // the colour after debayer. Round Bayer offsets to the nearest EVEN
            // pixel (≤1 px extra alignment error, invisible next to seeing).
            bool cfa = reader.ColorMode != SerColorMode.Mono;
            for (int k = 0; k < picked.Length; k++) {
                ct.ThrowIfCancellationRequested();
                var frame = reader.ReadFrameAsUshort(picked[k]);
                int dx, dy;
                if (cfa) {
                    dx = (int)Math.Round((refC.X - centroids[k].X) / 2.0) * 2;
                    dy = (int)Math.Round((refC.Y - centroids[k].Y) / 2.0) * 2;
                } else {
                    dx = (int)Math.Round(refC.X - centroids[k].X);
                    dy = (int)Math.Round(refC.Y - centroids[k].Y);
                }
                for (int y = 0; y < reader.Height; y++) {
                    int sy = y - dy;
                    if (sy < 0 || sy >= reader.Height) continue;
                    int dstRow = y * reader.Width;
                    int srcRow = sy * reader.Width;
                    for (int x = 0; x < reader.Width; x++) {
                        int sx = x - dx;
                        if (sx < 0 || sx >= reader.Width) continue;
                        accum[dstRow + x] += frame[srcRow + sx];
                        counts[dstRow + x]++;
                    }
                }
                stacked++;
                if (stacked % 25 == 0 || stacked == picked.Length) {
                    job.FramesStacked = stacked;
                    Notify(job);
                }
            }
            var stacked16 = new ushort[reader.Width * reader.Height];
            for (int i = 0; i < accum.Length; i++) {
                stacked16[i] = counts[i] == 0 ? (ushort)0
                    : (ushort)Math.Min(ushort.MaxValue, accum[i] / counts[i]);
            }

            // Phase 6: Write ---------------------------------------------------
            SetPhase(job, StackPhase.Writing);
            Directory.CreateDirectory(job.Config.OutputDir);
            var outName = $"{job.Config.OutputName}_{DateTime.UtcNow:yyyy-MM-ddTHH-mm-ss}.fits";
            var outPath = Path.Combine(job.Config.OutputDir, outName);
            // Carry the SER's Bayer mosaic into the stacked FITS. The stack is
            // still a raw CFA frame (we mean-combine the mosaic, no debayer), so
            // downstream tools must know the pattern to debayer it — otherwise it
            // opens as mono. The FITS writer stamps BAYERPAT from
            // MetaData.Camera.BayerPattern, so set BOTH that and props.
            var bayer = SerColorToBayer(reader.ColorMode);
            var imageData = new BaseImageData(stacked16,
                new ImageProperties {
                    Width = reader.Width,
                    Height = reader.Height,
                    BitDepth = 16,
                    IsBayered = bayer != BayerPatternEnum.None,
                    BayerPattern = bayer
                },
                new ImageMetaData());
            imageData.MetaData.Camera.Name = reader.Instrument;
            imageData.MetaData.Camera.BayerPattern = bayer;
            imageData.MetaData.Telescope.Name = reader.Telescope;
            // FITSWriter is sync; offload to thread pool so the cancellation
            // token still flows through the surrounding loop.
            await Task.Run(() => FITSWriter.Write(imageData, outPath), ct);
            job.OutputPath = outPath;

            // Phase 7: Done ----------------------------------------------------
            SetPhase(job, StackPhase.Ok);
            job.CompletedAt = DateTime.UtcNow;
            _logger.LogInformation(
                "Planetary stack OK: {N}/{Total} frames → {Path}",
                picked.Length, reader.FrameCount, outPath);
            Notify(job);

        } catch (OperationCanceledException) { Fail(job, "Cancelled"); }
          catch (Exception ex) { _logger.LogError(ex, "Planetary stack failed"); Fail(job, ex.Message); }
    }

    private void SetPhase(StackJob job, StackPhase p) {
        job.Phase = p;
        Notify(job);
    }

    private void Fail(StackJob job, string error) {
        job.Error = error;
        job.Phase = StackPhase.Fail;
        job.CompletedAt = DateTime.UtcNow;
        _logger.LogWarning("Stack failed: {Error}", error);
        Notify(job);
    }

    private void Notify(StackJob job) {
        try { JobUpdated?.Invoke(job); } catch { }
    }

    private static BayerPatternEnum SerColorToBayer(SerColorMode m) => m switch {
        SerColorMode.BayerRGGB => BayerPatternEnum.RGGB,
        SerColorMode.BayerGRBG => BayerPatternEnum.GRBG,
        SerColorMode.BayerGBRG => BayerPatternEnum.GBRG,
        SerColorMode.BayerBGGR => BayerPatternEnum.BGGR,
        _ => BayerPatternEnum.None
    };
}

public record StackConfig(
    string SerPath,
    string OutputDir,
    double KeepPercent = 50,
    string OutputName = "stack");

public class StackJob {
    public string Id { get; set; } = "";
    public StackConfig Config { get; set; } = new("", "");
    public StackPhase Phase { get; set; }
    public int TotalFrames { get; set; }
    public int FramesAnalyzed { get; set; }
    public int FramesPicked { get; set; }
    public int FramesAligned { get; set; }
    public int FramesStacked { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double[]? QualityScores { get; set; }
    public string? OutputPath { get; set; }
    public string? Error { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    internal Task? Task { get; set; }
    internal CancellationTokenSource? Cts { get; set; }
}

public enum StackPhase {
    Reading, Analyzing, Ranking, Aligning, Stacking, Writing, Ok, Fail
}