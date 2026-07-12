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
using System.Runtime;
using System.Threading.Tasks;
using NINA.Core.Enum;
using NINA.Image.Gpu;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Image.FileFormat.FITS;
using NINA.Polaris.Services.External;

namespace NINA.Polaris.Services;

/// <summary>Async handler invoked once per integrated frame. Handlers
/// run sequentially inside the caller's await chain, a long-running
/// handler (e.g. an auto-focus run) naturally pauses the next capture
/// because the caller is awaiting AddFrameAsync. This is the
/// LiveStackTriggersService integration point (LSTR-1).</summary>
public delegate Task LiveStackFrameHandler(LiveStackFrameInfo info);

public record LiveStackFrameInfo(
    int FrameCount,        // count AFTER this integration
    IImageData Frame,      // the raw frame integrated (not the running stack)
    double MedianHfr,      // median HFR of stars detected in this frame
    int StarCount,
    DateTime At,
    double FrameSnr = 0,       // background SNR of the incoming frame
    double CumulativeSnr = 0); // SNR of the running-mean accumulator

/// <summary>
/// Where the per-frame stacking math runs.
/// <list type="bullet">
/// <item><b>Full</b> (default): the server runs the whole pipeline,
/// StarDetector + StarMatcher + AffineTransform + ImageResampler +
/// running-mean accumulator. Server holds the accumulated stack and
/// pushes it as the live preview. This is the historical behaviour
/// and stays the safe fallback.</item>
/// <item><b>MetricsOnly</b>: the server still runs StarDetector (so
/// the trigger orchestrator gets HFR/star count + the reference solve
/// on frame 1 still happens), but skips matching/warping/accumulating.
/// The raw frame is still relayed to clients via ImageRelayService;
/// a client-side WASM module is expected to do the actual stacking
/// and render its own preview. Used by the CLST offloading work,
/// see plan file.</item>
/// </list>
/// </summary>
public enum StackMode {
    Full,
    MetricsOnly
}

public class LiveStackingService {
    private readonly ImageRelayService _relay;
    // Optional: null in unit tests that don't exercise SaveFramesToDisk.
    // Production DI always supplies it because both singletons are
    // registered in Program.cs and the service constructor resolves
    // strictly via the registered graph.
    private readonly ImageWriterService? _writer;
    private readonly ILogger<LiveStackingService> _logger;
    private IGpuCompute _gpu = new CpuGpuCompute();
    private readonly StarDetector _detector = new() { MaxStars = 200 };
    private readonly object _lock = new();

    private float[]? _stackBuffer;
    private int[]? _countBuffer;
    // Colour (OSC) accumulators, used only when ColorStacking is on AND
    // the session is Bayered. Each is a full-resolution plane; the running
    // mean divides by _countBuffer (shared, coverage is per-pixel). When
    // these are non-null the live preview is broadcast as an RGB JPEG.
    private float[]? _stackR, _stackG, _stackB;
    private bool _colorActive;
    private BayerPatternEnum _bayerPattern = BayerPatternEnum.None;
    // Last non-None Bayer pattern seen this session. Frames occasionally arrive
    // with BayerPattern=None when the driver transiently drops CCD_CFA; relaying
    // such a frame as mono makes the LIVE display flip colour->mono->colour. We
    // stamp the relayed (mono-branch) frame with this last-good pattern so the
    // client debayers consistently.
    private BayerPatternEnum _lastGoodBayer = BayerPatternEnum.None;
    // Bayer-dropout guard for the FIRST frame's colour decision. If the very
    // first frame of a colour session arrives with BayerPattern=None (a CFA
    // dropout), committing to mono here poisons the WHOLE session — every
    // subsequent frame stacks as grey until restart. Instead we DEFER: drop
    // the frame and wait for one that actually carries a pattern (or use the
    // per-rig override). Capped so a genuinely-mono camera that somehow has
    // colour stacking enabled still eventually stacks (in mono).
    private int _colorDeferrals;
    private const int MaxColorDeferrals = 30;
    private int _width;
    private int _height;
    // Last frame's bit depth + metadata, retained so SaveCurrentStack can
    // stamp the written master with the same camera/target/telescope
    // headers the live frames carried (and pick the right BITPIX).
    private int _lastBitDepth = 16;
    private ImageMetaData? _lastMetaData;
    private int _frameCount;
    private int _framesSavedToDisk;
    private List<DetectedStar>? _referenceStars;

    // ---- Meridian flip (Part B) ---------------------------------
    // The accumulator stays in the REFERENCE orientation. After a GEM
    // meridian flip, incoming frames arrive ~180-deg rotated; we detect
    // that and warp them back onto the reference grid so the stack keeps
    // growing without ghosting.
    //
    // _flipped tracks the orientation incoming frames are currently in
    // relative to the reference (true = arriving 180-deg rotated). Once
    // set we probe that orientation first to avoid a wasted match.
    private bool _flipped;
    // Pier side captured at the reference frame. A later frame reporting a
    // different pier side is a proactive hint that the next frame is
    // flipped (B2) -- purely an optimisation, B1's auto-detect is the
    // guarantee.
    private PierSide _referencePier = PierSide.pierUnknown;
    /// <summary>Count of meridian-flip orientation changes the stacker
    /// re-oriented and kept stacking through during the current session.
    /// Surfaced on the WS status payload + LIVE tab. Reset in
    /// <see cref="Reset"/>.</summary>
    public int MeridianFlipsHandled { get; private set; }
    // Default: stacking is OFF. The session comes up disarmed so frames
    // flow through the relay (and are saved when SaveFramesToDisk is on)
    // without silently integrating into a stack the user never asked
    // for. The operator explicitly arms it from the LIVE tab via
    // Start() / Resume() when they want stacking.
    private bool _isRunning = false;
    private DateTime? _startedAt;
    // Integration-time stopwatch. ElapsedSeconds must reflect the time
    // spent ACTIVELY stacking, not raw wall-clock since the first frame:
    // a stopped/paused stack (or one past the duration cap) has to FREEZE
    // (field report — the "Total integration time" counter kept climbing
    // after Stop). _elapsedAccrued banks completed running segments;
    // _elapsedSegmentStart marks the start of the segment currently
    // running (null while paused/stopped). Reset() clears both.
    private TimeSpan _elapsedAccrued = TimeSpan.Zero;
    private DateTime? _elapsedSegmentStart;

    /// <summary>When true, every raw frame received via
    /// <see cref="AddFrameAsync"/> is also persisted to disk via
    /// <see cref="ImageWriterService.SaveImage"/> with imageType
    /// "LIGHT", landing in {rig}/lights/{target}/{filter}/{date}
    /// like a regular sequence capture. Default ON — most users
    /// want both the integrated preview AND an archive of the raw
    /// frames so they can re-stack offline in Siril / PixInsight
    /// later. UI checkbox in LIVE tab persists the choice per-rig
    /// via PUT /api/livestack/save-frames.</summary>
    public bool SaveFramesToDisk { get; set; } = true;

    /// <summary>Per-rig opt-in: stack OSC frames in colour. When on AND the
    /// incoming frames are Bayered, each frame is debayered to RGB, aligned
    /// per-channel and integrated into 3 accumulators; the live preview is a
    /// colour JPEG. OFF (default) keeps the historical mono-CFA stack (client
    /// debayers for display). Set via PUT /api/livestack/color, persisted in
    /// the active rig's LiveStackColor field; loaded on rig change in
    /// Program.cs. Mono cameras ignore it.</summary>
    public bool ColorStacking { get; set; } = false;

    /// <summary>True once the current session is integrating in colour
    /// (ColorStacking + a Bayered reference frame). Drives the colour
    /// broadcast + colour save path.</summary>
    public bool ColorActive { get { lock (_lock) { return _colorActive; } } }

    /// <summary>Per-rig opt-in: per-pixel kappa-sigma outlier rejection on the
    /// live stack. When on, each incoming sample is compared to the pixel's
    /// running mean +/- <see cref="SigmaKappa"/> * sigma (Welford, over the
    /// already-accepted samples); samples past that are dropped instead of
    /// folded in, so cosmic rays, plane/satellite trails and dithered hot
    /// pixels stay out of the integration. OFF (default) keeps the plain
    /// running-mean accumulate (GPU fast path). Pays off most WITH dithering.
    /// Set via PUT /api/livestack/sigma-rejection, persisted per-rig.</summary>
    public bool SigmaRejection { get; set; } = false;

    /// <summary>Rejection threshold in sigmas (default 3.0). Lower = more
    /// aggressive clipping.</summary>
    public double SigmaKappa { get; set; } = 3.0;

    // Frames must build a spread estimate before any sample can be rejected,
    // so the first few always seed each pixel's statistics.
    private const int SigmaMinFrames = 5;

    // Welford M2 (sum of squared deviations) per pixel, allocated only while
    // SigmaRejection is on. Mono uses _stackBuffer as the running sum; colour
    // keeps a separate luminance running sum (_lumSumBuffer) since the three
    // channel accumulators aren't a luminance.
    private float[]? _m2Buffer;
    private float[]? _lumSumBuffer;

