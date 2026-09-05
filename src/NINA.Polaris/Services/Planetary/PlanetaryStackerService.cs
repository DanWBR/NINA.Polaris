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
            // Global shift of EVERY frame onto the best frame: centroid offsets
            // for a bounded planet, phase correlation on the blurred luminance
            // for a frame-filling surface (PLANETAP-SURFACE: computed for all
            // frames, not only the picked ones, so the alignment-point mesh can
            // rank and register locally on the Moon and the Sun too).
            var gdxs = new double[reader.FrameCount];
            var gdys = new double[reader.FrameCount];
            if (surface) {
                _logger.LogInformation(
                    "Planetary align: frame-filling target -> phase correlation ({N} frames)", reader.FrameCount);
                var refB = ToUshort(PlanetaryFrames.Blur7(refPlanes.Lum, reader.Width, reader.Height));
                var pc = new PhaseCorrelationAligner(refB, reader.Width, reader.Height);
                for (int i = 0; i < reader.FrameCount; i++) {
                    ct.ThrowIfCancellationRequested();
                    if (i == picked[0]) continue;
                    var lum = PlanetaryFrames.Split(reader.ReadFrameAsUshort(i), reader.Width, reader.Height, bayer).Lum;
                    var (dx, dy) = pc.Align(ToUshort(PlanetaryFrames.Blur7(lum, reader.Width, reader.Height)));
                    gdxs[i] = dx; gdys[i] = dy;
                    if (i % 25 == 0) { job.FramesAligned = i + 1; Notify(job); }
                }
            } else {
                double rx = cxs[picked[0]], ry = cys[picked[0]];
                for (int i = 0; i < reader.FrameCount; i++) { gdxs[i] = rx - cxs[i]; gdys[i] = ry - cys[i]; }
            }
            var shifts = new (double dx, double dy)[picked.Length];
            for (int k = 0; k < picked.Length; k++) shifts[k] = (gdxs[picked[k]], gdys[picked[k]]);
            job.FramesAligned = picked.Length;
            Notify(job);

            if (job.Config.AlignmentPoints
                    && await TryStackWithAlignmentPointsAsync(job, reader, bayer, gdxs, gdys, ranked, refPlanes, ct)) {
                return;
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

            NormaliseIfAsked(job, pixels, channels, npx);

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

    /// <summary>PLANETAP: local registration on a mesh of alignment points.
    /// Returns false (nothing written) when the target is too small for a
    /// mesh, so the caller falls back to the global stack.</summary>
    private async Task<bool> TryStackWithAlignmentPointsAsync(
            StackJob job, SerFileReader reader, BayerPatternEnum bayer,
            double[] gdxs, double[] gdys, int[] ranked, PlanetaryFrames.Planes bestPlanes, CancellationToken ct) {
        int w = reader.Width, h = reader.Height, npx = w * h, n = reader.FrameCount;
        bool cfa = bayer != BayerPatternEnum.None;
        var cfg = job.Config;
        (double dx, double dy) Global(int i) => (gdxs[i], gdys[i]);

        // 1. reference = mean of the best ReferencePercent frames, globally aligned
        int refCount = Math.Clamp((int)Math.Round(n * cfg.ReferencePercent / 100.0), 1, n);
        var accR = new float[npx]; var accG = cfa ? new float[npx] : accR; var accB = cfa ? new float[npx] : accR;
        var wgt = new float[npx];
        for (int k = 0; k < refCount; k++) {
            ct.ThrowIfCancellationRequested();
            var planes = k == 0 ? bestPlanes : PlanetaryFrames.Split(reader.ReadFrameAsUshort(ranked[k]), w, h, bayer);
            var (dx, dy) = Global(ranked[k]);
            if (cfa) {
                PlanetaryFrames.AccumulateShifted(planes.R, w, h, dx, dy, accR, wgt);
                var w2 = new float[npx];
                PlanetaryFrames.AccumulateShifted(planes.G, w, h, dx, dy, accG, w2);
                PlanetaryFrames.AccumulateShifted(planes.B, w, h, dx, dy, accB, w2);
            } else PlanetaryFrames.AccumulateShifted(planes.Lum, w, h, dx, dy, accR, wgt);
        }
        var refR = Mean(accR, wgt); var refG = cfa ? Mean(accG, wgt) : refR; var refB = cfa ? Mean(accB, wgt) : refR;
        var refLum = new float[npx];
        for (int i = 0; i < npx; i++) refLum[i] = cfa ? (refR[i] + 2f * refG[i] + refB[i]) * 0.25f : refR[i];
        var refLumB = PlanetaryFrames.Blur7(refLum, w, h);

        // 2. mesh on the reference
        var mesh = AlignmentPoints.BuildMesh(refLumB, w, h,
            new AlignmentPoints.MeshOptions(cfg.ApHalfBox, cfg.ApSearchWidth, cfg.ApStructureThreshold));
        if (mesh.Count < 2) {
            _logger.LogInformation("Planetary align: target too small for an alignment-point mesh ({N} points); global registration", mesh.Count);
            return false;
        }
        job.AlignmentPointCount = mesh.Count;
        var (rbg, rpeak) = PlanetaryFrames.Levels(refLum);
        double range = Math.Max(1.0, rpeak - rbg);
        float normThreshold = (float)(rbg + 0.25 * range);
        double refMean = PlanetaryFrames.MeanAbove(refLum, normThreshold);
        _logger.LogInformation("Planetary align: {N} alignment points (half box {HB}, search {SW})", mesh.Count, cfg.ApHalfBox, cfg.ApSearchWidth);

        // 3. local ranking: every frame, every point
        SetPhase(job, StackPhase.Analyzing);
        var local = new double[mesh.Count][];
        for (int a = 0; a < mesh.Count; a++) local[a] = new double[n];
        for (int i = 0; i < n; i++) {
            ct.ThrowIfCancellationRequested();
            var lumB = PlanetaryFrames.Blur7(PlanetaryFrames.Split(reader.ReadFrameAsUshort(i), w, h, bayer).Lum, w, h);
            var (gdx, gdy) = Global(i);
            for (int a = 0; a < mesh.Count; a++)
                local[a][i] = AlignmentPoints.LocalSharpness(lumB, w, h, mesh[a], gdx, gdy, range);
            if (i % 25 == 0 || i == n - 1) { job.FramesAnalyzed = i + 1; Notify(job); }
        }
        int stackSize = Math.Clamp((int)Math.Ceiling(n * cfg.ApFramePercent / 100.0), 1, n);
        var usedBy = new List<int>[n];
        for (int a = 0; a < mesh.Count; a++) {
            var q = local[a];
            mesh[a].BestFrames = Enumerable.Range(0, n).OrderByDescending(i => q[i]).Take(stackSize).ToArray();
            foreach (var i in mesh[a].BestFrames) (usedBy[i] ??= new List<int>()).Add(a);
        }
        job.FramesPicked = usedBy.Count(u => u != null);

        // 4. local registration + ramp-weighted accumulation
        SetPhase(job, StackPhase.Stacking);
        var apR = new float[npx]; var apG = cfa ? new float[npx] : apR; var apB = cfa ? new float[npx] : apR;
        var apW = new float[npx];
        int stacked = 0, rejected = 0, accepted = 0; double localSum = 0;
        for (int i = 0; i < n; i++) {
            if (usedBy[i] == null) continue;
            ct.ThrowIfCancellationRequested();
            var planes = PlanetaryFrames.Split(reader.ReadFrameAsUshort(i), w, h, bayer);
            var lumB = PlanetaryFrames.Blur7(planes.Lum, w, h);
            var (gdx, gdy) = Global(i);
            float gain = PlanetaryFrames.NormalisationGain(refMean, PlanetaryFrames.MeanAbove(planes.Lum, normThreshold));
            foreach (var a in usedBy[i]) {
                // LocalShift returns the TOTAL shift (global + local) that puts
                // this frame's box onto the reference box.
                var shift = cfg.ApDeWarp
                    ? AlignmentPoints.LocalShift(refLumB, lumB, w, h, mesh[a], gdx, gdy, cfg.ApSearchWidth)
                    : (gdx, gdy);
                if (shift == null) { rejected++; continue; }
                var (dx, dy) = shift.Value;
                accepted++; localSum += Math.Sqrt((dx - gdx) * (dx - gdx) + (dy - gdy) * (dy - gdy));
                if (cfa) {
                    AlignmentPoints.AccumulatePatch(planes.R, w, h, mesh[a], dx, dy, gain, apR, apW);
                    var w2 = new float[npx];
                    AlignmentPoints.AccumulatePatch(planes.G, w, h, mesh[a], dx, dy, gain, apG, w2);
                    AlignmentPoints.AccumulatePatch(planes.B, w, h, mesh[a], dx, dy, gain, apB, w2);
                } else AlignmentPoints.AccumulatePatch(planes.Lum, w, h, mesh[a], dx, dy, gain, apR, apW);
            }
            stacked++;
            if (stacked % 25 == 0) { job.FramesStacked = stacked; job.FramesAligned = stacked; Notify(job); }
        }
        job.FramesStacked = stacked; job.FramesAligned = stacked;
        job.ApMatchesAccepted = accepted; job.ApMatchesRejected = rejected;
        job.ApMeanLocalShiftPx = accepted > 0 ? localSum / accepted : 0;
        if (rejected > 0)
            _logger.LogInformation("Planetary align: {R} local matches rejected (optimum on the search border)", rejected);

        // 5. merge with the reference where the mesh did not reach
        SetPhase(job, StackPhase.Writing);
        ushort[] pixels; int channels;
        const double blend = 0.2;
        if (cfa) {
            var r = AlignmentPoints.Merge(apR, apW, refR, stackSize, blend);
            var g = AlignmentPoints.Merge(apG, apW, refG, stackSize, blend);
            var b = AlignmentPoints.Merge(apB, apW, refB, stackSize, blend);
            pixels = new ushort[npx * 3];
            Array.Copy(r, 0, pixels, 0, npx); Array.Copy(g, 0, pixels, npx, npx); Array.Copy(b, 0, pixels, npx * 2, npx);
            channels = 3;
        } else { pixels = AlignmentPoints.Merge(apR, apW, refR, stackSize, blend); channels = 1; }
        await WriteStackAsync(job, reader, pixels, channels, ct);
        _logger.LogInformation("Planetary stack OK ({AP} alignment points, {S} frames used) → {Path}",
            mesh.Count, stacked, job.OutputPath);
        return true;
    }

    private static float[] Mean(float[] acc, float[] wgt) {
        var o = new float[acc.Length];
        for (int i = 0; i < o.Length; i++) o[i] = wgt[i] > 0 ? acc[i] / wgt[i] : 0f;
        return o;
    }

    /// <summary>Apply the output level normalisation when the job asks for it,
    /// and record what it did: a stack that comes out flat is nearly always the
    /// black level, not the alignment.</summary>
    private void NormaliseIfAsked(StackJob job, ushort[] pixels, int channels, int npx) {
        if (!job.Config.NormalizeLevels) return;
        var (floor, gain) = PlanetaryFrames.NormaliseLevels(pixels, channels, npx);
        _logger.LogInformation("Stack levels: floor [{Floor}] gain {Gain:F2}",
            string.Join(", ", floor.Select(f => f.ToString("F0"))), gain);
    }

    private async Task WriteStackAsync(StackJob job, SerFileReader reader, ushort[] pixels, int channels, CancellationToken ct) {
        Directory.CreateDirectory(job.Config.OutputDir);
        NormaliseIfAsked(job, pixels, channels, reader.Width * reader.Height);
        var outName = $"{job.Config.OutputName}_{DateTime.UtcNow:yyyy-MM-ddTHH-mm-ss}.fits";
        var outPath = Path.Combine(job.Config.OutputDir, outName);
        var imageData = new BaseImageData(pixels,
            new ImageProperties {
                Width = reader.Width, Height = reader.Height, BitDepth = 16, Channels = channels,
                IsBayered = false, BayerPattern = BayerPatternEnum.None
            },
            new ImageMetaData());
        imageData.MetaData.Camera.Name = reader.Instrument;
        imageData.MetaData.Telescope.Name = reader.Telescope;
        await Task.Run(() => FITSWriter.Write(imageData, outPath), ct);
        job.OutputPath = outPath;
        SetPhase(job, StackPhase.Ok);
        job.CompletedAt = DateTime.UtcNow;
        Notify(job);
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
    string OutputName = "stack",
    /// <summary>PLANETAP: register locally on a mesh of alignment points
    /// (PlanetarySystemStacker style) when the target is large enough for a
    /// mesh; otherwise the single global registration is used.</summary>
    bool AlignmentPoints = true,
    int ApHalfBox = 24,
    int ApSearchWidth = 14,
    /// <summary>Best frames kept PER alignment point, percent of all frames.</summary>
    double ApFramePercent = 10,
    double ApStructureThreshold = 0.04,
    /// <summary>Globally aligned best frames averaged into the reference the
    /// mesh is built on and that fills what the mesh does not cover.</summary>
    double ReferencePercent = 5,
    /// <summary>PSS de_warp: search a local shift at every point (true) or
    /// stack the point's patches with the global shift only (false), which
    /// still gives per-point frame selection.</summary>
    bool ApDeWarp = true,
    /// <summary>Subtract the per-channel black level and rescale the result to
    /// the 16-bit range before writing. Off writes the raw stacked levels.</summary>
    bool NormalizeLevels = true);

public class StackJob {
    public string Id { get; set; } = "";
    public StackConfig Config { get; set; } = new("", "");
    public StackPhase Phase { get; set; }
    public int TotalFrames { get; set; }
    public int FramesAnalyzed { get; set; }
    public int FramesPicked { get; set; }
    public int FramesAligned { get; set; }
    public int FramesStacked { get; set; }
    /// <summary>PLANETAP: alignment points used (0 = global registration only).</summary>
    public int AlignmentPointCount { get; set; }
    /// <summary>PLANETAP diagnostics: local matches accepted / rejected, and the
    /// mean magnitude of the local correction on top of the global shift.</summary>
    public int ApMatchesAccepted { get; set; }
    public int ApMatchesRejected { get; set; }
    public double ApMeanLocalShiftPx { get; set; }
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