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

using System.Diagnostics;
using NINA.Core.Enum;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Polaris.Services.External;
using NINA.Polaris.Services.Qnn;
using NINA.Polaris.Services.Rknn;
using NINA.Polaris.Services.Studio;

namespace NINA.Polaris.Services;

/// <summary>
/// On-demand hardware benchmark. Runs the *real* Polaris processing code
/// paths (star detection + alignment + resample for stacking, debayer +
/// autostretch + JPEG + LZ4 for the capture/video stream) over a
/// deterministic, in-memory synthetic frame, plus a raw CPU/memory
/// micro-bench, and reports throughput so the user can compare the
/// performance of different host machines (e.g. a Raspberry Pi 5 vs a
/// Pi 4 vs an Orange Pi 5 Pro vs an Intel mini-PC).
///
/// Why synthetic? Real-camera capture throughput is dominated by the
/// camera + USB link, not the host, so it is NOT comparable across
/// boards. The synthetic suite isolates the host CPU/RAM and runs the
/// identical workload everywhere, so the numbers are directly
/// comparable. An optional live-camera measurement is reported
/// separately and clearly labelled as camera-dependent.
///
/// Not a hosted service: it does nothing until <see cref="Start"/> is
/// called from the Settings UI. Work runs on a background task; progress
/// is surfaced via the /ws/status payload + the REST status endpoint.
/// </summary>
public class BenchmarkService {
    private readonly ILogger<BenchmarkService> _logger;
    private readonly BenchmarkResultsStore _store;
    private readonly EquipmentManager _equipment;
    private readonly CameraStreamService _cameraStream;
    private readonly LiveStackingService _liveStack;
    private readonly ProfileService _profiles;

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;

    // Fixed workload size: every device runs the identical frame so the
    // results compare apples to apples. Metrics are also normalised to
    // Mpx/s so a future size change stays comparable. 4096x4096 = 16.78
    // MP, in the ballpark of a mid-size OSC sensor.
    private const int FrameW = 4096;
    private const int FrameH = 4096;
    private const int Seed = 0x5EED;

    private static readonly TimeSpan WorkloadBudget = TimeSpan.FromSeconds(3);
    private const int MinIters = 2;
    private const int MaxIters = 60;

    // Composite-score baselines (measured Raspberry Pi 5 throughput) so a
    // Pi 5 scores ~100. Purely relative; the detailed table is the
    // substance, and these constants are identical on every device so the
    // ranking between boards is unaffected by their exact values.
    private const double StackBaselineMpxS = 20.0;   // Pi 5 ~20 Mpx/s
    private const double EncodeBaselineMpxS = 13.0;  // Pi 5 ~13 Mpx/s
    private const double CpuBaselineMflops = 5000.0; // Pi 5 ~5 GFLOPS multi (compute-bound)

    public string State { get; private set; } = "idle"; // idle|running|complete|error
    public int Progress { get; private set; }
    public string Phase { get; private set; } = "";
    public string? LastError { get; private set; }
    public BenchmarkResult? LastResult { get; private set; }

    public bool IsRunning => State == "running";

    public BenchmarkService(
        ILogger<BenchmarkService> logger,
        BenchmarkResultsStore store,
        EquipmentManager equipment,
        CameraStreamService cameraStream,
        LiveStackingService liveStack,
        ProfileService profiles,
        NINA.Image.Gpu.IGpuCompute? gpu = null,
        RknnInferenceService? rknn = null,
        QnnInferenceService? qnn = null) {
        _logger = logger;
        _store = store;
        _equipment = equipment;
        _cameraStream = cameraStream;
        _liveStack = liveStack;
        _profiles = profiles;
        _gpu = gpu ?? new NINA.Image.Gpu.CpuGpuCompute();
        _rknn = rknn;
        _qnn = qnn;
    }
    private readonly NINA.Image.Gpu.IGpuCompute _gpu;
    private readonly RknnInferenceService? _rknn;
    private readonly QnnInferenceService? _qnn;

    public object GetStatus() => new {
        state = State,
        progress = Progress,
        phase = Phase,
        lastError = LastError,
        lastResult = LastResult
    };

    /// <summary>Kick off a benchmark run on a background task. Returns
    /// null on success, or an error string if it cannot start (already
    /// running, or a live capture/stack is active).</summary>
    public string? Start(BenchmarkRequest req) {
        lock (_gate) {
            if (State == "running") return "A benchmark is already running.";
            if (_liveStack.IsRunning) return "Stop live stacking before running a benchmark.";
            if (_cameraStream.IsRunning) return "Stop the video stream before running a benchmark.";

            _cts = new CancellationTokenSource();
            State = "running";
            Progress = 0;
            Phase = "Starting";
            LastError = null;
            var ct = _cts.Token;
            _ = Task.Run(() => RunInternalAsync(req ?? new BenchmarkRequest(), ct));
            return null;
        }
    }

    public void Cancel() => _cts?.Cancel();

