using System.Collections.Concurrent;
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
                // path isn't built yet — fail clearly instead of producing
                // garbage.
                Fail(job, $"Color mode {reader.ColorMode} not yet supported (mono / Bayer only)");
                return;
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
            // (bilinear / lanczos) — deferred to follow-up.
            var refC = centroids[0];

            // Phase 5: Stack ---------------------------------------------------
            SetPhase(job, StackPhase.Stacking);
            // Accumulator: uint accumulator + count per pixel so we can mean
            // at the end. For up to 65535 frames of uint16 this fits in uint32.
            var accum = new uint[reader.Width * reader.Height];
            var counts = new ushort[reader.Width * reader.Height];
            int stacked = 0;
            for (int k = 0; k < picked.Length; k++) {
                ct.ThrowIfCancellationRequested();
                var frame = reader.ReadFrameAsUshort(picked[k]);
                int dx = (int)Math.Round(refC.X - centroids[k].X);
                int dy = (int)Math.Round(refC.Y - centroids[k].Y);
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
            var imageData = new BaseImageData(stacked16,
                new ImageProperties {
                    Width = reader.Width,
                    Height = reader.Height,
                    BitDepth = 16,
                    IsBayered = reader.ColorMode != SerColorMode.Mono
                },
                new ImageMetaData());
            imageData.MetaData.Camera.Name = reader.Instrument;
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
