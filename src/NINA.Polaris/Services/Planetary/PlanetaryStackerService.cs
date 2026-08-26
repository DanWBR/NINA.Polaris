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
        JobRetention.TrimFinished(_jobs, j => j.StartedAt, j => j.CompletedAt != null);
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
            // PLAN8: 8-bit is the norm in planetary capture, and the reader
            // hands both depths back on the same 16-bit scale, so the rest of
            // this method does not care which it got.
            if (reader.BitDepth is not (8 or 16)) {
                Fail(job, $"Only 8-bit and 16-bit SER are supported (file is {reader.BitDepth}-bit)");
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
            // Per-frame integer shift (dst = src + (dx,dy)) aligning each kept
            // frame to the first. Sub-pixel refinement would need resampling
            // (bilinear / lanczos), deferred to follow-up.
            var shifts = new (int dx, int dy)[picked.Length];
            var refFrame = reader.ReadFrameAsUshort(picked[0]);
            // A target that FILLS the frame — a lunar/solar close-up, all surface
            // with no sky and no limb — has no centroid to track: ~every pixel
            // clears the threshold, the intensity-weighted centre never moves, and
            // the stack gets no alignment (soft/doubled). Switch to phase
            // correlation there; it keys on surface detail (craters, terminator).
            // A bounded disc or a planet on black sky stays on the centroid (its
            // fill fraction is well below the switch-over).
            bool surface = Math.Min(reader.Width, reader.Height) >= 128
                && CentroidAligner.FillFraction(refFrame, reader.Width, reader.Height) >= 0.6;
            if (surface) {
                _logger.LogInformation(
                    "Planetary align: frame-filling target -> phase correlation ({N} frames)", picked.Length);
                var pc = new PhaseCorrelationAligner(refFrame, reader.Width, reader.Height);
                for (int k = 0; k < picked.Length; k++) {
                    ct.ThrowIfCancellationRequested();
                    shifts[k] = k == 0 ? (0, 0) : pc.Align(reader.ReadFrameAsUshort(picked[k]));
                    if (k % 25 == 0) { job.FramesAligned = k + 1; Notify(job); }
                }
            } else {
                var centroids = new CentroidAligner.Centroid[picked.Length];
                for (int k = 0; k < picked.Length; k++) {
                    ct.ThrowIfCancellationRequested();
                    var frame = k == 0 ? refFrame : reader.ReadFrameAsUshort(picked[k]);
                    centroids[k] = CentroidAligner.Find(frame, reader.Width, reader.Height);
                    if (k % 25 == 0) { job.FramesAligned = k + 1; Notify(job); }
                }
                var refC = centroids[0];
                for (int k = 0; k < picked.Length; k++)
                    shifts[k] = ((int)Math.Round(refC.X - centroids[k].X),
                                 (int)Math.Round(refC.Y - centroids[k].Y));
            }

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
                int dx = shifts[k].dx, dy = shifts[k].dy;
                if (cfa) {
                    // Preserve the CFA phase: round to the nearest EVEN pixel so
                    // R/G/B samples don't land on each other's positions.
                    dx = (int)Math.Round(dx / 2.0) * 2;
                    dy = (int)Math.Round(dy / 2.0) * 2;
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
            // A colour camera has to produce a colour stack.
            //
            // This used to write the mean-combined MOSAIC and stamp BAYERPAT so
            // "downstream tools can debayer it". They mostly do not: PixInsight
            // needs an explicit Debayer process, and every tool in this app that
            // renders or sharpens the result treated it as one grey plane. A
            // planetary stack that opens grey out of an ASI585MC is wrong, and
            // worse, sharpening it is wrong ARITHMETIC -- a wavelet transform
            // over a mosaic mixes red and green samples as if they were
            // neighbouring values of one signal.
            //
            // Stacking the mosaic first and debayering once at the end is the
            // right order, and is exactly what the even-pixel shift above
            // exists to make valid: the CFA phase is preserved through the
            // whole stack, so a single debayer at the end sees a clean mosaic
            // with the noise already averaged down.
            var bayer = SerColorToBayer(reader.ColorMode);
            ushort[] pixels = stacked16;
            int channels = 1;
            if (bayer != BayerPatternEnum.None) {
                var ch = NINA.Image.ImageAnalysis.BayerDebayer.Bilinear(
                    stacked16, reader.Width, reader.Height, bayer);
                // FITS colour is PLANAR: the whole R plane, then G, then B.
                int n = reader.Width * reader.Height;
                pixels = new ushort[n * 3];
                Array.Copy(ch.R, 0, pixels, 0, n);
                Array.Copy(ch.G, 0, pixels, n, n);
                Array.Copy(ch.B, 0, pixels, n * 2, n);
                channels = 3;
            }

            var imageData = new BaseImageData(pixels,
                new ImageProperties {
                    Width = reader.Width,
                    Height = reader.Height,
                    BitDepth = 16,
                    Channels = channels,
                    // The result is debayered, so it is NOT a mosaic any more.
                    // Leaving these set would tell the next tool to debayer an
                    // image that already is, which is how one bug becomes two.
                    IsBayered = false,
                    BayerPattern = BayerPatternEnum.None
                },
                new ImageMetaData());
            imageData.MetaData.Camera.Name = reader.Instrument;
            // Deliberately NOT MetaData.Camera.BayerPattern: FITSWriter stamps
            // BAYERPAT from it, and a BAYERPAT on an RGB cube would make
            // PixInsight and Siril offer to debayer it a second time.
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