    // MEMOPT: session-scoped scratch buffers for the per-frame transients
    // that never leave AddFrameAsync (calibrated frame, debayered planes,
    // warped planes, SNR reconstruction). Frame geometry is constant within
    // a session, so reusing these instead of new[]-ing per frame removes
    // ~150 MB of large-object-heap churn PER FRAME on a 9 MP OSC camera —
    // the churn (not the accumulators) was what ballooned RSS to ~1 GB.
    // Only buffers that are consumed inside AddFrameAsync may live here;
    // anything handed to the relay/writer escapes and must stay per-frame.
    // All are lazily sized on first use and nulled in Reset().
    private ushort[]? _scratchCal;
    private ushort[]? _dbR, _dbG, _dbB;
    private ushort[]? _warpR, _warpG, _warpB, _warpMono;
    private ushort[]? _scratchSnr;

    private static void EnsureScratch(ref ushort[]? buf, int length) {
        if (buf == null || buf.Length != length) buf = new ushort[length];
    }

    /// <summary>Count of individual pixel samples rejected as outliers during
    /// the current session (diagnostic; surfaced on the status payload).</summary>
    public long RejectedPixels { get; private set; }

    /// <summary>When > 0, stacking auto-pauses after this many
    /// seconds elapsed since the first frame of the current stack
    /// (i.e. since the last Reset). 0 = run indefinitely. Frames
    /// arriving past the cap are still relayed to clients + saved
    /// to disk (when <see cref="SaveFramesToDisk"/> is on), but
    /// don't update the running mean. Reset clears the timer too.
    /// Set via PUT /api/livestack/max-duration.</summary>
    public int MaxDurationSeconds { get; set; }

    /// <summary>When the current stack started (first frame after
    /// the most recent Reset). Null when no frame has been
    /// integrated yet. Used to drive the elapsed counter shown in
    /// the LIVE tab and the auto-pause check against
    /// <see cref="MaxDurationSeconds"/>.</summary>
    public DateTime? StartedAt => _startedAt;

    /// <summary>Seconds of ACTIVE integration for the current stack:
    /// banked running segments plus the live segment if stacking is
    /// currently running. Freezes while stopped/paused and past the
    /// duration cap. 0 when no frame has been integrated yet.</summary>
    public double ElapsedSeconds {
        get {
            var accrued = _elapsedAccrued;
            if (_elapsedSegmentStart is { } s) accrued += DateTime.UtcNow - s;
            return accrued.TotalSeconds;
        }
    }

    /// <summary>True when <see cref="MaxDurationSeconds"/> is set and
    /// the elapsed time has crossed it. UI uses this to render a
    /// "complete" badge instead of "running" once the cap fires.</summary>
    public bool DurationCapReached =>
        MaxDurationSeconds > 0 && ElapsedSeconds >= MaxDurationSeconds;

    /// <summary>Counter of frames actually written to disk during
    /// the current live-stack session. Resets along with
    /// <see cref="FrameCount"/> in <see cref="Reset"/>. Exposed on
    /// the status payload so the UI can show "12 saved" next to
    /// the toggle as live confirmation that the writes are landing.</summary>
    public int FramesSavedToDisk => _framesSavedToDisk;

    /// <summary>True when the user asked to keep frames (<see cref="SaveFramesToDisk"/>)
    /// but no image output folder is configured, so every save silently no-ops.
    /// Surfaced on the status payload so the LIVE tab can warn instead of the
    /// user discovering an empty lights/ folder after a whole session.</summary>
    public bool SaveFramesNoOutputDir =>
        SaveFramesToDisk && _writer != null && !_writer.HasOutputDir;

    /// <summary>Persist one frame to disk if the user enabled it. Centralises
    /// the save so both the stacking path (<see cref="AddFrameAsync"/>) and the
    /// server LIVE loop's non-stacking branch archive frames identically.
    /// No-op (and harmless) when saving is off or no writer is wired.</summary>
    public void SaveFrameIfEnabled(IImageData imageData) {
        if (!SaveFramesToDisk || _writer == null) return;
        try {
            var savedPath = _writer.SaveImage(imageData, imageType: "LIGHT");
            if (savedPath != null) {
                Interlocked.Increment(ref _framesSavedToDisk);
                _logger.LogDebug("Live stack: saved frame to {Path}", savedPath);
            }
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Live stack: failed to save frame to disk");
        }
    }

    // Frame-integrated subscribers (LSTR-1). Append-only list guarded
    // by _handlersLock for snapshotting; handlers awaited sequentially
    // inside AddFrameAsync so a slow handler (AF run, recenter) blocks
    // the caller and naturally pauses the next capture.
    private readonly List<LiveStackFrameHandler> _frameHandlers = new();
    private readonly object _handlersLock = new();

    public bool IsRunning => _isRunning;
    public int FrameCount => _frameCount;
    public int Width => _width;
    public int Height => _height;

    /// <summary>True while a frame is actively being detected / aligned /
    /// integrated (the stacking math is running). Surfaced so the UI can show a
    /// "Stacking…" indicator instead of leaving the operator guessing whether
    /// anything is happening between frames.</summary>
    private volatile bool _isStacking;
    public bool IsStacking => _isStacking;

    /// <summary>Why the most recent rejected frame was dropped (alignment failed,
    /// size mismatch, meridian flip in progress, …), null until one is rejected.
    /// Frames are silently skipped otherwise — this makes the reason visible.</summary>
    public string? LastRejectReason { get; private set; }
    /// <summary>UTC time of the last rejected frame (null until one happens).</summary>
    public DateTime? LastRejectAt { get; private set; }
    /// <summary>How many frames were dropped this session (not integrated).</summary>
    public int RejectedFrames { get; private set; }

    public double LastFrameMedianHfr { get; private set; }
    public int LastFrameStarCount { get; private set; }
    // SNR-4: background SNR per-frame + cumulative-stack. CumulativeSnr
    // is the headline number in the LIVE-tab "stack quality" widget —
    // it's the SNR of the running-mean accumulator, growing ~√N as
    // frames stack.
    public double LastFrameSnr { get; private set; }
    public double CumulativeSnr { get; private set; }
    // Plain mean of the latest incoming sub, surfaced for the LIVE-tab
    // "Mean" readout. Populated every frame regardless of mono/colour or
    // compute mode (the client stats bar used to only get this from the
    // retired client-side capture loop, so it read blank in server-owned live).
    public double LastFrameMean { get; private set; }

    // 16-bit luminance histogram + stats of the latest colour stack, surfaced
    // over the WS status so the LIVE histogram panel shows the real 16-bit data
    // even though the colour frame is broadcast as an 8-bit JPEG. Null until a
    // colour frame has been integrated; bins span 0..65535 in 256 buckets.
    public int[]? ColorHistogram { get; private set; }
    public int ColorHistMin { get; private set; }
    public int ColorHistMax { get; private set; }
    public double ColorHistMean { get; private set; }
    public double ColorHistStd { get; private set; }

    /// <summary>Build the 256-bin 16-bit luminance histogram + min/max/mean/std
    /// of a planar RGB stack (subsampled on big sensors). Cheap; runs once per
    /// integrated colour frame, off the relay's broadcast.</summary>
    private void ComputeColorHistogram(ushort[] rgb, int w, int h) {
        int plane = w * h;
        if (rgb.Length < plane * 3 || plane == 0) { ColorHistogram = null; return; }
        const int NB = 256;
        var bins = new int[NB];
        int mn = 65535, mx = 0; double sum = 0, sumSq = 0; long cnt = 0;
        int step = Math.Max(1, plane / 300_000);
        for (int i = 0; i < plane; i += step) {
            int lum = (int)(rgb[i] * 0.299 + rgb[plane + i] * 0.587 + rgb[2 * plane + i] * 0.114);
            if (lum < 0) lum = 0; else if (lum > 65535) lum = 65535;
            if (lum < mn) mn = lum;
            if (lum > mx) mx = lum;
            sum += lum; sumSq += (double)lum * lum; cnt++;
            bins[lum * (NB - 1) / 65535]++;
        }
        var mean = cnt > 0 ? sum / cnt : 0;
        ColorHistogram = bins;
        ColorHistMin = cnt > 0 ? mn : 0;
        ColorHistMax = mx;
        ColorHistMean = mean;
        ColorHistStd = cnt > 0 ? Math.Sqrt(Math.Max(0, sumSq / cnt - mean * mean)) : 0;
    }
    /// <summary>Rolling history of (frameCount, cumulativeSnr) used
    /// by <see cref="SnrEtaCalculator"/> to fit the √N model + ETA.
    /// Capped at 50 entries — beyond that the fit is dominated by
    /// recent samples anyway and we'd just be paying memory for
    /// nothing.</summary>
    public IReadOnlyList<(int frame, double snr)> SnrHistory => _snrHistory;
    private readonly List<(int frame, double snr)> _snrHistory = new(50);
    /// <summary>Cached last ETA result. Recomputed each AddFrame so
    /// the WS broadcaster can serve it without re-fitting.</summary>
    public SnrEtaCalculator.EtaResult? LastEta { get; private set; }