    private async Task RunInternalAsync(BenchmarkRequest req, CancellationToken ct) {
        try {
            SetPhase("Generating frames", 3);
            // The whole synthetic suite is CPU-bound; run it off the
            // thread-pool entry so the await chain stays responsive. Each
            // workload reports a 0..1 fraction so the progress bar advances
            // continuously within a phase instead of jumping at the
            // boundaries (and looking stuck during a multi-second stage).
            bool cam = req.IncludeCamera;
            int cpuHi = cam ? 80 : 98;
            var (stacking, encode, cpu) = await Task.Run(() => {
                Phase = "Stacking pipeline";
                var s = RunStackingWorkload(FrameW, FrameH, WorkloadBudget, ct, f => SetProgress(5, 35, f));
                Phase = "Capture / video encode";
                var e = RunEncodeWorkload(FrameW, FrameH, WorkloadBudget, ct, f => SetProgress(35, 60, f));
                Phase = "CPU / memory";
                var c = RunCpuWorkload(ct, f => SetProgress(60, cpuHi, f));
                return (s, e, c);
            }, ct);

            // OCL: GPU-vs-CPU on the image kernels (skipped when no OpenCL GPU
            // or the GPU toggle is off -> Ran=false).
            GpuResult? gpuRes = null;
            try {
                Phase = "GPU (OpenCL)";
                gpuRes = await Task.Run(() => RunGpuWorkload(ct), ct);
            } catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _logger.LogWarning(ex, "GPU benchmark workload failed"); }

            // QNN-5: NPU (AI inference) — times a real GraXpert denoise on the
            // Hexagon/Rockchip NPU (Ran=false off an NPU host, like the GPU row).
            NpuResult? npuRes = null;
            try {
                Phase = "NPU (AI inference)";
                npuRes = await Task.Run(() => RunNpuWorkload(ct), ct);
            } catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _logger.LogWarning(ex, "NPU benchmark workload failed"); }

            CameraResult? camera = null;
            CameraVideoResult? cameraVideo = null;
            if (cam) {
                Phase = "Live camera (capture)";
                camera = await RunCameraWorkloadAsync(req, ct, f => SetProgress(80, 90, f));
                Phase = "Live camera (video stream)";
                cameraVideo = await RunCameraVideoWorkloadAsync(req, ct, f => SetProgress(90, 99, f));
            }

            var dev = HostInfo.Current;
            var device = new BenchmarkDevice(
                dev.Kind, dev.Model, dev.Os, dev.Architecture, dev.Cores,
                dev.ShortLabel, dev.Cpu, dev.CpuLabel);
            double mpx = (double)FrameW * FrameH / 1_000_000.0;
            double composite = ComputeComposite(stacking, encode, cpu);

            var result = new BenchmarkResult(
                Timestamp: DateTime.UtcNow.ToString("o"),
                Device: device,
                FrameWidth: FrameW,
                FrameHeight: FrameH,
                Megapixels: Math.Round(mpx, 2),
                Stacking: stacking,
                Encode: encode,
                Cpu: cpu,
                CompositeScore: Math.Round(composite, 1),
                Camera: camera,
                CameraVideo: cameraVideo,
                Gpu: gpuRes,
                Npu: npuRes);

            try { await _store.SaveResultAsync(result, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to persist benchmark result"); }

            LastResult = result;
            State = "complete";
            Progress = 100;
            Phase = "Done";
            _logger.LogInformation(
                "Benchmark complete on {Device}: score {Score}, stacking {Sfps:n1} fps, encode {Efps:n1} fps",
                device.ShortLabel, composite, stacking.Fps, encode.Fps);
        } catch (OperationCanceledException) {
            State = "idle";
            Progress = 0;
            Phase = "Cancelled";
        } catch (Exception ex) {
            _logger.LogError(ex, "Benchmark failed");
            LastError = ex.Message;
            State = "error";
            Phase = "Error";
        } finally {
            lock (_gate) { _cts?.Dispose(); _cts = null; }
        }
    }

    private void SetPhase(string phase, int progress) {
        Phase = phase;
        Progress = progress;
    }

    /// <summary>Maps a workload's 0..1 completion fraction onto a [lo, hi]
    /// slice of the overall progress bar, so it advances smoothly within a
    /// phase rather than jumping only at phase boundaries.</summary>
    private void SetProgress(int lo, int hi, double fraction) {
        var f = Math.Clamp(fraction, 0.0, 1.0);
        Progress = lo + (int)Math.Round((hi - lo) * f);
    }

    // ----- Workload A: stacking pipeline -----

