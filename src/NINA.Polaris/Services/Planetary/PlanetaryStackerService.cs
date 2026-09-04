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
            // PLANETSTACK-2: each frame is debayered, and its centroid and
            // sharpness are measured on the LUMINANCE in a window around the
            // planet. On the mosaic the Laplacian ranked the Bayer
            // checkerboard, not the seeing, and the centroid drifted with it.
            SetPhase(job, StackPhase.Analyzing);
            var bayer = SerColorToBayer(reader.ColorMode);
            var qualities = new double[reader.FrameCount];
            var cxs = new double[reader.FrameCount];
            var cys = new double[reader.FrameCount];
            int window = Math.Clamp(Math.Min(reader.Width, reader.Height) / 3, 32, 256);
            int analysed = 0;
            for (int i = 0; i < reader.FrameCount; i++) {
                ct.ThrowIfCancellationRequested();
                var planes = PlanetaryFrames.Split(reader.ReadFrameAsUshort(i), reader.Width, reader.Height, bayer);
                // PSS lesson: rank and centroid on the Gaussian-blurred
                // luminance, so noise is not mistaken for structure.
                var lumB = PlanetaryFrames.Blur7(planes.Lum, reader.Width, reader.Height);
                var (cx, cy, _) = PlanetaryFrames.Centroid(lumB, reader.Width, reader.Height);
                cxs[i] = cx; cys[i] = cy;
                qualities[i] = PlanetaryFrames.Sharpness(lumB, reader.Width, reader.Height, cx, cy, window);
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
            // Reference = the sharpest frame; every picked frame is moved so its
            // luminance centroid lands on the reference's, with sub-pixel
            // precision. A frame-filling target (Moon/Sun surface) has no
            // centroid to speak of: keep phase correlation on its luminance.
            var refPlanes = PlanetaryFrames.Split(reader.ReadFrameAsUshort(picked[0]), reader.Width, reader.Height, bayer);
            bool surface = Math.Min(reader.Width, reader.Height) >= 128
                && CentroidAligner.FillFraction(ToUshort(refPlanes.Lum), reader.Width, reader.Height) >= 0.6;
            var shifts = new (double dx, double dy)[picked.Length];
            if (surface) {
                _logger.LogInformation(
                    "Planetary align: frame-filling target -> phase correlation ({N} frames)", picked.Length);
                var pc = new PhaseCorrelationAligner(ToUshort(refPlanes.Lum), reader.Width, reader.Height);
                for (int k = 0; k < picked.Length; k++) {
                    ct.ThrowIfCancellationRequested();
                    if (k == 0) { shifts[k] = (0, 0); continue; }
                    var lum = PlanetaryFrames.Split(reader.ReadFrameAsUshort(picked[k]), reader.Width, reader.Height, bayer).Lum;
                    var (dx, dy) = pc.Align(ToUshort(lum));
                    shifts[k] = (dx, dy);
                    if (k % 25 == 0) { job.FramesAligned = k + 1; Notify(job); }
                }
            } else {
                double rx = cxs[picked[0]], ry = cys[picked[0]];
                for (int k = 0; k < picked.Length; k++)
                    shifts[k] = (rx - cxs[picked[k]], ry - cys[picked[k]]);
                job.FramesAligned = picked.Length;
                Notify(job);
            }

            SetPhase(job, StackPhase.Stacking);
            int npx = reader.Width * reader.Height;
            // PSS lesson (frames_normalization): scale every frame's object
            // brightness to the reference's before it is added, so seeing
            // transparency and thin cloud do not modulate the stack.
            var (refBg, refPeak) = PlanetaryFrames.Levels(refPlanes.Lum);
            float normThreshold = (float)(refBg + 0.25 * (refPeak - refBg));
            double refMean = PlanetaryFrames.MeanAbove(refPlanes.Lum, normThreshold);
            bool cfa = bayer != BayerPatternEnum.None;
            var accR = new float[npx]; var accG = cfa ? new float[npx] : accR; var accB = cfa ? new float[npx] : accR;
            var wgt = new float[npx];
            int stacked = 0;
            for (int k = 0; k < picked.Length; k++) {
                ct.ThrowIfCancellationRequested();
                var planes = k == 0 ? refPlanes
                    : PlanetaryFrames.Split(reader.ReadFrameAsUshort(picked[k]), reader.Width, reader.Height, bayer);
                var (dx, dy) = shifts[k];
                float gain = k == 0 ? 1f
                    : PlanetaryFrames.NormalisationGain(refMean, PlanetaryFrames.MeanAbove(planes.Lum, normThreshold));
                if (cfa) {
                    PlanetaryFrames.AccumulateShifted(planes.R, reader.Width, reader.Height, dx, dy, accR, wgt, gain);
                    var w2 = new float[npx];   // same coverage for every plane; count once
                    PlanetaryFrames.AccumulateShifted(planes.G, reader.Width, reader.Height, dx, dy, accG, w2, gain);
                    PlanetaryFrames.AccumulateShifted(planes.B, reader.Width, reader.Height, dx, dy, accB, w2, gain);
                } else {
                    PlanetaryFrames.AccumulateShifted(planes.Lum, reader.Width, reader.Height, dx, dy, accR, wgt, gain);
                }
                stacked++;
                if (stacked % 25 == 0 || stacked == picked.Length) {
                    job.FramesStacked = stacked;
                    Notify(job);
                }
            }

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
            ushort[] pixels;
            int channels;
            if (cfa) {
                var r = PlanetaryFrames.Finish(accR, wgt);
                var g = PlanetaryFrames.Finish(accG, wgt);
                var b = PlanetaryFrames.Finish(accB, wgt);
                pixels = new ushort[npx * 3];
                Array.Copy(r, 0, pixels, 0, npx);
                Array.Copy(g, 0, pixels, npx, npx);
                Array.Copy(b, 0, pixels, npx * 2, npx);
                channels = 3;
            } else {
                pixels = PlanetaryFrames.Finish(accR, wgt);
                channels = 1;
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

    private static ushort[] ToUshort(float[] plane) {
        var o = new ushort[plane.Length];
        for (int i = 0; i < o.Length; i++) o[i] = (ushort)Math.Clamp(Math.Round(plane[i]), 0, 65535);
        return o;
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