    /// <summary>Where the per-frame math runs. Default <see cref="StackMode.Full"/>.
    /// Switched to <see cref="StackMode.MetricsOnly"/> by the WASM
    /// handshake (CLST-5) when a WASM-capable client is connected and
    /// the active rig hasn't forced server-side.</summary>
    public StackMode Mode { get; set; } = StackMode.Full;

    // LSPP-3+4: per-frame pre-processing. Settings read from the active
    // rig on every frame so live toggles take effect without a restart.
    // PreProcessor is the singleton (Program.cs); null in unit-test
    // doubles -- splice is no-op when null. Status fields broadcast via
    // the WS payload so the LIVE-tab UI can show counters in real time.
    private readonly LiveStackPreProcessor? _preProcessor;
    private readonly ProfileService? _profiles;
    // Part B: optional in unit tests (the doubles construct without DI).
    // _equipment provides the pier-side hint; _meridian lets us pause
    // integration while a flip slew is in progress. Both null -> the
    // alignment-based auto-detect (B1) still handles a flip on its own.
    private readonly EquipmentManager? _equipment;
    private readonly MeridianFlipService? _meridian;
    // Optional GraXpert backend for server-side BGE in Full stack mode.
    private readonly GraXpertService? _graxpert;
    // Latches when GraXpert (CLI + NPU) is absent so we stop retrying BGE
    // every frame and don't spam the log; reset on Reset().
    private bool _serverBgeUnavailable;
    public LiveStackPreProcStatus PreProcStatus { get; } = new();

    /// <summary>
    /// Whether BGE can run with the current setup — computed live (so the LIVE
    /// settings panel reflects it even while idle, not just mid-stack). True
    /// when the stack is computed client-side (the browser runs GraXpert ONNX
    /// over WASM cpu/gpu, MetricsOnly) OR a GraXpert backend is present on the
    /// host (CLI or RK3588 NPU) for server-side stacking. Only the genuinely
    /// impossible case — server-side stacking on a host with no GraXpert at all
    /// — reports false.
    /// </summary>
    public bool BgeSupported {
        get {
            if (Mode == StackMode.MetricsOnly) return true;   // client-side ONNX (WASM)
            return _graxpert != null
                   && (_graxpert.IsAvailable || _graxpert.NpuAvailable)
                   && !_serverBgeUnavailable;                 // host CLI / NPU
        }
    }

    public LiveStackingService(ImageRelayService relay,
                                ILogger<LiveStackingService> logger,
                                ImageWriterService? writer = null,
                                ProfileService? profiles = null,
                                LiveStackPreProcessor? preProcessor = null,
                                EquipmentManager? equipment = null,
                                MeridianFlipService? meridian = null,
                                IGpuCompute? gpu = null,
                                GraXpertService? graxpert = null) {
        _relay = relay;
        _writer = writer;
        _logger = logger;
        _profiles = profiles;
        _preProcessor = preProcessor;
        _equipment = equipment;
        _meridian = meridian;
        _graxpert = graxpert;
        // GPU compute is optional; null (and the test doubles) get the CPU path.
        _gpu = gpu ?? new CpuGpuCompute();
        // SNR-3: keep TargetSnr aligned with the active rig until the
        // user explicitly overrides via /api/livestack/target-snr.
        // ProfileService is optional in the ctor so the existing test
        // doubles (which instantiate without DI) keep working.
        if (profiles != null) {
            TargetSnr = profiles.ActiveEquipmentProfile?.TargetSnr;
            profiles.EquipmentProfileActivated += rig => {
                // Refresh only if no override is in place — the user's
                // session-level number sticks until they clear it.
                if (_targetSnrOverride == null) TargetSnr = rig?.TargetSnr;
                // LSPP-3: switching rigs invalidates the master cache
                // (different rig = different gain/binning likely).
                _preProcessor?.Reset();
                PreProcStatus.Reset();
            };
        }
    }
    /// <summary>Run GraXpert background extraction on one live-stack frame
    /// (Full mode). Round-trips through a temp FITS because the GraXpert
    /// backends (CLI + RK3588 NPU) work on files. Returns the BGE'd pixels, or
    /// the input unchanged on any failure. Latches <see cref="_serverBgeUnavailable"/>
    /// when no backend is installed so we don't retry + log every frame.</summary>
    private async Task<ushort[]> ApplyServerBgeAsync(ushort[] data, ImageProperties props,
            ImageMetaData meta, LiveStackPreProcSettings s, CancellationToken ct) {
        var tmpIn = Path.Combine(Path.GetTempPath(), $"polaris_lsbge_{Guid.NewGuid():N}.fits");
        string? tmpOut = null;
        try {
            FITSWriter.Write(new BaseImageData(data, props, meta), tmpIn);
            var opts = new GraXpertOptions(
                Operation: GraXpertOperation.BackgroundExtraction,
                Correction: string.IsNullOrWhiteSpace(s.BgeCorrection) ? "Subtraction" : s.BgeCorrection,
                Smoothing: s.BgeSmoothing,
                UseNpu: true);
            var res = await _graxpert!.ProcessFrameAsync(tmpIn, opts, ct);
            if (res.Error != null || string.IsNullOrEmpty(res.OutputPath) || !File.Exists(res.OutputPath)) {
                if (res.Error != null && res.Error.Contains("not installed", StringComparison.OrdinalIgnoreCase)) {
                    _serverBgeUnavailable = true;
                    _logger.LogWarning("Live-stack server BGE unavailable (no GraXpert CLI / NPU); "
                        + "disabling for this session. Install GraXpert on the host or use client-side stacking.");
                } else {
                    _logger.LogWarning("Live-stack server BGE failed for frame {N}: {Err}",
                        _frameCount + 1, res.Error);
                }
                PreProcStatus.RecordServerBge(ok: false, error: res.Error);
                return data;
            }
            tmpOut = res.OutputPath;
            BaseImageData outImg;
            using (var fs = File.OpenRead(tmpOut)) outImg = FITSReader.Read(fs);
            if (outImg?.Data != null
                    && outImg.Properties.Width == props.Width
                    && outImg.Properties.Height == props.Height) {
                PreProcStatus.RecordServerBge(ok: true, error: null);
                return outImg.Data;
            }
            PreProcStatus.RecordServerBge(ok: false, error: "BGE output dimensions mismatch");
            return data;
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Live-stack server BGE error on frame {N}", _frameCount + 1);
            PreProcStatus.RecordServerBge(ok: false, error: ex.Message);
            return data;
        } finally {
            try { File.Delete(tmpIn); } catch { }
            try { if (tmpOut != null) File.Delete(tmpOut); } catch { }
        }
    }

    private double? _targetSnrOverride;
    /// <summary>Called by the /api/livestack/target-snr endpoint to
    /// distinguish a session override from a rig-default refresh.</summary>
    public void SetTargetSnrOverride(double? value) {
        _targetSnrOverride = value;
        TargetSnr = value;
        RecomputeEta();
    }

    /// <summary>Subscribe to per-frame integration events. Handlers
    /// are awaited sequentially inside <see cref="AddFrameAsync"/>;
    /// a slow handler pauses the upstream capture loop. Returns an
    /// IDisposable that removes the subscription.</summary>
    public IDisposable SubscribeFrameIntegrated(LiveStackFrameHandler handler) {
        lock (_handlersLock) _frameHandlers.Add(handler);
        return new HandlerSub(this, handler);
    }

    private sealed class HandlerSub : IDisposable {
        private readonly LiveStackingService _svc;
        private readonly LiveStackFrameHandler _h;
        public HandlerSub(LiveStackingService svc, LiveStackFrameHandler h) { _svc = svc; _h = h; }
        public void Dispose() {
            lock (_svc._handlersLock) _svc._frameHandlers.Remove(_h);
        }
    }