    internal static StackingResult RunStackingWorkload(int w, int h, TimeSpan budget, CancellationToken ct, Action<double>? onProgress = null) {
        var reference = GenerateStarField(w, h, Seed, 0, 0);
        // A small known shift so StarMatcher has a real translation to
        // recover (and ImageResampler real work to do).
        var current = GenerateStarField(w, h, Seed, 7, -5);

        var detector = new StarDetector();
        var refStars = detector.Detect(reference, w, h);

        var accum = new float[w * h];
        var count = new int[w * h];

        // Warmup (JIT + caches) - not measured.
        {
            var cur = detector.Detect(current, w, h);
            var t0 = StarMatcher.Match(refStars, cur, maxSearchRadius: 60);
            var aligned0 = t0 != null
                ? ImageResampler.ApplyTransform(current, w, h, t0)
                : current;
            Accumulate(accum, count, aligned0);
            _ = ImageStatistics.ComputeBackgroundSnrFromData(current);
        }

        double detectMs = 0, matchMs = 0, resampleMs = 0, statsMs = 0;
        int iters = 0, starCount = refStars.Count;
        var clock = Stopwatch.StartNew();
        var sw = new Stopwatch();
        while (iters < MaxIters && (iters < MinIters || clock.Elapsed < budget)) {
            ct.ThrowIfCancellationRequested();

            sw.Restart();
            var curStars = detector.Detect(current, w, h);
            sw.Stop(); detectMs += sw.Elapsed.TotalMilliseconds;
            starCount = curStars.Count;

            sw.Restart();
            var t = StarMatcher.Match(refStars, curStars, maxSearchRadius: 60);
            sw.Stop(); matchMs += sw.Elapsed.TotalMilliseconds;

            sw.Restart();
            var aligned = t != null
                ? ImageResampler.ApplyTransform(current, w, h, t)
                : current;
            Accumulate(accum, count, aligned);
            sw.Stop(); resampleMs += sw.Elapsed.TotalMilliseconds;

            sw.Restart();
            _ = ImageStatistics.ComputeBackgroundSnrFromData(current);
            sw.Stop(); statsMs += sw.Elapsed.TotalMilliseconds;

            iters++;
            onProgress?.Invoke(clock.Elapsed.TotalMilliseconds / budget.TotalMilliseconds);
        }
        onProgress?.Invoke(1.0);

        double n = Math.Max(1, iters);
        double totalPerFrame = (detectMs + matchMs + resampleMs + statsMs) / n;
        double fps = totalPerFrame > 0 ? 1000.0 / totalPerFrame : 0;
        double mpx = (double)w * h / 1_000_000.0;
        return new StackingResult(
            DetectMs: Math.Round(detectMs / n, 2),
            MatchMs: Math.Round(matchMs / n, 2),
            ResampleMs: Math.Round(resampleMs / n, 2),
            StatsMs: Math.Round(statsMs / n, 2),
            TotalMs: Math.Round(totalPerFrame, 2),
            Fps: Math.Round(fps, 2),
            MpxPerSec: Math.Round(mpx * fps, 1),
            Iterations: iters,
            StarCount: starCount);
    }

    private static void Accumulate(float[] accum, int[] count, ushort[] frame) {
        for (int i = 0; i < frame.Length; i++) {
            accum[i] += frame[i];
            count[i]++;
        }
    }

    // ----- Workload B: capture / video encode -----

    internal static EncodeResult RunEncodeWorkload(int w, int h, TimeSpan budget, CancellationToken ct, Action<double>? onProgress = null) {
        var frame = GenerateStarField(w, h, Seed, 0, 0);
        const BayerPatternEnum pattern = BayerPatternEnum.RGGB;
        var props = new ImageProperties {
            Width = w, Height = h, BitDepth = 16,
            IsBayered = true, BayerPattern = pattern, Channels = 1
        };
        var img = new BaseImageData(frame, props);
        var buffer = new ImageBuffer(frame, w, h, 16, pattern);
        long uncompressedBytes = (long)w * h * 2;

        // Warmup.
        _ = BayerDebayer.Bilinear(frame, w, h, pattern);
        _ = FitsThumbnailer.RenderJpegFromImageData(img, 1280, 70);
        _ = buffer.ToLz4Compressed();

        double debayerMs = 0, jpegMs = 0, lz4Ms = 0;
        int iters = 0;
        var clock = Stopwatch.StartNew();
        var sw = new Stopwatch();
        while (iters < MaxIters && (iters < MinIters || clock.Elapsed < budget)) {
            ct.ThrowIfCancellationRequested();

            sw.Restart();
            _ = BayerDebayer.Bilinear(frame, w, h, pattern);
            sw.Stop(); debayerMs += sw.Elapsed.TotalMilliseconds;

            sw.Restart();
            _ = FitsThumbnailer.RenderJpegFromImageData(img, 1280, 70);
            sw.Stop(); jpegMs += sw.Elapsed.TotalMilliseconds;

            sw.Restart();
            _ = buffer.ToLz4Compressed();
            sw.Stop(); lz4Ms += sw.Elapsed.TotalMilliseconds;

            iters++;
            onProgress?.Invoke(clock.Elapsed.TotalMilliseconds / budget.TotalMilliseconds);
        }
        onProgress?.Invoke(1.0);

        double n = Math.Max(1, iters);
        double totalPerFrame = (debayerMs + jpegMs + lz4Ms) / n;
        double fps = totalPerFrame > 0 ? 1000.0 / totalPerFrame : 0;
        double mpx = (double)w * h / 1_000_000.0;
        double lz4SecPerFrame = (lz4Ms / n) / 1000.0;
        double lz4MBps = lz4SecPerFrame > 0
            ? (uncompressedBytes / (1024.0 * 1024.0)) / lz4SecPerFrame : 0;
        return new EncodeResult(
            DebayerMs: Math.Round(debayerMs / n, 2),
            JpegMs: Math.Round(jpegMs / n, 2),
            Lz4Ms: Math.Round(lz4Ms / n, 2),
            TotalMs: Math.Round(totalPerFrame, 2),
            Fps: Math.Round(fps, 2),
            MpxPerSec: Math.Round(mpx * fps, 1),
            Lz4MBps: Math.Round(lz4MBps, 1),
            Iterations: iters);
    }

    // ----- Workload C: raw CPU / memory baseline -----

    internal static CpuResult RunCpuWorkload(CancellationToken ct, Action<double>? onProgress = null) {
        int cores = Math.Max(1, Environment.ProcessorCount);
        // Compute-bound FLOP kernel: a tight loop over four independent
        // accumulator chains held in registers (no large array), so this
        // measures CPU floating-point throughput, NOT memory bandwidth.
        // That distinction matters: a memory-bound kernel saturates the
        // shared bus with one thread on bandwidth-limited SBCs (Pi 5
        // ~12 GB/s), so spreading it across cores adds contention and
        // shows <1x "scaling" - misleading for a CPU score. With a
        // register-bound kernel the multi-thread run scales ~core-count.
        const long iters = 120_000_000;   // per single-thread run
        const double flopsPerIter = 8.0;  // 4 chains x (multiply + add)
        double singleFlops = iters * flopsPerIter;

        FloatChains(2_000_000); // warmup (JIT)
        ct.ThrowIfCancellationRequested();
        var sw = Stopwatch.StartNew();
        double sink = FloatChains(iters);
        sw.Stop();
        double singleMflops = sw.Elapsed.TotalSeconds > 0
            ? singleFlops / sw.Elapsed.TotalSeconds / 1e6 : 0;
        onProgress?.Invoke(0.45);

        // Multi-thread: every core runs the full per-core workload
        // independently (no shared data), so total work = perCore * cores.
        ct.ThrowIfCancellationRequested();
        var opts = new ParallelOptions { MaxDegreeOfParallelism = cores, CancellationToken = ct };
        long perCore = iters;
        double multiFlops = (double)perCore * flopsPerIter * cores;
        double mtSink = 0;
        var mtLock = new object();
        sw.Restart();
        Parallel.For(0, cores, opts, _ => {
            double s = FloatChains(perCore);
            lock (mtLock) { mtSink += s; }
        });
        sw.Stop();
        double multiMflops = sw.Elapsed.TotalSeconds > 0
            ? multiFlops / sw.Elapsed.TotalSeconds / 1e6 : 0;
        GC.KeepAlive(sink + mtSink);
        onProgress?.Invoke(0.8);

        // Memory bandwidth: stream a large buffer across ALL cores. A
        // single thread cannot saturate a modern memory controller - it
        // caps at single-core copy speed (~10-15 GB/s) regardless of the
        // platform's real bandwidth, which made a DDR5 desktop report the
        // same ~12 GB/s as a Pi 5. Splitting the copy across cores measures
        // the platform's aggregate bandwidth (STREAM-style), which is what
        // actually differentiates the boards. Each thread streams its own
        // contiguous chunk bwPasses times; the per-thread Array.Copy uses
        // the runtime's vectorized memmove.
        ct.ThrowIfCancellationRequested();
        const int bw = 16_000_000; // 16M doubles = 128 MB (exceeds any L3)
        const int bwPasses = 6;
        var src = new double[bw];
        var dst = new double[bw];
        Array.Copy(src, dst, bw); // warmup + page-in
        var bwOpts = new ParallelOptions { MaxDegreeOfParallelism = cores, CancellationToken = ct };
        sw.Restart();
        Parallel.For(0, cores, bwOpts, t => {
            int chunk = bw / cores;
            int start = t * chunk;
            int len = (t == cores - 1) ? bw - start : chunk;
            for (int p = 0; p < bwPasses; p++)
                Array.Copy(src, start, dst, start, len);
        });
        sw.Stop();
        // Each copy touches read+write = 2 * bytes.
        double movedBytes = (double)bw * 8 * 2 * bwPasses;
        double memGBps = sw.Elapsed.TotalSeconds > 0
            ? movedBytes / sw.Elapsed.TotalSeconds / 1e9 : 0;
        onProgress?.Invoke(1.0);

        double scaling = singleMflops > 0 ? multiMflops / singleMflops : 0;
        return new CpuResult(
            SingleThreadMflops: Math.Round(singleMflops, 0),
            MultiThreadMflops: Math.Round(multiMflops, 0),
            CoreScaling: Math.Round(scaling, 2),
            MemBandwidthGBps: Math.Round(memGBps, 1),
            Cores: cores);
    }

    /// <summary>Compute-bound FLOP loop: four independent multiply+add
    /// accumulator chains kept in registers. Independent chains give the
    /// CPU instruction-level parallelism to fill its FP pipeline, while
    /// the lack of any array access keeps it off the memory bus so it
    /// scales with core count when run on multiple threads. Returns the
    /// accumulated value so the JIT cannot elide the loop. Factors are a
    /// mix above and below 1.0 so the values stay finite over the run.</summary>
    private static double FloatChains(long iters) {
        double a = 1.0, b = 1.0001, c = 0.9999, d = 1.00002;
        for (long i = 0; i < iters; i++) {
            a = a * 1.0000001 + 0.5;
            b = b * 0.9999999 + 0.25;
            c = c * 1.0000002 + 0.125;
            d = d * 0.9999998 + 0.0625;
        }
        return a + b + c + d;
    }

    // ----- Optional live-camera workload -----