    /// <summary>Clear the accumulator + reference + counters and
    /// start a fresh stack on the next incoming frame. Does NOT
    /// flip IsRunning off — stacking stays armed and the new
    /// stack begins immediately when the next frame arrives. Used
    /// when the user switches targets and wants to start over.</summary>
    public void Reset() {
        lock (_lock) {
            _stackBuffer = null;
            _countBuffer = null;
            _stackR = null;
            _stackG = null;
            _stackB = null;
            _m2Buffer = null;
            _lumSumBuffer = null;
            _scratchCal = null;
            _dbR = null; _dbG = null; _dbB = null;
            _warpR = null; _warpG = null; _warpB = null; _warpMono = null;
            _scratchSnr = null;
            RejectedPixels = 0;
            _colorActive = false;
            _bayerPattern = BayerPatternEnum.None;
            _lastGoodBayer = BayerPatternEnum.None;
            _colorDeferrals = 0;
            _referenceStars = null;
            _flipped = false;
            _referencePier = PierSide.pierUnknown;
            MeridianFlipsHandled = 0;
            _frameCount = 0;
            _framesSavedToDisk = 0;
            _width = 0;
            _height = 0;
            _lastMetaData = null;
            _lastBitDepth = 16;
            _startedAt = null;
            _elapsedAccrued = TimeSpan.Zero;
            _elapsedSegmentStart = null;
            LastFrameMedianHfr = 0;
            LastFrameStarCount = 0;
            LastFrameSnr = 0;
            CumulativeSnr = 0;
            LastFrameMean = 0;
            RejectedFrames = 0;
            LastRejectReason = null;
            LastRejectAt = null;
            _snrHistory.Clear();
            LastEta = null;
            // LSPP-3+4: target switch -> drop the master cache so the
            // next frame re-resolves with the new filter/exposure/gain.
            _preProcessor?.Reset();
            PreProcStatus.Reset();
            _serverBgeUnavailable = false;   // re-probe BGE backend next session
            _logger.LogInformation("Live stacking reset");
        }
        // MEMOPT: a session just released ~300+ MB of accumulators, scratch
        // and master-cache LOH arrays. Without an explicit compacting
        // collection the freed segments linger as fragmented LOH and RSS
        // never comes down on the SBC. Reset is a user-paced action
        // (target switch / stop), so a one-off blocking full GC here is
        // invisible; NEVER do this per frame. Outside the lock so a
        // concurrent frame isn't stalled behind the collection.
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    /// <summary>Pick the Bayer pattern to lock for a whole colour session,
    /// preferring dropout-proof sources over the per-frame CFA:
    ///   1. The frame's own <c>props.BayerPattern</c> if it carries one.
    ///   2. The per-rig BayerPatternOverride (camera-quirks map, else the
    ///      legacy per-rig field) — user-set, so it never drops out.
    /// Returns None when nothing resolves (mono camera, or an OSC frame-0
    /// CFA dropout with no override — the caller then DEFERS rather than
    /// commit the session to mono). Mirrors ImageRelayService's override
    /// resolution so the stack and the raw relay agree on the pattern.</summary>
    private BayerPatternEnum ResolveSessionBayer(ImageProperties props) {
        if (props.BayerPattern != BayerPatternEnum.None
                && props.BayerPattern != BayerPatternEnum.Auto) {
            return props.BayerPattern;
        }
        var raw = _profiles?.GetActiveCameraQuirks()?.BayerPatternOverride;
        if (string.IsNullOrWhiteSpace(raw))
            raw = _profiles?.ActiveEquipmentProfile?.BayerPatternOverride;
        if (!string.IsNullOrWhiteSpace(raw)
                && !string.Equals(raw, "Auto", StringComparison.OrdinalIgnoreCase)
                && Enum.TryParse<BayerPatternEnum>(raw, ignoreCase: true, out var p)
                && p != BayerPatternEnum.None
                && p != BayerPatternEnum.Auto) {
            return p;
        }
        return BayerPatternEnum.None;
    }

    /// <summary>Arm stacking AND clear the current accumulator. The
    /// "fresh start" path — use when the operator wants to begin a
    /// new target / discard whatever was stacked before. Prefer
    /// <see cref="Resume"/> when the operator paused mid-session
    /// and wants to keep building on the existing stack.</summary>
    public void Start() {
        Reset();
        _isRunning = true;
        // Segment starts when the first frame actually integrates
        // (BeginElapsedSegment in the AddFrame first-frame path), so
        // the counter reflects real integration, not arm-to-first-frame
        // dead time. Reset() already cleared the accumulator.
        _logger.LogInformation("Live stacking started (buffer reset)");
    }

    /// <summary>Begin (or resume) accruing integration time from now.
    /// No-op if a segment is already running. Called on the first
    /// integrated frame and on Resume(). Lock-guarded (Monitor is
    /// reentrant, so callers already holding _lock are fine).</summary>
    private void BeginElapsedSegment() {
        lock (_lock) _elapsedSegmentStart ??= DateTime.UtcNow;
    }

    /// <summary>Bank the running segment and stop accruing. Freezes
    /// ElapsedSeconds. Called on Stop() and when the duration cap
    /// fires. No-op if nothing is running. Lock-guarded so it can't
    /// race with a concurrent freeze on the frame path.</summary>
    private void FreezeElapsedSegment() {
        lock (_lock) {
            if (_elapsedSegmentStart is { } s) {
                _elapsedAccrued += DateTime.UtcNow - s;
                _elapsedSegmentStart = null;
            }
        }
    }

    /// <summary>Re-arm stacking WITHOUT clearing the accumulator. New
    /// frames continue to integrate into the running mean. Lets the
    /// operator pause (e.g. clouds rolled in), fix things, then pick
    /// up where they left off — typical workflow when wifi drops or
    /// you need to disconnect the laptop for a minute.</summary>
    public void Resume() {
        if (_frameCount == 0) {
            // Nothing to resume FROM — fall through to Start so the
            // first frame still establishes the reference. Avoids a
            // confusing state where Resume succeeds but the next
            // frame can't align to an empty buffer.
            Start();
            return;
        }
        _isRunning = true;
        // Resume accruing integration time from now (banks stay from the
        // earlier running segment(s)).
        BeginElapsedSegment();
        _logger.LogInformation("Live stacking resumed at {Count} frames", _frameCount);
    }

    /// <summary>Disarm stacking. Frames still flow through the relay
    /// + per-frame save path but no longer update the running mean.
    /// Pair with <see cref="Resume"/> to continue or
    /// <see cref="Start"/> to begin a new stack.</summary>
    public void Stop() {
        _isRunning = false;
        // Freeze the integration-time counter — a stopped stack must
        // not keep climbing (field report).
        FreezeElapsedSegment();
        _logger.LogInformation("Live stacking stopped after {Count} frames", _frameCount);
    }

    /// <summary>Record that a frame was dropped (not integrated) with the reason,
    /// so it surfaces in the WS status + LIVE tab instead of vanishing silently.</summary>
    private void RecordReject(string reason) {
        LastRejectReason = reason;
        LastRejectAt = DateTime.UtcNow;
        RejectedFrames++;
        _logger.LogInformation("Live stack: frame rejected — {Reason} (total dropped {N})",
            reason, RejectedFrames);
    }

    public async Task AddFrameAsync(IImageData imageData, CancellationToken ct = default) {
        // Disk persistence runs INDEPENDENTLY of whether the stacker
        // is currently armed and INDEPENDENTLY of whether the
        // duration cap was reached — the user opted to keep raw
        // frames, so we should keep ALL of them. Stacking math
        // below short-circuits when disarmed / past cap, but the
        // archive doesn't.
        SaveFrameIfEnabled(imageData);

        if (!_isRunning) return;

        // Duration cap. Once the elapsed time crosses
        // MaxDurationSeconds, stop touching the accumulator —
        // further frames are saved to disk (above) and relayed to
        // clients, but the stacked preview holds steady at the
        // master that completed at the cap. Reset clears _startedAt
        // and the timer restarts on the next frame.
        if (DurationCapReached) {
            // Freeze the integration counter at the cap — past-cap frames
            // are saved/relayed but don't integrate, so they must not
            // advance the "total integration time" either.
            FreezeElapsedSegment();
            _logger.LogDebug("Live stack: duration cap reached ({Cap}s), skipping accumulation",
                MaxDurationSeconds);
            return;
        }

        // Part B3: while a meridian flip is running the mount is slewing
        // and settling, so any frame captured now is trailed/blurred.
        // Skip integration until the flip returns to Idle -- the frame is
        // still saved to disk (above) and the first good frame afterwards
        // is re-oriented by the alignment probe in B1.
        if (_meridian != null && _meridian.State != MeridianFlipState.Idle) {
            RecordReject($"meridian flip in progress ({_meridian.State})");
            return;
        }

        var props = imageData.Properties;
        var data = imageData.Data;

        var mode = Mode;
        _logger.LogInformation("Live stack: processing frame {N} ({W}x{H}), mode={Mode}",
            _frameCount + 1, props.Width, props.Height, mode);

        // Mark the stacking math as active for the whole detect/align/integrate
        // pass (cleared in the finally below, even on a reject or throw) so the
        // UI can show a "Stacking…" indicator.
        _isStacking = true;
        try {

        // LSPP-4: per-frame pre-processing splice. Calibration runs
        // here on the server (or via the client when MetricsOnly is
        // chosen by the server-side stack -- either way the pixels
        // we feed into StarDetector below are the calibrated ones).
        // BGE is handled client-side ONLY (MetricsOnly) so the server
        // just tracks supportedThisSession for the WS payload.
        var preProcSettings = _profiles?.ActiveEquipmentProfile?.LiveStackPreProcessing
                              ?? new LiveStackPreProcSettings();
        // Keep the stored flag in sync during stacking; BgeSupported is the
        // live source of truth (also valid while idle) the WS payload reads.
        PreProcStatus.BgeSupportedThisSession = BgeSupported;
        if (preProcSettings.CalibrationEnabled && _preProcessor != null) {
            // MEMOPT: calibrated pixels never escape AddFrameAsync (the relay
            // retains the RAW frame, the writer saves the RAW frame), so the
            // calibrated copy is written into session scratch instead of a
            // fresh ~18 MB array per frame.
            EnsureScratch(ref _scratchCal, imageData.Data.Length);
            var res = await _preProcessor.ApplyAsync(imageData, preProcSettings, _scratchCal, ct);
            if (res.Success && (res.MasterDarkUsed != null
                                || res.MasterFlatUsed != null
                                || res.MasterBiasUsed != null)) {
                // Calibration applied successfully -- swap in the
                // calibrated pixels for the rest of the pipeline.
                data = res.Pixels;
                PreProcStatus.RecordCalibrationApplied(res);
            } else if (!res.Success) {
                // Math threw / master corrupted -- fall back to raw
                // pixels so the session continues. Operator sees the
                // counter increment via WS, and the warning lands in
                // the debug log via the helper.
                _logger.LogWarning(
                    "Live-stack calibration failed for frame {N}: {Err}",
                    _frameCount + 1, res.Error);
                PreProcStatus.RecordCalibrationFallback(res.Error);
            } else {
                // Success but no masters matched (auto-match empty).
                // Don't penalise the counter as fallback -- nothing
                // went wrong, there just wasn't anything to apply.
                PreProcStatus.RecordCalibrationNoMatch();
            }
        }

        // Server-side BGE — Full (server-stacked) mode only. The client WASM
        // path already does BGE in MetricsOnly mode; this covers the case where
        // the SBC integrates the stack, so a Pi/SBC session still gets gradient
        // removal. One BGE per exposure (GraXpert CLI, or the RK3588 NPU when
        // present) is cheap at capture cadence. Honours the same BgeEnabled
        // toggle; fully graceful (any failure feeds the un-BGE'd frame).
        if (mode == StackMode.Full && preProcSettings.BgeEnabled
                && _graxpert != null && !_serverBgeUnavailable) {
            data = await ApplyServerBgeAsync(data, props, imageData.MetaData, preProcSettings, ct);
        }

        // StarDetector runs in BOTH modes:
        //   - Full: feeds StarMatcher for alignment + provides HFR
        //   - MetricsOnly: trigger orchestrator (LSTR-3) needs HFR +
        //     star count even when stacking happens client-side
        var stars = _detector.Detect(data, props.Width, props.Height);
        _logger.LogDebug("Detected {Count} stars in frame", stars.Count);

        if (mode == StackMode.Full) {
            ushort[] alignedData;
            // Transform that aligned THIS frame onto the reference grid
            // (null for the reference frame / identity). In colour mode we
            // apply it per-debayered-plane instead of warping the raw CFA.
            AffineTransform? usedTransform = null;

            lock (_lock) {
                if (_frameCount == 0) {
                    // Resolve the effective Bayer pattern for the whole
                    // session from the most reliable source: the frame's own
                    // CFA, else the per-rig override (dropout-proof — the user
                    // set it, it never disappears). See ResolveSessionBayer.
                    var effectivePattern = ResolveSessionBayer(props);
                    bool wantColour = ColorStacking;
                    bool haveUsablePattern = effectivePattern != BayerPatternEnum.None
                        && effectivePattern != BayerPatternEnum.Auto;

                    // Bayer-dropout DEFER: colour is wanted but the first
                    // frame carries no pattern and no override is set. Don't
                    // lock the session to mono on a transient CFA drop — skip
                    // this frame (LIVE keeps the last good frame) and retry on
                    // the next, which almost always carries the pattern. Cap
                    // it so a genuinely-mono setup with colour left on still
                    // proceeds (in mono) after a few seconds.
                    if (wantColour && !haveUsablePattern
                            && _colorDeferrals < MaxColorDeferrals) {
                        _colorDeferrals++;
                        _logger.LogWarning(
                            "Live stack: first frame has no Bayer pattern (CFA dropout) but colour is on — deferring init ({N}/{Max}) instead of falling back to mono",
                            _colorDeferrals, MaxColorDeferrals);
                        RecordReject("waiting for a Bayer pattern (CFA dropout on first frame)");
                        return;
                    }

                    // First frame: initialize buffers and set as reference.
                    // Stamp _startedAt (the "stack began" timestamp) and
                    // start accruing integration time. Reset clears both;
                    // the next first frame restarts the timer.
                    _startedAt = DateTime.UtcNow;
                    BeginElapsedSegment();
                    _width = props.Width;
                    _height = props.Height;
                    int pixelCount = _width * _height;
                    _countBuffer = new int[pixelCount];
                    _referenceStars = stars;
                    // Colour session? OSC frame + the per-rig toggle. Allocate
                    // the 3 plane accumulators once; the rest of the session
                    // debayers + integrates in colour. The mono accumulator is
                    // only allocated in the mono branch — a colour session
                    // never writes it, so allocating it there was ~35 MB of
                    // dead weight on a 9 MP sensor (MEMOPT).
                    _bayerPattern = effectivePattern;
                    _colorActive = wantColour && haveUsablePattern;
                    if (_colorActive) _lastGoodBayer = effectivePattern;
                    if (_colorActive) {
                        _stackR = new float[pixelCount];
                        _stackG = new float[pixelCount];
                        _stackB = new float[pixelCount];
                        _logger.LogInformation("Live stack: colour mode ON (pattern {P})", _bayerPattern);
                    } else {
                        _stackBuffer = new float[pixelCount];
                    }
                    // Kappa-sigma rejection buffers (opt-in). Mono reuses
                    // _stackBuffer as the running sum + one M2 buffer; colour
                    // needs a separate luminance running sum since the channel
                    // accumulators aren't a luminance. Allocated only when on.
                    if (SigmaRejection) {
                        _m2Buffer = new float[pixelCount];
                        if (_colorActive) _lumSumBuffer = new float[pixelCount];
                        _logger.LogInformation("Live stack: kappa-sigma rejection ON (k={K})", SigmaKappa);
                    }
                    // Part B2: remember the pier side at the reference so a
                    // later change can hint a flip before alignment proves it.
                    _referencePier = _equipment?.Telescope?.SideOfPier ?? PierSide.pierUnknown;
                    _flipped = false;
                    alignedData = data;
                } else {
                    if (props.Width != _width || props.Height != _height) {
                        RecordReject($"size mismatch {props.Width}x{props.Height} vs {_width}x{_height}");
                        return;
                    }

                    // Part B1+B2: orientation-aware alignment. Probe the
                    // orientation we expect first (the one we last matched,
                    // or "flipped" when the pier side changed), then the
                    // other. The reference accumulator never rotates -- a
                    // post-flip frame is warped back onto it.
                    var curPier = _equipment?.Telescope?.SideOfPier ?? PierSide.pierUnknown;
                    bool pierFlipHint = _referencePier != PierSide.pierUnknown
                        && curPier != PierSide.pierUnknown
                        && curPier != _referencePier;
                    // Probe the flipped orientation first when either the
                    // pier hint says so or we're already tracking a flip.
                    bool flippedFirst = _flipped || pierFlipHint;

                    alignedData = TryAlignOriented(stars, data, flippedFirst, out bool usedFlipped, out usedTransform);
                    if (alignedData == null) {
                        RecordReject($"alignment failed ({stars.Count} stars detected)");
                        return;
                    }

                    if (usedFlipped != _flipped) {
                        // Orientation toggled -> a meridian flip happened
                        // (or the mount flipped back). Count it and keep
                        // probing this orientation first from now on.
                        _flipped = usedFlipped;
                        MeridianFlipsHandled++;
                        _logger.LogInformation(
                            "Live stack: meridian flip handled, now stacking {Orient} frames (total flips={N})",
                            usedFlipped ? "180-deg-rotated" : "reference-orientation",
                            MeridianFlipsHandled);
                    }
                }

                if (_colorActive) {
                    // Colour: debayer the ORIGINAL frame to RGB, then warp
                    // each plane with the transform that aligned it (null =
                    // reference, no warp). Interpolation stays within a
                    // colour channel, so no CFA smear. Accumulate per channel
                    // into the 3 buffers, sharing one coverage count.
                    // MEMOPT: both stages write into session scratch — these
                    // planes are consumed by the accumulate loop below and
                    // never escape, so 6× ushort[N] (~108 MB on 9 MP) of
                    // per-frame LOH churn becomes a fixed session allocation.
                    int pc = _width * _height;
                    EnsureScratch(ref _dbR, pc);
                    EnsureScratch(ref _dbG, pc);
                    EnsureScratch(ref _dbB, pc);
                    BayerDebayer.Bilinear(data, _width, _height, _bayerPattern, _dbR!, _dbG!, _dbB!);
                    ushort[] r = _dbR!, g = _dbG!, b = _dbB!;
                    if (usedTransform != null) {
                        EnsureScratch(ref _warpR, pc);
                        EnsureScratch(ref _warpG, pc);
                        EnsureScratch(ref _warpB, pc);
                        r = ImageResampler.ApplyTransform(r, _width, _height, usedTransform, _warpR!);
                        g = ImageResampler.ApplyTransform(g, _width, _height, usedTransform, _warpG!);
                        b = ImageResampler.ApplyTransform(b, _width, _height, usedTransform, _warpB!);
                    }
                    int accN = Math.Min(r.Length, _stackR!.Length);
                    bool rej = SigmaRejection && _m2Buffer != null && _lumSumBuffer != null;
                    for (int i = 0; i < accN; i++) {
                        // Off-canvas after warp is 0 in all three planes.
                        if (r[i] > 0 || g[i] > 0 || b[i] > 0) {
                            if (rej) {
                                // Reject on luminance: an outlier in brightness
                                // (cosmic ray, hot pixel) drops the whole RGB
                                // triple so colour balance isn't skewed.
                                double lum = 0.299 * r[i] + 0.587 * g[i] + 0.114 * b[i];
                                if (!KappaSigmaStack.Accept(_lumSumBuffer!, _countBuffer!, _m2Buffer!,
                                        i, lum, SigmaMinFrames, SigmaKappa)) {
                                    RejectedPixels++;
                                    continue;
                                }
                                _stackR![i] += r[i];
                                _stackG![i] += g[i];
                                _stackB![i] += b[i];
                                // Update increments the shared count for us.
                                KappaSigmaStack.Update(_lumSumBuffer!, _countBuffer!, _m2Buffer!, i, lum);
                            } else {
                                _stackR![i] += r[i];
                                _stackG![i] += g[i];
                                _stackB![i] += b[i];
                                _countBuffer![i]++;
                            }
                        }
                    }
                } else {
                    // Mono: accumulate the aligned CFA/mono frame (running
                    // average), on the GPU when available, CPU otherwise.
                    int accN = Math.Min(alignedData.Length, _stackBuffer!.Length);
                    if (SigmaRejection && _m2Buffer != null) {
                        // Kappa-sigma path: per-pixel Welford + reject before
                        // folding in (no GPU kernel; runs on CPU).
                        for (int i = 0; i < accN; i++) {
                            if (alignedData[i] > 0 &&
                                !KappaSigmaStack.Accumulate(_stackBuffer, _countBuffer!, _m2Buffer,
                                    i, alignedData[i], SigmaMinFrames, SigmaKappa)) {
                                RejectedPixels++;
                            }
                        }
                    } else if (!_gpu.TryAccumulate(alignedData, _stackBuffer!, _countBuffer!, accN)) {
                        for (int i = 0; i < accN; i++) {
                            if (alignedData[i] > 0) {
                                _stackBuffer[i] += alignedData[i];
                                _countBuffer![i]++;
                            }
                        }
                    }
                }

                _frameCount++;
                // Retain for SaveCurrentStack: the master inherits the
                // last frame's BITPIX + camera/target headers.
                _lastBitDepth = props.BitDepth;
                _lastMetaData = imageData.MetaData;
            }

            // Generate stacked result and relay to clients.
            if (_colorActive) {
                // Colour: broadcast the debayered RGB stack as a colour JPEG
                // on the LIVE canvas (the client renders headered JPEGs by
                // FrameKind, no RGB-raw WebGL path needed).
                var rgbPixels = GetStackedResultRgb();
                var rgbProps = new ImageProperties {
                    Width = _width, Height = _height, BitDepth = props.BitDepth,
                    Channels = 3,
                    IsBayered = false,
                    BayerPattern = BayerPatternEnum.None
                };
                var rgbImage = new BaseImageData(rgbPixels, rgbProps, imageData.MetaData);
                // The colour stack is broadcast as an 8-bit JPEG (the raw WS
                // protocol is single-channel only), so the client can't build a
                // 16-bit histogram from it — it would pin the LIVE histogram
                // panel to 0..255 while the real frame is 16-bit. Compute the
                // true 16-bit luminance histogram + stats here and surface them
                // via the WS status so the panel reflects the actual data.
                ComputeColorHistogram(rgbPixels, _width, _height);
                // The colour live stack is the image the operator zooms into on
                // the LIVE tab, and it's broadcast only once per integrated
                // frame (seconds apart), so it isn't fps-critical like the video
                // stream. Send it at a much higher resolution + quality than the
                // 1280/80 video default so zooming stays sharp instead of
                // upscaling a downsized preview. Capped at the stack's native
                // size by the renderer's scale<=1 clamp.
                await _relay.RelayRgbJpegAsync(rgbImage, maxDim: 4096, quality: 90,
                    kind: FrameKind.LiveStack, ct: ct);
            } else {
                // Stabilize the relayed Bayer pattern: a single frame whose
                // CCD_CFA was momentarily empty (BayerPattern=None) must not
                // flip the LIVE display to mono. Reuse the last good pattern.
                if (props.BayerPattern != BayerPatternEnum.None
                        && props.BayerPattern != BayerPatternEnum.Auto)
                    _lastGoodBayer = props.BayerPattern;
                var relayBayer = (props.BayerPattern != BayerPatternEnum.None
                        && props.BayerPattern != BayerPatternEnum.Auto)
                    ? props.BayerPattern : _lastGoodBayer;
                var stackedPixels = GetStackedResult();
                var stackedProps = new ImageProperties {
                    Width = _width,
                    Height = _height,
                    BitDepth = props.BitDepth,
                    IsBayered = relayBayer != BayerPatternEnum.None,
                    BayerPattern = relayBayer
                };
                var stackedImage = new BaseImageData(stackedPixels, stackedProps, imageData.MetaData);
                // Dedicated stack kind: while a server stack runs the client
                // paints ONLY LiveStack frames on the LIVE canvas, so a stray
                // kind-0 frame can never flash B&W between stack updates.
                await _relay.RelayImageAsync(stackedImage, FrameKind.LiveStack, ct);
            }
        } else {
            // MetricsOnly: bookkeep frame count + dimensions so triggers
            // and status broadcasts have something to render, but skip
            // the accumulator. The raw frame is still relayed via
            // ImageRelayService elsewhere in the capture path (see
            // SequenceEngine / ImageRelayService.RelayImageAsync from
            // the camera capture endpoint), the WASM client picks it
            // up from the existing /ws/image-stream raw mode.
            lock (_lock) {
                if (_frameCount == 0) {
                    _startedAt = DateTime.UtcNow;
                    BeginElapsedSegment();
                    _width = props.Width;
                    _height = props.Height;
                    _referenceStars = stars;
                }
                _frameCount++;
            }
        }

        // Compute median HFR from the already-detected stars (no extra
        // pixel pass). Falls back to 0 when no stars, handlers that
        // care about HFR should treat 0 as "no data this frame".
        // Computed in BOTH modes so trigger orchestrator (auto-AF based
        // on HFR degradation) still works in MetricsOnly mode.
        double medianHfr = 0;
        if (stars.Count > 0) {
            var sorted = stars.Select(s => s.HFR).Where(h => h > 0).OrderBy(h => h).ToList();
            if (sorted.Count > 0) medianHfr = sorted[sorted.Count / 2];
        }
        LastFrameMedianHfr = medianHfr;
        LastFrameStarCount = stars.Count;

        // SNR-4: per-frame + cumulative background SNR.
        // - LastFrameSnr is the snap-quality of the incoming frame.
        //   Cheap (one extra pixel pass that piggy-backs on the same
        //   median/MAD we already need for the stretch path).
        // - CumulativeSnr is the SNR of the running-mean accumulator.
        //   In Full mode we compute it from _accumulator; in
        //   MetricsOnly mode the WASM client tells us via
        //   InjectCumulativeSnr() below (no buffer here to inspect).
        try {
            LastFrameSnr = ComputeFrameSnr(imageData.Data);
            LastFrameMean = ImageStatistics.ComputeMean(imageData.Data);
            if (mode == StackMode.Full) {
                CumulativeSnr = ComputeCumulativeSnrFromAccumulator();
            }
            RecordSnrSample(_frameCount, CumulativeSnr);
            RecomputeEta();
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Live stack: SNR computation failed (non-fatal)");
        }

        _logger.LogInformation("Live stack: frame {N} added, {Stars} stars (HFR={Hfr:F2}, snr={Snr:F1} cum={Cum:F1}), mode={Mode}",
            _frameCount, stars.Count, medianHfr, LastFrameSnr, CumulativeSnr, mode);

        // Snapshot handlers + await sequentially. Any handler that
        // throws is logged + swallowed, one bad subscriber can't
        // poison the chain. Slow handlers (AF, recenter) pause the
        // upstream capture loop by extending this await.
        LiveStackFrameHandler[] handlers;
        lock (_handlersLock) handlers = _frameHandlers.ToArray();
        if (handlers.Length > 0) {
            var info = new LiveStackFrameInfo(_frameCount, imageData, medianHfr, stars.Count, DateTime.UtcNow,
                FrameSnr: LastFrameSnr, CumulativeSnr: CumulativeSnr);
            foreach (var h in handlers) {
                try { await h(info); }
                catch (Exception ex) {
                    _logger.LogWarning(ex, "LiveStack frame handler threw (continuing)");
                }
            }
        }
        } finally {
            _isStacking = false;
        }
    }

    // ===== Meridian-flip alignment helpers (Part B) ==============
    //
    // Callers hold _lock (these read _referenceStars / _width / _height).

    /// <summary>
    /// Try to align <paramref name="data"/> onto the reference grid in both
    /// orientations, probing <paramref name="flippedFirst"/> first. Returns
    /// the warped pixels (reference orientation) or null if neither
    /// orientation registers. <paramref name="usedFlipped"/> reports which
    /// orientation won so the caller can track flip state.
    /// </summary>
    private ushort[]? TryAlignOriented(List<DetectedStar> stars, ushort[] data,
                                       bool flippedFirst, out bool usedFlipped,
                                       out AffineTransform? used) {
        usedFlipped = false;
        used = null;
        var order = flippedFirst ? new[] { true, false } : new[] { false, true };
        foreach (var flip in order) {
            if (!flip) {
                var t = StarMatcher.Match(_referenceStars!, stars);
                if (t != null) {
                    usedFlipped = false;
                    used = t;
                    _logger.LogDebug("Frame aligned (reference orientation): dx={Tx:F1} dy={Ty:F1}",
                        t.Tx, t.Ty);
                    return Warp(data, t);
                }
            } else {
                var rotStars = Rotate180Stars(stars, _width, _height);
                // Bigger search radius: a flip that wasn't plate-solve
                // recentred can leave a large residual translation that the
                // default 50 px window would miss.
                var t = StarMatcher.Match(_referenceStars!, rotStars, maxSearchRadius: 250.0);
                if (t != null) {
                    usedFlipped = true;
                    // Single warp lands the (un-rotated) frame on the
                    // reference grid: rotate 180 then apply the residual
                    // match, composed into one transform.
                    var rot180 = new AffineTransform {
                        M00 = -1, M11 = -1, Tx = _width - 1, Ty = _height - 1
                    };
                    var composed = AffineTransform.Compose(t, rot180);
                    used = composed;
                    _logger.LogDebug("Frame aligned (flipped): residual dx={Tx:F1} dy={Ty:F1}",
                        t.Tx, t.Ty);
                    return Warp(data, composed);
                }
            }
        }
        return null;
    }

    /// <summary>Affine warp + bilinear resample, on the GPU when available
    /// (<see cref="IGpuCompute"/>) and on the CPU otherwise. The CPU helper is
    /// the canonical fallback whenever the GPU declines.</summary>
    private ushort[] Warp(ushort[] data, AffineTransform t) {
        if (_gpu.TryWarpAffine(data, _width, _height, t, out var warped)) return warped;
        // MEMOPT: the CPU-warped mono frame is only accumulated, never
        // retained, so it reuses one session scratch instead of a fresh
        // ~18 MB LOH array per frame. (The GPU path keeps its own output
        // buffer — changing IGpuCompute isn't worth it for that branch.)
        EnsureScratch(ref _warpMono, _width * _height);
        return ImageResampler.ApplyTransform(data, _width, _height, t, _warpMono!);
    }

    private static List<DetectedStar> Rotate180Stars(List<DetectedStar> stars, int w, int h) {
        var result = new List<DetectedStar>(stars.Count);
        foreach (var s in stars) {
            result.Add(new DetectedStar {
                X = (w - 1) - s.X,
                Y = (h - 1) - s.Y,
                HFR = s.HFR,
                Peak = s.Peak,
                Flux = s.Flux,
                PixelCount = s.PixelCount,
                Eccentricity = s.Eccentricity,
                OrientationRad = s.OrientationRad
            });
        }
        return result;
    }

    // ===== SNR-4 helpers =========================================
    //
    // TargetSnr + ExposureSecondsHint are caller-set knobs (the LIVE
    // tab pushes them via /api/livestack/target-snr + the capture
    // endpoint hands us the last exposure). Both nullable: when null
    // the ETA computation returns null and the UI shows "—".

    /// <summary>Target SNR for the ETA widget. Frontend sets via the
    /// LIVE tab's override input (which itself defaults to the
    /// active rig's TargetSnr profile field). Null = no target →
    /// no ETA computed.</summary>
    public double? TargetSnr { get; set; }

    /// <summary>Average exposure time of recent frames, seconds.
    /// Used by ETA to convert frames-remaining into time-remaining.
    /// Capture endpoints push the last exposure here so the ETA
    /// reflects the actual sub length being shot.</summary>
    public double AverageExposureSec { get; set; } = 1.0;

    /// <summary>MetricsOnly mode bridge: the WASM client side
    /// computes cumulativeSnr on its accumulator and posts it back
    /// via the existing 'client-stack-progress' WS message. The
    /// ImageStreamHandler consumes the message and forwards via
    /// this method so the WS broadcast + ETA work the same as in
    /// Full mode. Frame-side per-frame snr also flows here so the
    /// LIVE / PREVIEW UIs render consistent numbers.</summary>
    public void InjectClientStackMetrics(int frameCount, double frameSnr, double cumulativeSnr) {
        InjectClientStackMetrics(frameCount, frameSnr, cumulativeSnr,
            bgeProcessed: null, bgeFallback: null, bgeError: null);
    }

    /// <summary>LSPP-5: extended overload that also lets the client
    /// report per-session BGE counters back to the server. When the
    /// browser runs BGE per-frame (MetricsOnly + bgeEnabled), it
    /// posts the running counters here so the WS broadcast can
    /// mirror them to every other connected browser + the LIVE-tab
    /// status badge stays in sync. Null params on the legacy
    /// 3-arg overload keep the existing CLST-5 wire format working
    /// untouched.</summary>
    public void InjectClientStackMetrics(int frameCount, double frameSnr, double cumulativeSnr,
                                          int? bgeProcessed, int? bgeFallback, string? bgeError) {
        if (Mode != StackMode.MetricsOnly) return;
        // Defensive: only update when the WASM client's frameCount is
        // not behind ours (it lags by ≤1 due to async dispatch). A
        // stale message shouldn't rewrite history.
        if (frameCount < _frameCount - 1) return;
        if (double.IsFinite(frameSnr) && frameSnr >= 0) LastFrameSnr = frameSnr;
        if (double.IsFinite(cumulativeSnr) && cumulativeSnr >= 0) {
            CumulativeSnr = cumulativeSnr;
            RecordSnrSample(frameCount, cumulativeSnr);
            RecomputeEta();
        }
        if (bgeProcessed.HasValue || bgeFallback.HasValue) {
            PreProcStatus.InjectClientBgeMetrics(
                processed: bgeProcessed ?? 0,
                fallback:  bgeFallback  ?? 0,
                error: bgeError);
        }
    }

    private double ComputeFrameSnr(ushort[] data) {
        // BENCH-PERF: delegate to the shared ImageStatistics path, which
        // now computes median + MAD via parallel partition-local
        // histograms (no per-frame deviations[] allocation). This used to
        // be hand-inlined here with two serial full-frame histogram
        // passes; the shared helper is identical numerically and runs
        // multi-core, which matters because it fires on every frame.
        if (data == null || data.Length == 0) return 0;
        return ImageStatistics.ComputeBackgroundSnrFromData(data);
    }

    private double ComputeCumulativeSnrFromAccumulator() {
        // Reconstruct the current running-mean stack from _stackBuffer /
        // _countBuffer, then run the same background-SNR path used per
        // frame so the two numbers are comparable.
        //
        // BENCH-PERF: the reconstruction is parallelized and the heavy
        // SNR computation now runs OUTSIDE _lock. Previously the whole
        // ~40 ms (Pi 4) reconstruct+SNR ran while holding _lock, which
        // serialized it against the next frame's accumulate. Now the lock
        // is held only for the parallel reconstruction; the three SNR
        // passes happen on the local snapshot with the lock released.
        // MEMOPT: reconstruct into session scratch (frames are strictly
        // sequential through AddFrameAsync, so the scratch is never read
        // and rewritten concurrently). Cells with no coverage are zeroed
        // explicitly — the scratch carries the previous frame's values.
        ushort[] stacked;
        lock (_lock) {
            if (_countBuffer == null) return 0;
            var n = _countBuffer.Length;
            var cb = _countBuffer;
            EnsureScratch(ref _scratchSnr, n);
            stacked = _scratchSnr!;
            if (_colorActive && _stackR != null && _stackG != null && _stackB != null) {
                // Colour: the mono accumulator doesn't exist; reconstruct the
                // Rec.601 luminance of the running mean. (Before MEMOPT this
                // path read the never-written mono buffer and reported the SNR
                // of an all-zero image, so colour sessions always showed
                // cumulative SNR 0 — reconstructing luminance fixes that.)
                var r = _stackR; var g = _stackG; var b = _stackB;
                Parallel.ForEach(Partitioner.Create(0, n), range => {
                    for (int i = range.Item1; i < range.Item2; i++) {
                        int c = cb[i];
                        stacked[i] = c > 0
                            ? (ushort)Math.Clamp(
                                (0.299 * r[i] + 0.587 * g[i] + 0.114 * b[i]) / c, 0, 65535)
                            : (ushort)0;
                    }
                });
            } else if (_stackBuffer != null) {
                var sb = _stackBuffer;
                Parallel.ForEach(Partitioner.Create(0, n), range => {
                    for (int i = range.Item1; i < range.Item2; i++) {
                        stacked[i] = cb[i] > 0
                            ? (ushort)Math.Clamp(sb[i] / cb[i], 0, 65535)
                            : (ushort)0;
                    }
                });
            } else {
                return 0;
            }
        }
        return ComputeFrameSnr(stacked);
    }

    private void RecordSnrSample(int frame, double snr) {
        if (frame <= 0 || !double.IsFinite(snr) || snr < 0) return;
        // Deduplicate identical frame numbers (defensive — shouldn't
        // happen but a duplicate WS message from the WASM client
        // could in theory arrive).
        if (_snrHistory.Count > 0 && _snrHistory[_snrHistory.Count - 1].frame == frame) {
            _snrHistory[_snrHistory.Count - 1] = (frame, snr);
            return;
        }
        _snrHistory.Add((frame, snr));
        if (_snrHistory.Count > 50) _snrHistory.RemoveAt(0);
    }

    private void RecomputeEta() {
        if (!TargetSnr.HasValue) { LastEta = null; return; }
        LastEta = SnrEtaCalculator.Estimate(_snrHistory, TargetSnr.Value, AverageExposureSec);
    }

    public ushort[] GetStackedResult() {
        lock (_lock) {
            if (_stackBuffer == null) return [];

            var result = new ushort[_stackBuffer.Length];
            for (int i = 0; i < _stackBuffer.Length; i++) {
                if (_countBuffer![i] > 0) {
                    result[i] = (ushort)Math.Clamp(_stackBuffer[i] / _countBuffer[i], 0, 65535);
                }
            }
            return result;
        }
    }

    /// <summary>The running-mean colour stack as a plane-sequential RGB
    /// buffer (R then G then B, each W*H). Empty when not in colour mode.
    /// Used by the colour live preview broadcast and the colour save path.</summary>
    public ushort[] GetStackedResultRgb() {
        lock (_lock) {
            if (_stackR == null || _stackG == null || _stackB == null) return [];
            int n = _stackR.Length;
            var result = new ushort[n * 3];
            for (int i = 0; i < n; i++) {
                int c = _countBuffer![i];
                if (c > 0) {
                    result[i]         = (ushort)Math.Clamp(_stackR[i] / c, 0, 65535);
                    result[n + i]     = (ushort)Math.Clamp(_stackG[i] / c, 0, 65535);
                    result[2 * n + i] = (ushort)Math.Clamp(_stackB[i] / c, 0, 65535);
                }
            }
            return result;
        }
    }

    /// <summary>Materialise the current accumulated stack as an image:
    /// a 3-channel plane-sequential RGB image when colour mode is active,
    /// otherwise the mono running-mean. Stamped with the last frame's
    /// metadata + bit depth so the written master keeps the camera /
    /// target / telescope headers. Returns null when nothing has been
    /// integrated yet. The caller persists it via ImageWriterService.</summary>
    public IImageData? GetCurrentStackImage() {
        lock (_lock) {
            if (_frameCount == 0 || _width == 0 || _height == 0) return null;
            var meta = _lastMetaData ?? new ImageMetaData();
            if (_colorActive && _stackR != null) {
                var rgb = GetStackedResultRgb();
                if (rgb.Length == 0) return null;
                var props = new ImageProperties {
                    Width = _width, Height = _height, BitDepth = _lastBitDepth,
                    Channels = 3, IsBayered = false, BayerPattern = BayerPatternEnum.None
                };
                return new BaseImageData(rgb, props, meta);
            } else {
                var mono = GetStackedResult();
                if (mono.Length == 0) return null;
                var props = new ImageProperties {
                    Width = _width, Height = _height, BitDepth = _lastBitDepth
                };
                return new BaseImageData(mono, props, meta);
            }
        }
    }

    public StackStatus GetStatus() {
        return new StackStatus {
            IsRunning = _isRunning,
            FrameCount = _frameCount,
            Width = _width,
            Height = _height,
            ReferenceStarCount = _referenceStars?.Count ?? 0,
            Mode = Mode.ToString().ToLowerInvariant(),
            SaveFramesToDisk = SaveFramesToDisk,
            FramesSavedToDisk = _framesSavedToDisk,
            MeridianFlipsHandled = MeridianFlipsHandled,
            MaxDurationSeconds = MaxDurationSeconds,
            StartedAt = _startedAt,
            ElapsedSeconds = ElapsedSeconds,
            DurationCapReached = DurationCapReached,
            // SNR-4 surface for the WS broadcaster. EtaSeconds /
            // EtaFrames are null when SnrEtaCalculator returned null
            // (low confidence / no target set / target already met).
            LastFrameSnr = LastFrameSnr,
            CumulativeSnr = CumulativeSnr,
            TargetSnr = TargetSnr,
            EtaFrames = LastEta?.RemainingFrames,
            EtaSeconds = LastEta?.RemainingSeconds,
            EtaConfidence = LastEta?.Confidence,
            IsStacking = _isStacking,
            RejectedFrames = RejectedFrames,
            LastRejectReason = LastRejectReason,
            LastRejectAt = LastRejectAt
        };
    }

    public class StackStatus {
        public bool IsRunning { get; set; }
        public int FrameCount { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int ReferenceStarCount { get; set; }
        /// <summary>"full" or "metricsonly". UI uses this for the
        /// compute-location chip + the "Save current stack" button
        /// gating (only meaningful when a WASM client is actually
        /// doing the accumulation).</summary>
        public string Mode { get; set; } = "full";
        /// <summary>Mirrors <see cref="LiveStackingService.SaveFramesToDisk"/>
        /// so the UI checkbox reflects the live state across
        /// browser tabs (it is also persisted to the user profile
        /// in <see cref="LiveStackEndpoints"/>).</summary>
        public bool SaveFramesToDisk { get; set; }
        /// <summary>How many raw frames landed in lights/ during the
        /// current session. Shown next to the toggle as live
        /// confirmation that the writes are actually working.</summary>
        public int FramesSavedToDisk { get; set; }
        /// <summary>How many meridian-flip orientation changes the stacker
        /// re-oriented and stacked through this session. Surfaced in the
        /// LIVE tab as a "flips handled" note.</summary>
        public int MeridianFlipsHandled { get; set; }
        /// <summary>Per-stack auto-pause cap, seconds. 0 = unlimited
        /// (default). Persisted per-rig.</summary>
        public int MaxDurationSeconds { get; set; }
        /// <summary>UTC timestamp of the first frame in the current
        /// stack, or null when no frames have been integrated yet.</summary>
        public DateTime? StartedAt { get; set; }
        /// <summary>Seconds elapsed since StartedAt. 0 when null.
        /// Snapshot at the moment GetStatus was called; the UI
        /// re-renders it on every status broadcast (~1 Hz).</summary>
        public double ElapsedSeconds { get; set; }
        /// <summary>True when MaxDurationSeconds > 0 and elapsed
        /// crossed it. UI surfaces a "Stack complete" badge and
        /// stops the spinning indicator.</summary>
        public bool DurationCapReached { get; set; }
        // SNR-4: SNR + ETA payload. nullable on ETA fields because
        // SnrEtaCalculator returns null when the fit confidence is
        // below threshold or the target isn't configured.
        public double LastFrameSnr { get; set; }
        public double CumulativeSnr { get; set; }
        public double? TargetSnr { get; set; }
        public int? EtaFrames { get; set; }
        public double? EtaSeconds { get; set; }
        public double? EtaConfidence { get; set; }
        /// <summary>True while a frame is being detected/aligned/integrated right
        /// now — the UI shows a "Stacking…" indicator.</summary>
        public bool IsStacking { get; set; }
        /// <summary>How many frames were dropped (not integrated) this session.</summary>
        public int RejectedFrames { get; set; }
        /// <summary>Reason the last frame was dropped (null until one is).</summary>
        public string? LastRejectReason { get; set; }
        /// <summary>UTC timestamp of the last dropped frame (null until one is).</summary>
        public DateTime? LastRejectAt { get; set; }
    }
}