    private async Task<CameraResult> RunCameraWorkloadAsync(BenchmarkRequest req, CancellationToken ct, Action<double>? onProgress = null) {
        var cam = _equipment.Camera;
        if (cam == null || !cam.IsConnected)
            return new CameraResult(0, 0, 0, 0, 0, 0, "No camera connected.");

        int frames = Math.Clamp(req.CameraFrames, 1, 30);
        double exposure = Math.Clamp(req.CameraExposure, 0.0, 60.0);
        var opts = new CaptureOptions(Gain: req.CameraGain, ImageType: "LIGHT");
        var times = new List<double>(frames);
        int w = 0, h = 0;
        long bytes = 0;
        try {
            // Warmup capture (driver spin-up, buffer alloc) - discarded.
            await CameraCaptureGate.RunAsync(() => cam.CaptureAsync(exposure, opts, ct), ct);
            for (int i = 0; i < frames; i++) {
                ct.ThrowIfCancellationRequested();
                var sw = Stopwatch.StartNew();
                var frame = await CameraCaptureGate.RunAsync(
                    () => cam.CaptureAsync(exposure, opts, ct), ct);
                sw.Stop();
                times.Add(sw.Elapsed.TotalMilliseconds);
                w = frame.Properties.Width;
                h = frame.Properties.Height;
                bytes = (long)frame.Data.Length * 2;
                onProgress?.Invoke((i + 1) / (double)frames);
            }
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            return new CameraResult(times.Count, 0, 0, w, h, 0, ex.Message);
        }

        double meanMs = times.Count > 0 ? times.Average() : 0;
        double fps = meanMs > 0 ? 1000.0 / meanMs : 0;
        double mbps = meanMs > 0
            ? (bytes / (1024.0 * 1024.0)) / (meanMs / 1000.0) : 0;
        return new CameraResult(
            Frames: times.Count,
            MeanCaptureMs: Math.Round(meanMs, 1),
            Fps: Math.Round(fps, 2),
            Width: w,
            Height: h,
            MBPerSec: Math.Round(mbps, 1),
            Error: null);
    }

    /// <summary>Measures the real camera video-stream path: starts
    /// CameraStreamService (native CCD_VIDEO_STREAM when the driver
    /// supports it, else the server capture loop), waits for the stream to
    /// actually start producing frames (so a slow start or a native->loop
    /// fallback does not count against the result), then measures the
    /// capture FPS, transmitted (downscaled-JPEG) FPS, frame size and raw
    /// on-wire MB/s over a fixed window using frame-count deltas. A short
    /// streaming exposure is forced regardless of the requested still
    /// exposure, since streaming is about frame rate, not depth. Camera +
    /// USB dependent, so reported separately and not in the composite
    /// score.</summary>
    private async Task<CameraVideoResult> RunCameraVideoWorkloadAsync(BenchmarkRequest req, CancellationToken ct, Action<double>? onProgress = null) {
        var cam = _equipment.Camera;
        if (cam == null || !cam.IsConnected)
            return new CameraVideoResult("idle", 0, 0, 0, 0, 0, 0, 0, "No camera connected.");
        if (_cameraStream.IsRunning)
            return new CameraVideoResult("idle", 0, 0, 0, 0, 0, 0, 0, "A video stream is already running.");

        // Force a short streaming exposure representative of real
        // planetary video (typically ~25-40 ms). The still-capture test
        // already covers the user's chosen exposure; a video benchmark at
        // a 1s exposure would only ever manage ~1 fps and tells us nothing
        // about the streaming path.
        const double streamExposure = 0.03;
        const int warmupMaxTicks = 60;  // up to 6s for the first frame
        const int windowTicks = 50;     // 5s measurement window
        bool roiSet = false;

        // Recording probe state (only used when req.MeasureRecording). The
        // sink mirrors VideoRecordingService.OnFrame exactly: try the write
        // lock with a 5 ms budget, drop the frame if the writer is busy,
        // and time each real SER write. Writing under ImageOutputDir (not
        // /tmp, which is tmpfs/RAM on a Pi and would fake the disk speed)
        // so the number reflects the rig's actual storage.
        Planetary.SerFileWriter? recWriter = null;
        string? recPath = null;
        IDisposable? recSub = null;
        var recLock = new object();
        long recorded = 0, dropped = 0;
        double writeMsTotal = 0;
        bool recording = false;

        try {
            // High-fps planetary video needs a small ROI; a full-frame OSC
            // simply can't deliver 100 fps no matter the host. Center it.
            if (req.VideoRoi > 0 && cam.MaxX > 0 && cam.MaxY > 0) {
                int roi = Math.Min(req.VideoRoi, Math.Min(cam.MaxX, cam.MaxY));
                int rx = (cam.MaxX - roi) / 2, ry = (cam.MaxY - roi) / 2;
                try { await cam.SetSubframeAsync(rx, ry, roi, roi, ct); roiSet = true; }
                catch (Exception ex) { _logger.LogDebug(ex, "Benchmark: ROI set failed (full-frame)"); }
            }

            _cameraStream.Start(new StreamConfig(ExposureSeconds: streamExposure, Gain: req.CameraGain));

            // Warmup: wait until the first frame actually lands (skips the
            // native->loop fallback dead time) or give up after the cap.
            int warm = 0;
            while (warm < warmupMaxTicks && _cameraStream.FrameCount == 0) {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(100, ct);
                warm++;
                onProgress?.Invoke(0.1 * warm / warmupMaxTicks);
            }

            // Attach the recording sink AFTER warmup so every recorded frame
            // falls inside the measurement window.
            if (req.MeasureRecording && _cameraStream.FrameCount > 0) {
                recording = true;
                recSub = _cameraStream.SubscribeFrames(frame => {
                    if (!Monitor.TryEnter(recLock, 5)) { Interlocked.Increment(ref dropped); return; }
                    try {
                        if (recWriter == null) {
                            var dir = Path.Combine(_profiles.Active.ImageOutputDir, "planetary");
                            Directory.CreateDirectory(dir);
                            recPath = Path.Combine(dir, $".benchmark-probe-{Guid.NewGuid():N}.ser.tmp");
                            recWriter = new Planetary.SerFileWriter(recPath,
                                frame.Properties.Width, frame.Properties.Height,
                                frame.Properties.BitDepth > 0 ? frame.Properties.BitDepth : 16,
                                Planetary.SerColorMode.Mono, "Polaris-bench", cam.DeviceName, "");
                        }
                        var wsw = Stopwatch.StartNew();
                        recWriter.WriteFrame(frame.Data, DateTime.UtcNow);
                        wsw.Stop();
                        writeMsTotal += wsw.Elapsed.TotalMilliseconds;
                        recorded++;
                    } catch { Interlocked.Increment(ref dropped); }
                    finally { Monitor.Exit(recLock); }
                });
            }

            // Measurement window: rate = frame-count delta / elapsed, so
            // the warmup/fallback time never drags the number down.
            long capStart = _cameraStream.FrameCount;
            long txStart = _cameraStream.TransmittedFrames;
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < windowTicks; i++) {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(100, ct);
                onProgress?.Invoke(0.1 + 0.9 * (i + 1) / windowTicks);
            }
            clock.Stop();

            // Detach + close the recording writer before reading counters.
            recSub?.Dispose();
            recSub = null;
            lock (recLock) { try { recWriter?.Dispose(); } catch { } recWriter = null; }

            double secs = Math.Max(0.001, clock.Elapsed.TotalSeconds);
            long capFrames = _cameraStream.FrameCount - capStart;
            long txFrames = _cameraStream.TransmittedFrames - txStart;
            double captureFps = capFrames / secs;
            double transmitFps = txFrames / secs;
            int w = _cameraStream.LastFrameWidth;
            int h = _cameraStream.LastFrameHeight;
            long raw = _cameraStream.LastFrameRawBytes;
            string mode = _cameraStream.Mode;
            double mbps = raw > 0 && captureFps > 0
                ? (raw / (1024.0 * 1024.0)) * captureFps : 0;

            long recCount = Interlocked.Read(ref recorded);
            long dropCount = Interlocked.Read(ref dropped);
            double recordFps = recording ? recCount / secs : 0;
            double meanWriteMs = recCount > 0 ? writeMsTotal / recCount : 0;

            string? err = capFrames == 0
                ? "The camera produced no video frames (driver may not support streaming)."
                : null;

            return new CameraVideoResult(
                Mode: mode,
                CaptureFps: Math.Round(captureFps, 2),
                TransmitFps: Math.Round(transmitFps, 2),
                Width: w,
                Height: h,
                MBPerSec: Math.Round(mbps, 1),
                Frames: capFrames,
                DurationSec: (int)Math.Round(secs),
                Error: err,
                RecordFps: Math.Round(recordFps, 2),
                DroppedFrames: dropCount,
                MeanWriteMs: Math.Round(meanWriteMs, 2));
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            return new CameraVideoResult("idle", 0, 0, 0, 0, 0, 0, 0, ex.Message);
        } finally {
            try { recSub?.Dispose(); } catch { }
            lock (recLock) { try { recWriter?.Dispose(); } catch { } }
            if (recPath != null) { try { File.Delete(recPath); } catch { } }
            try { await _cameraStream.StopAsync(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Benchmark: stopping video stream failed"); }
            // Restore the full frame so the probe doesn't leave the camera
            // stuck in a small ROI for the next real capture.
            if (roiSet) {
                try { await cam.SetSubframeAsync(0, 0, cam.MaxX, cam.MaxY, CancellationToken.None); }
                catch (Exception ex) { _logger.LogDebug(ex, "Benchmark: ROI restore failed"); }
            }
        }
    }

    // ----- composite + synthetic frame generation -----

    private static double ComputeComposite(StackingResult s, EncodeResult e, CpuResult c) {
        double sn = s.MpxPerSec / StackBaselineMpxS;
        double en = e.MpxPerSec / EncodeBaselineMpxS;
        double cn = c.MultiThreadMflops / CpuBaselineMflops;
        if (sn <= 0 || en <= 0 || cn <= 0) return 0;
        return 100.0 * Math.Pow(sn * en * cn, 1.0 / 3.0);
    }

    // ----- Workload: GPU (OpenCL) vs CPU on the image kernels -----

    private GpuResult RunGpuWorkload(CancellationToken ct) {
        if (!_gpu.IsHardware)
            return new GpuResult(false, _gpu.BackendName, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        // The GPU section reports the *raw* per-op GPU-vs-CPU speed — that's what
        // justifies the offload policy — so measure with every kernel forced on
        // even if the production policy declines some on this discrete device.
        return _gpu is NINA.Polaris.Services.OpenCl.OpenClGpuCompute ocl
            ? ocl.WithAllKernels(() => MeasureGpu(ct))
            : MeasureGpu(ct);
    }

    private GpuResult MeasureGpu(CancellationToken ct) {
        var none = new GpuResult(false, _gpu.BackendName, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var cpu = new NINA.Image.Gpu.CpuGpuCompute();
        int w = FrameW, h = FrameH;
        double mpx = (double)w * h / 1_000_000.0;
        var frame = GenerateStarField(w, h, Seed, 0, 0);
        var t = new AffineTransform { M00 = 1, M11 = 1, Tx = 7.3, Ty = -5.1 };
        var budget = TimeSpan.FromMilliseconds(700);

        double warpCpu = MeasureMpx(() => cpu.TryWarpAffine(frame, w, h, t, out _), mpx, budget, ct);
        double warpGpu = MeasureMpx(() => _gpu.TryWarpAffine(frame, w, h, t, out _), mpx, budget, ct);
        double debCpu = MeasureMpx(() => cpu.TryDebayerBilinear(frame, w, h, NINA.Core.Enum.BayerPatternEnum.RGGB, out _), mpx, budget, ct);
        double debGpu = MeasureMpx(() => _gpu.TryDebayerBilinear(frame, w, h, NINA.Core.Enum.BayerPatternEnum.RGGB, out _), mpx, budget, ct);
        double blurCpu = MeasureMpx(() => cpu.TrySeparableBlur(frame, w, h, 3, 1.5, out _), mpx, budget, ct);
        double blurGpu = MeasureMpx(() => _gpu.TrySeparableBlur(frame, w, h, 3, 1.5, out _), mpx, budget, ct);

        // GPU declined everything -> disabled at runtime; report not-ran.
        if (warpGpu <= 0 && debGpu <= 0 && blurGpu <= 0) return none;

        static double Spd(double g, double c) => c > 0 ? Math.Round(g / c, 2) : 0;
        static double R(double v) => Math.Round(v, 1);
        double overall = GpuOverallSpeedup(
            new[] { Spd(warpGpu, warpCpu), Spd(debGpu, debCpu), Spd(blurGpu, blurCpu) });
        return new GpuResult(true, _gpu.BackendName,
            R(warpCpu), R(warpGpu), Spd(warpGpu, warpCpu),
            R(debCpu), R(debGpu), Spd(debGpu, debCpu),
            R(blurCpu), R(blurGpu), Spd(blurGpu, blurCpu),
            overall);
    }

    /// <summary>Aggregate the per-op GPU/CPU speedups into one headline figure
    /// using the <i>geometric</i> mean of the ops that ran (speedup &gt; 0). The
    /// geometric mean is the correct way to average ratios: unlike a plain
    /// arithmetic mean it is not dominated by a single large win (e.g. blur 16×
    /// while warp/debayer are &lt;1×), so it doesn't overstate the benefit when
    /// some ops are actually slower on the GPU. Returns 0 when nothing ran.</summary>
    internal static double GpuOverallSpeedup(IEnumerable<double> perOpSpeedups) {
        var s = perOpSpeedups.Where(x => x > 0).ToArray();
        if (s.Length == 0) return 0;
        double product = 1.0;
        foreach (var v in s) product *= v;
        return Math.Round(Math.Pow(product, 1.0 / s.Length), 2);
    }

    /// <summary>Run <paramref name="op"/> repeatedly within the budget and
    /// return megapixels/sec. Returns 0 if the op declines (e.g. GPU disabled).
    /// One warm-up call primes buffers/JIT/kernel build before timing.</summary>
    private static double MeasureMpx(Func<bool> op, double mpx, TimeSpan budget, CancellationToken ct) {
        if (!op()) return 0; // warm-up + capability probe
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int iters = 0;
        while (sw.Elapsed < budget && !ct.IsCancellationRequested) {
            if (!op()) break;
            iters++;
        }
        sw.Stop();
        if (iters == 0 || sw.Elapsed.TotalSeconds <= 0) return 0;
        return iters * mpx / sw.Elapsed.TotalSeconds;
    }

    // ----- Workload: NPU (GraXpert AI denoise) -----

    /// <summary>
    /// Time a real GraXpert Denoise on the board's NPU and report per-tile cost.
    /// Picks the available lane — Qualcomm Hexagon (QAIRT) first, then Rockchip
    /// RKNPU2 — both of which run the same tiled GraXpert pipeline. Off an NPU
    /// host, or when no denoise model is bundled, returns <c>Ran=false</c> with a
    /// reason (same convention as the GPU row). 1024×1024 mono → enough tiles for
    /// a stable per-tile number without dragging the suite out.
    /// </summary>
    private NpuResult RunNpuWorkload(CancellationToken ct) {
        const int w = 1024, h = 1024;
        var opts = new GraXpertOptions(Operation: GraXpertOperation.Denoising, DenoiseStrength: 0.5);

        if (_qnn?.IsAvailable == true &&
            _qnn.CanHandle(GraXpertOperation.Denoising, null, out var qbin, out var qver)) {
            return MeasureNpu(ct, "Qualcomm Hexagon (QAIRT)", $"denoise/{qver}",
                PrecisionFromName(qbin), w, h,
                img => { var r = _qnn.Run(img, opts); return (r.ElapsedMs, r.Tiles); });
        }
        if (_rknn?.IsAvailable == true &&
            _rknn.CanHandle(GraXpertOperation.Denoising, null, out var rbin, out var rver)) {
            var prec = PrecisionFromName(rbin);
            return MeasureNpu(ct, "Rockchip RKNPU2", $"denoise/{rver}",
                string.IsNullOrEmpty(prec) ? "fp16" : prec, w, h,
                img => { var r = _rknn.Run(img, opts); return (r.ElapsedMs, r.Tiles); });
        }

        bool present = _qnn?.IsAvailable == true || _rknn?.IsAvailable == true;
        string diag = present
            ? "NPU present but no denoise model bundled."
            : (_qnn?.Diagnostics ?? _rknn?.Diagnostics ?? "No NPU detected.");
        return new NpuResult(false, "", "", "", 0, 0, 0, 0, 0, diag);
    }

    private NpuResult MeasureNpu(CancellationToken ct, string backend, string model,
                                 string precision, int w, int h,
                                 Func<BaseImageData, (double ms, int tiles)> run) {
        var data = GenerateStarField(w, h, Seed, 0, 0);
        var props = new ImageProperties { Width = w, Height = h, BitDepth = 16, Channels = 1 };
        var img = new BaseImageData(data, props);

        run(img);                       // warmup: model load + DSP spin-up + JIT
        ct.ThrowIfCancellationRequested();

        var budget = TimeSpan.FromSeconds(2);
        double totalMs = 0;
        int tiles = 0, iters = 0;
        var clock = Stopwatch.StartNew();
        while (iters < 8 && (iters < 2 || clock.Elapsed < budget)) {
            ct.ThrowIfCancellationRequested();
            var (ms, t) = run(img);
            totalMs += ms; tiles = t; iters++;
        }

        double n = Math.Max(1, iters);
        double perImageMs = totalMs / n;
        double msPerTile = tiles > 0 ? perImageMs / tiles : 0;
        double tilesPerSec = msPerTile > 0 ? 1000.0 / msPerTile : 0;
        return new NpuResult(true, backend, model, precision,
            Math.Round(msPerTile, 2), Math.Round(tilesPerSec, 1), tiles, w, h, null);
    }

    /// <summary>Infer the model dtype from the artifact filename
    /// (<c>*_v68_int16.bin</c> etc.). Empty when no recognised tag.</summary>
    internal static string PrecisionFromName(string? path) {
        var f = (path ?? "").ToLowerInvariant();
        if (f.Contains("int16")) return "int16";
        if (f.Contains("int8")) return "int8";
        if (f.Contains("fp16")) return "fp16";
        return "";
    }

    /// <summary>Deterministic synthetic star field: uniform background +
    /// a jittered grid of round Gaussian stars. Same (seed, dx, dy) always
    /// yields identical pixels so the workload is repeatable across
    /// devices and runs. dx/dy shift every star, used to give the stacker
    /// a real translation to recover.</summary>
    internal static ushort[] GenerateStarField(int w, int h, int seed, int dx, int dy) {
        const ushort bg = 200;
        var data = new ushort[w * h];
        for (int i = 0; i < data.Length; i++) data[i] = bg;

        var rng = new Random(seed);
        const int cols = 24, rows = 24;
        for (int gy = 1; gy < rows; gy++) {
            for (int gx = 1; gx < cols; gx++) {
                double cx = (double)gx * w / cols + dx + (rng.NextDouble() - 0.5) * 3;
                double cy = (double)gy * h / rows + dy + (rng.NextDouble() - 0.5) * 3;
                double sigma = 1.5 + rng.NextDouble() * 1.5;
                double amp = 3000 + rng.NextDouble() * 20000;
                PlantStar(data, w, h, cx, cy, sigma, amp, bg);
            }
        }
        return data;
    }

    /// <summary>Adds a round 2D Gaussian to the buffer over a small window
    /// (ported from the FrameAnalysis test fixture). Clamps to ushort.</summary>
    private static void PlantStar(ushort[] data, int w, int h,
                                  double cx, double cy, double sigma, double amp, ushort bg) {
        int radius = (int)Math.Ceiling(sigma * 3);
        int x0 = Math.Max(0, (int)cx - radius);
        int x1 = Math.Min(w - 1, (int)cx + radius);
        int y0 = Math.Max(0, (int)cy - radius);
        int y1 = Math.Min(h - 1, (int)cy + radius);
        double twoSigma2 = 2 * sigma * sigma;
        for (int y = y0; y <= y1; y++) {
            for (int x = x0; x <= x1; x++) {
                double dx = x - cx, dy = y - cy;
                double g = amp * Math.Exp(-(dx * dx + dy * dy) / twoSigma2);
                int idx = y * w + x;
                double v = data[idx] + g;
                data[idx] = v >= 65535 ? (ushort)65535 : (ushort)v;
            }
        }
    }
}
