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
using System.Runtime.InteropServices;
using NINA.Image.Interfaces;
using NINA.Polaris.Services.External;
using NINA.Polaris.Services.Studio;
using SkiaSharp;

namespace NINA.Polaris.Services.Broadcast;

/// <summary>Which of the three possible pictures a frame was drawn from. Read
/// by the status block, and the reason a stalled broadcast is diagnosable.</summary>
public enum BroadcastFrameSource { None, VideoStream, LatestImage, Repeated }

/// <summary>What the broadcast is doing, for the status block and the card.</summary>
public sealed record BroadcastStatus {
    public bool Running { get; init; }
    public string? Destination { get; init; }
    public string Quality { get; init; } = "medium";
    public string? Encoder { get; init; }
    public bool HardwareEncoder { get; init; }
    public double UptimeSec { get; init; }
    public double Fps { get; init; }
    public double BitrateKbps { get; init; }
    public long DroppedFrames { get; init; }
    public int Reconnects { get; init; }
    public bool Recording { get; init; }
    public string? RecordPath { get; init; }
    public string? LastError { get; init; }
    public string FrameSource { get; init; } = "none";
    /// <summary>Which source the corner picture is drawn from, and why it is
    /// missing when it is.</summary>
    public string PipSource { get; init; } = PipSources.Off;
    public string? PipError { get; init; }
    public long FramesSent { get; init; }
    /// <summary>The music this run is playing, or why it is not. Null when the
    /// operator asked for none, which is the default and not a problem.</summary>
    public string? Music { get; init; }
}

/// <summary>
/// Runs a live broadcast: composes frames and keeps an ffmpeg fed with them
/// for as long as the operator wants.
///
/// <para>The frame clock is the heart of it and it is deliberately dumb. It
/// ticks at a fixed rate and asks, every tick, for the best picture available
/// right now: the video stream if one is running, otherwise the last image the
/// relay saw, otherwise the frame it drew last time. It never waits for a
/// picture. A deep sky sub takes two minutes and a plate solve takes thirty
/// seconds, and a broadcast that stopped sending during those would be a
/// broadcast that stops every two minutes.</para>
///
/// <para>It never starts a camera stream of its own either. <see
/// cref="CameraStreamService"/> holds the camera exclusively while it runs, so
/// a broadcast that opened one would block every exposure for as long as it
/// was on air. It subscribes to a stream someone else started and otherwise
/// takes what the relay has.</para>
///
/// <para>Above all that sits a supervisor. ffmpeg exits when an RTMP
/// connection drops, which on a hotspot at a dark site is a normal event, so
/// the run loop restarts it with a capped backoff and counts the reconnects
/// rather than ending the broadcast.</para>
/// </summary>
public sealed class BroadcastService : IDisposable {

    /// <summary>Frames written into ffmpeg per second. Two is plenty for deep
    /// sky, where the picture changes once per sub; the encoder duplicates up
    /// to the output rate the platforms expect.</summary>
    private const int InputFps = 2;

    /// <summary>How long without a reading from ffmpeg before the run is
    /// treated as wedged rather than merely quiet.</summary>
    private static readonly TimeSpan ProgressTimeout = TimeSpan.FromSeconds(30);

    private readonly BroadcastConfigService _config;
    private readonly FfmpegService _ffmpeg;
    private readonly ObjectCardService _cards;
    private readonly BroadcastPipService _pip;
    private readonly ImageRelayService _relay;
    private readonly CameraStreamService _stream;
    private readonly LiveStackingService _liveStack;
    private readonly LiveCaptureService _liveCapture;
    private readonly ActiveGuiderProvider _guiders;
    private readonly EquipmentManager _equipment;
    private readonly SkyCatalogService _sky;
    private readonly PlateSolveService _plateSolve;
    private readonly ProfileService _profiles;
    private readonly NotificationService _notify;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<BroadcastService> _logger;

    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private Task? _run;
    private volatile bool _wanted;

    // Live numbers, written by the run loop and read by the status block.
    private DateTime _startedUtc;
    private volatile string? _encoder;
    private volatile string? _recordPath;
    private volatile string? _lastError;
    /// <summary>What the music setting resolved to for this run, so the status
    /// block can say "3 tracks" rather than leaving the operator to guess
    /// whether the folder they typed was found.</summary>
    private volatile string? _musicNote;
    private long _framesSent;
    private long _dropped;
    private int _reconnects;
    private double _fps, _bitrate;
    private volatile string _frameSource = "none";

    // The picture, and the work of turning it into one.
    private IImageData? _streamFrame;
    private object? _decodedFrom;
    private SKBitmap? _decoded;
    private ObjectCard? _card;
    private string? _cardTarget;

    public BroadcastService(BroadcastConfigService config, FfmpegService ffmpeg, ObjectCardService cards,
                            BroadcastPipService pip,
                            ImageRelayService relay, CameraStreamService stream,
                            LiveStackingService liveStack, LiveCaptureService liveCapture,
                            ActiveGuiderProvider guiders, EquipmentManager equipment,
                            SkyCatalogService sky, PlateSolveService plateSolve,
                            ProfileService profiles, NotificationService notify,
                            IWebHostEnvironment env, ILogger<BroadcastService> logger) {
        _config = config;
        _ffmpeg = ffmpeg;
        _cards = cards;
        _pip = pip;
        _relay = relay;
        _stream = stream;
        _liveStack = liveStack;
        _liveCapture = liveCapture;
        _guiders = guiders;
        _equipment = equipment;
        _sky = sky;
        _plateSolve = plateSolve;
        _profiles = profiles;
        _notify = notify;
        _env = env;
        _logger = logger;
        // A description that arrives mid broadcast replaces the generated
        // sentence on the next tick instead of waiting for the next target.
        _cards.NoteFetched += _ => { lock (_lock) { _cardTarget = null; } };
    }

    public bool IsRunning => _wanted;

    public BroadcastStatus GetStatus() {
        var cfg = _config.Get();
        return new BroadcastStatus {
            Running = _wanted,
            Destination = _wanted ? cfg.Destination : null,
            Quality = cfg.Quality,
            Encoder = _encoder,
            HardwareEncoder = FfmpegEncoders.IsHardware(_encoder),
            UptimeSec = _wanted ? Math.Round((DateTime.UtcNow - _startedUtc).TotalSeconds) : 0,
            Fps = Math.Round(Volatile.Read(ref _fps), 1),
            BitrateKbps = Math.Round(Volatile.Read(ref _bitrate)),
            DroppedFrames = Interlocked.Read(ref _dropped),
            Reconnects = Volatile.Read(ref _reconnects),
            Recording = _recordPath != null,
            RecordPath = _recordPath,
            LastError = _lastError,
            FrameSource = _frameSource,
            PipSource = cfg.PipSource,
            PipError = cfg.PipSource == PipSources.Off ? null : _pip.LastError,
            FramesSent = Interlocked.Read(ref _framesSent),
            Music = _wanted ? _musicNote : null
        };
    }

    /// <summary>Null when a broadcast could start now, otherwise the reason in
    /// the words the operator sees.</summary>
    public string? Validate() => _config.Validate(_ffmpeg.IsAvailable);

    // --- Start and stop ---------------------------------------------

    /// <summary>
    /// Start broadcasting. Returns the reason it could not, or null on
    /// success. Nothing reaches the internet before this is called: there is
    /// no auto-start and no resume across a restart, by design.
    /// </summary>
    public async Task<string?> StartAsync() {
        var reason = Validate();
        if (reason != null) return reason;
        lock (_lock) {
            if (_wanted) return null;
            _wanted = true;
            _cts = new CancellationTokenSource();
            _lastError = null;
            _startedUtc = DateTime.UtcNow;
            Interlocked.Exchange(ref _framesSent, 0);
            Interlocked.Exchange(ref _dropped, 0);
            Volatile.Write(ref _reconnects, 0);
            _cardTarget = null;
        }

        _encoder = await ChooseEncoderAsync().ConfigureAwait(false);
        _logger.LogInformation("Broadcast starting with the {Encoder} encoder", _encoder);

        var token = _cts!.Token;
        _run = Task.Run(() => RunAsync(token), CancellationToken.None);
        return null;
    }

    /// <summary>
    /// The encoder to use, settled by trying them.
    ///
    /// <para>The preference list is walked in order and each candidate is
    /// asked to encode a fifth of a second of black before it is trusted with
    /// a broadcast. Listing an encoder and being able to run it are different
    /// things, and the gap between them is not visible from any table: this
    /// machine lists h264_qsv, has no usable Quick Sync, and the first version
    /// of this picked it and spent the whole broadcast in a restart loop.</para>
    ///
    /// <para>Costs about a second at start, once, and only for the candidates
    /// ahead of the one that works.</para>
    /// </summary>
    private async Task<string> ChooseEncoderAsync() {
        var caps = await _ffmpeg.GetCapabilitiesAsync().ConfigureAwait(false);
        var listed = FfmpegEncoders.ParseEncoders(caps?.EncodersOutput);
        // A cheap pre-filter on Linux, where a missing device node settles it
        // without spawning anything.
        var usable = FfmpegEncoders.DropUnusable(listed, File.Exists);

        foreach (var candidate in FfmpegEncoders.PreferenceFor(IsArm())) {
            if (!usable.Contains(candidate)) continue;
            if (candidate == "libx264") break;   // the floor, and it always works
            if (await _ffmpeg.CanEncodeAsync(candidate).ConfigureAwait(false)) return candidate;
        }
        return "libx264";
    }

    /// <summary>Stop and let ffmpeg finish: the recording gets its last
    /// fragment and the platform gets a clean disconnect.</summary>
    public async Task StopAsync() {
        Task? run;
        lock (_lock) {
            if (!_wanted) return;
            _wanted = false;
            run = _run;
        }
        if (run != null) { try { await run.ConfigureAwait(false); } catch { } }
        Cleanup();
    }

    /// <summary>Stop now. For a shutdown, where waiting out a drain on a board
    /// that is powering off is worse than a truncated last fragment.</summary>
    public void Abort() {
        lock (_lock) {
            _wanted = false;
            try { _cts?.Cancel(); } catch { }
        }
    }

    // --- The run loop -----------------------------------------------

    private async Task RunAsync(CancellationToken ct) {
        var cfg = _config.Get();
        var quality = BroadcastQuality.Parse(cfg.Quality);
        var layout = BroadcastLayout.For(quality.Width, quality.Height, cfg.ShowHeader,
                                         cfg.TextScale, cfg.CardFullHeight);
        using var fonts = new BroadcastFonts(_env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot"));
        using var composer = new FrameComposer(layout, fonts);
        _recordPath = cfg.RecordToDisk ? NewRecordingPath() : null;

        var (musicFile, musicList) = PrepareMusic(cfg);

        using var streamSub = _stream.SubscribeFrames(OnStreamFrame);

        var attempt = 0;
        while (_wanted && !ct.IsCancellationRequested) {
            var plan = new BroadcastPlan {
                Quality = quality,
                Encoder = _encoder ?? "libx264",
                InputFps = InputFps,
                OutputFps = 25,
                RtmpUrl = cfg.CanPublish ? cfg.RtmpUrl : null,
                StreamKey = cfg.CanPublish ? cfg.StreamKey : null,
                // A restart opens a new file rather than overwriting the one
                // the first half of the night is in.
                RecordPath = _recordPath == null ? null : (attempt == 0 ? _recordPath : NewRecordingPath()),
                MusicFile = musicFile,
                MusicPlaylistFile = musicList,
                MusicVolume = cfg.MusicVolume
            };
            if (plan.RecordPath != null) _recordPath = plan.RecordPath;

            List<string> args;
            try { args = BroadcastArgs.Build(plan); }
            catch (Exception ex) { Fail(ex.Message); return; }

            var lastProgress = DateTime.UtcNow;
            FfmpegRunResult result;
            try {
                result = await _ffmpeg.RunLiveAsync(args,
                    pumpFrames: (stdin, pumpCt) => PumpAsync(stdin, composer, cfg, () => lastProgress, pumpCt),
                    onProgress: p => {
                        lastProgress = DateTime.UtcNow;
                        Volatile.Write(ref _fps, p.Fps);
                        Volatile.Write(ref _bitrate, p.BitrateKbps);
                        Interlocked.Exchange(ref _dropped, p.DroppedFrames);
                    },
                    redactedCommandForLog: BroadcastArgs.Redact(args, plan.StreamKey),
                    ct: ct).ConfigureAwait(false);
            } catch (Exception ex) {
                Fail(ex.Message);
                return;
            }

            if (!_wanted || ct.IsCancellationRequested) break;

            // ffmpeg came back while the operator still wants a broadcast, so
            // something dropped. On a hotspot at a dark site that is a normal
            // night, not a fault: back off and pick it up again.
            attempt++;
            Volatile.Write(ref _reconnects, attempt);
            _lastError = string.IsNullOrWhiteSpace(result.StderrTail)
                ? $"ffmpeg exited with {result.ExitCode}"
                : LastLine(result.StderrTail);
            _logger.LogWarning("Broadcast lost its encoder (exit {Code}); reconnecting. {Error}",
                result.ExitCode, _lastError);
            _notify.Push("warn", $"Broadcast reconnecting ({attempt}): {_lastError}", 6000);

            // Capped: a destination that is refusing has to be retried slowly
            // enough not to become a hammering loop, and often enough that a
            // link coming back is picked up within a minute.
            var backoff = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(5, attempt))));
            try { await Task.Delay(backoff, ct).ConfigureAwait(false); } catch { break; }
        }
        Cleanup();
    }

    private void Fail(string message) {
        _lastError = message;
        _wanted = false;
        _logger.LogError("Broadcast could not run: {Error}", message);
        _notify.Push("error", "Broadcast stopped: " + message, 8000);
        Cleanup();
    }

    private void Cleanup() {
        lock (_lock) {
            _decoded?.Dispose();
            _decoded = null;
            _decodedFrom = null;
            _streamFrame = null;
            _frameSource = "none";
            Volatile.Write(ref _fps, 0);
            Volatile.Write(ref _bitrate, 0);
        }
    }

    // --- The frame clock --------------------------------------------

    /// <summary>
    /// Write composed frames into ffmpeg until the broadcast is over.
    ///
    /// <para>Returning from here is the graceful stop: <see
    /// cref="FfmpegService.RunLiveAsync"/> closes stdin, ffmpeg finishes the
    /// recording properly and exits on its own.</para>
    /// </summary>
    private async Task PumpAsync(Stream stdin, FrameComposer composer, BroadcastConfig cfg,
                                 Func<DateTime> lastProgress, CancellationToken ct) {
        var interval = TimeSpan.FromSeconds(1.0 / InputFps);
        var clock = Stopwatch.StartNew();
        var next = TimeSpan.Zero;

        while (_wanted && !ct.IsCancellationRequested) {
            var picture = CurrentPicture();
            var card = cfg.ShowObjectCard ? await CurrentCardAsync(cfg, ct).ConfigureAwait(false) : null;
            var banner = cfg.ShowBanner ? BroadcastBanner.Compose(CurrentBanner()) : null;
            var title = cfg.ShowHeader ? RigLine.Title(cfg.Title, ActiveRigName()) : null;
            var rig = cfg.ShowHeader ? RigLine.Equipment(CurrentRig()) : null;

            // Only while nothing has arrived at all. Once a picture has been
            // shown, the last one is repeated instead, which is better than
            // going back to a message.
            var waiting = picture == null ? BroadcastStrings.WaitingForFirstFrame(_cards.Language) : null;

            // The second picture, bottom left. Whatever is already in hand:
            // it never waits, and a source with nothing to show simply draws
            // no corner at all rather than an empty box.
            SKBitmap? pip = null;
            string? pipLabel = null;
            if (cfg.PipSource != PipSources.Off) {
                pip = _pip.Current(cfg.PipSource, cfg.PipUrl);
                if (pip != null) {
                    pipLabel = string.IsNullOrWhiteSpace(cfg.PipLabel)
                        ? BroadcastPipService.LabelFor(cfg.PipSource, _cards.Language)
                        : cfg.PipLabel;
                }
            }

            byte[] frame;
            try {
                frame = composer.Compose(picture, card, banner, title, rig, waiting, pip, pipLabel,
                                         fill: cfg.PictureFit == PictureFits.Fill);
            }
            catch (Exception ex) {
                // One bad frame is not a reason to end a broadcast. Skip it
                // and let the next tick try again.
                _logger.LogDebug(ex, "Could not compose a broadcast frame");
                await DelayTo(clock, next += interval, ct).ConfigureAwait(false);
                continue;
            }

            try {
                await stdin.WriteAsync(frame, ct).ConfigureAwait(false);
                await stdin.FlushAsync(ct).ConfigureAwait(false);
            } catch (Exception) {
                return;   // ffmpeg went away; the supervisor decides what next
            }
            Interlocked.Increment(ref _framesSent);

            // ffmpeg has stopped saying anything while we are still writing to
            // it. Ending the pump hands it to the supervisor, which restarts
            // it: better than filling a pipe nobody is reading for hours.
            if (DateTime.UtcNow - lastProgress() > ProgressTimeout) {
                _logger.LogWarning("No progress from ffmpeg for {Seconds:0}s; restarting the encoder",
                    ProgressTimeout.TotalSeconds);
                return;
            }

            await DelayTo(clock, next += interval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Sleep until the next tick on an absolute clock, so composing a
    /// slow frame does not push every later frame late.</summary>
    private static async Task DelayTo(Stopwatch clock, TimeSpan target, CancellationToken ct) {
        var wait = target - clock.Elapsed;
        if (wait > TimeSpan.Zero) {
            try { await Task.Delay(wait, ct).ConfigureAwait(false); } catch { }
        }
    }

    private void OnStreamFrame(IImageData frame) {
        // Enqueue-only, the way every other consumer of this callback works:
        // whatever is doing the capture must not be made to wait on us.
        lock (_lock) { _streamFrame = frame; }
    }

    /// <summary>
    /// The best picture available right now, and never a wait for one.
    ///
    /// <para>A running video stream wins: it is the live view, and it is the
    /// only source during planetary work. Otherwise the relay's last image,
    /// which covers LIVE, the stack, PREVIEW and AUTORUN, and which the relay
    /// keeps whether or not a browser is attached. Otherwise the last frame
    /// drawn, so the picture holds instead of going black between subs.</para>
    /// </summary>
    private SKBitmap? CurrentPicture() {
        IImageData? source;
        string kind;
        lock (_lock) { source = _streamFrame; }
        if (source != null && _stream.IsRunning) {
            kind = "videoStream";
        } else {
            source = _relay.LatestImageData;
            kind = "latestImage";
        }

        if (source == null) {
            lock (_lock) {
                _frameSource = _decoded != null ? "repeated" : "none";
                return _decoded;
            }
        }

        lock (_lock) {
            if (ReferenceEquals(source, _decodedFrom) && _decoded != null) {
                // Same picture as last tick, which at 2 fps and a 120 second
                // sub is 239 ticks out of 240. Decoding it again every time
                // would be the most expensive thing the broadcast does.
                _frameSource = "repeated";
                return _decoded;
            }
        }

        SKBitmap? decoded = null;
        try {
            var jpeg = FitsThumbnailer.RenderJpegFromImageData(source, maxDim: 1920, quality: 88);
            decoded = SKBitmap.Decode(jpeg);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Could not render the broadcast picture");
        }
        if (decoded == null) {
            lock (_lock) { _frameSource = _decoded != null ? "repeated" : "none"; return _decoded; }
        }

        lock (_lock) {
            _decoded?.Dispose();
            _decoded = decoded;
            _decodedFrom = source;
            _frameSource = kind;
            return _decoded;
        }
    }

    /// <summary>The card for the current target, rebuilt only when the target
    /// changes or a description arrives.</summary>
    private async Task<ObjectCard?> CurrentCardAsync(BroadcastConfig cfg, CancellationToken ct) {
        var target = CurrentTarget();
        lock (_lock) {
            if (_card != null && string.Equals(_cardTarget, target, StringComparison.Ordinal)) return _card;
        }
        ObjectCard card;
        try {
            card = await _cards.BuildAsync(target, cfg.FetchDescriptions, ct).ConfigureAwait(false);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Could not build the object card for {Target}", target);
            return null;
        }
        lock (_lock) { _card = card; _cardTarget = target; }
        return card;
    }

    // --- What the frame says ----------------------------------------

    /// <summary>
    /// What the scope is pointed at, worked out the same way the file namer
    /// does it: the mount's coordinates, refined by the last plate solve when
    /// that solve is for this field, looked up in the sky catalogue.
    ///
    /// <para>There is no "current target" stored anywhere to read, because
    /// nothing in Polaris requires the operator to declare one: LIVE and
    /// PREVIEW just point and shoot. Deriving it from where the mount is
    /// means the broadcast names the object correctly for a session that was
    /// never planned, which is most of them.</para>
    /// </summary>
    private string? CurrentTarget() {
        double? ra = null, dec = null;
        var mount = _equipment.Telescope;
        var mountOk = mount is { IsConnected: true }
            && !double.IsNaN(mount.RightAscension) && !double.IsNaN(mount.Declination);
        if (mountOk) { ra = mount!.RightAscension; dec = mount.Declination; }

        var solve = _plateSolve.LastSuccessfulSolve;
        if (solve != null) {
            // The solve is for THIS field and tighter than open loop
            // coordinates, but a stale one from an earlier target has to be
            // rejected, hence the separation gate.
            if (!mountOk) { ra = solve.RaHours; dec = solve.DecDeg; }
            else if (SeparationDeg(ra!.Value, dec!.Value, solve.RaHours, solve.DecDeg) <= 5.0) {
                ra = solve.RaHours; dec = solve.DecDeg;
            }
        }
        if (ra == null || dec == null) return null;

        try {
            var name = _sky.Identify(ra.Value, dec.Value, FovRadiusDeg())?.Object?.Name;
            return string.IsNullOrWhiteSpace(name) ? null : name!.Trim();
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Could not identify the broadcast target");
            return null;
        }
    }

    /// <summary>Half the field, from the rig's optics and the connected
    /// sensor. A guess of one degree when either is unknown: the catalogue
    /// lookup only needs the right order of magnitude.</summary>
    private double FovRadiusDeg() {
        var rig = _profiles.ActiveEquipmentProfile;
        var camera = _equipment.Camera;
        if (rig == null || camera is not { IsConnected: true } || rig.FocalLengthMm <= 0) return 1.0;
        var widthMm = camera.MaxX * camera.PixelSizeX / 1000.0;
        var heightMm = camera.MaxY * camera.PixelSizeY / 1000.0;
        var largest = Math.Max(widthMm, heightMm);
        if (largest <= 0) return 1.0;
        return Math.Clamp(Math.Atan(largest / 2 / rig.FocalLengthMm) * 180.0 / Math.PI, 0.05, 10.0);
    }

    private static double SeparationDeg(double ra1Hours, double dec1, double ra2Hours, double dec2) {
        var ra1 = ra1Hours * 15 * Math.PI / 180;
        var ra2 = ra2Hours * 15 * Math.PI / 180;
        var d1 = dec1 * Math.PI / 180;
        var d2 = dec2 * Math.PI / 180;
        var cos = Math.Sin(d1) * Math.Sin(d2) + Math.Cos(d1) * Math.Cos(d2) * Math.Cos(ra1 - ra2);
        return Math.Acos(Math.Clamp(cos, -1, 1)) * 180 / Math.PI;
    }

    private string? ActiveRigName() => _profiles.ActiveEquipmentProfile?.Name;

    private RigFacts CurrentRig() {
        var rig = _profiles.ActiveEquipmentProfile;
        return new RigFacts {
            RigName = rig?.Name,
            FocalLengthMm = rig?.FocalLengthMm ?? 0,
            ApertureMm = rig?.ApertureMm ?? 0,
            Camera = rig?.Camera,
            Mount = rig?.Telescope,
            FilterWheel = rig?.FilterWheel,
            Focuser = rig?.Focuser,
            GuideCamera = rig?.GuideCamera,
            Guider = _guiders.Active.IsGuiding ? GuiderName() : null
        };
    }

    private string GuiderName() => _guiders.Active is PHD2Client ? "PHD2" : "Polaris guiding";

    private BannerFacts CurrentBanner() {
        var stack = _liveStack.GetStatus();
        var camera = _equipment.Camera;
        var guider = _guiders.Active;
        var exposure = _liveCapture.IsRunning ? _liveCapture.ExposureSeconds : (double?)null;
        var frames = stack.IsRunning ? stack.FrameCount : (int?)null;

        return new BannerFacts {
            Target = CurrentTarget(),
            Filter = CurrentFilter(),
            ExposureSeconds = exposure,
            Gain = _liveCapture.IsRunning ? _liveCapture.Gain : null,
            FrameCount = frames,
            // Frames times exposure, which is integration rather than elapsed:
            // dithers, slews and rejected frames are in the clock and not in
            // the picture.
            IntegratedSeconds = frames is { } n && exposure is { } e ? n * e : null,
            Snr = stack.IsRunning && stack.CumulativeSnr > 0 ? stack.CumulativeSnr : null,
            GuideRmsArcsec = guider.IsGuiding ? guider.RmsTotal : null,
            // Only a camera with the cooler running has a temperature worth
            // printing; an uncooled sensor reports whatever it feels like.
            SensorTempC = camera is { IsConnected: true, CoolerOn: true } ? camera.Temperature : null
        };
    }

    /// <summary>The filter in front of the sensor: the wheel's current slot
    /// when there is a wheel, otherwise the one screwed into the train, which
    /// is what a rig with a dual band filter and no wheel has.</summary>
    private string? CurrentFilter() {
        var wheel = _equipment.FilterWheel;
        if (wheel is { IsConnected: true }) {
            var slot = wheel.CurrentFilterName;
            if (!string.IsNullOrWhiteSpace(slot)) return slot.Trim();
        }
        var attached = _profiles.ActiveEquipmentProfile?.AttachedFilter;
        return string.IsNullOrWhiteSpace(attached) ? null : attached.Trim();
    }

    // --- Odds and ends ----------------------------------------------

    /// <summary>
    /// Turn the operator's music setting into something ffmpeg can open, or
    /// into nothing at all.
    ///
    /// <para>Never throws and never refuses to start. A broadcast that dies
    /// because a USB stick with the music on it was unplugged would be a worse
    /// failure than a silent one, so a missing folder costs the music, says so
    /// in the status block and in one notification, and the night carries
    /// on.</para>
    /// </summary>
    private (string? File, string? Playlist) PrepareMusic(BroadcastConfig cfg) {
        _musicNote = null;
        try {
            var list = BroadcastMusic.Resolve(cfg.MusicPath, cfg.MusicShuffle,
                Environment.TickCount, File.Exists, Directory.Exists,
                d => Directory.EnumerateFiles(d));

            if (list.Problem != null) {
                _musicNote = list.Problem;
                _notify.Push("warn", "Broadcast music: " + list.Problem, 6000);
                _logger.LogWarning("Broadcast: {Problem}", list.Problem);
            }
            if (!list.HasMusic) return (null, null);

            if (list.IsSingleTrack) {
                _musicNote = Path.GetFileName(list.Tracks[0]);
                return (list.Tracks[0], null);
            }

            var dir = Path.Combine(_profiles.DataDir, "broadcast");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "music.ffconcat");
            File.WriteAllText(path, BroadcastMusic.RenderPlaylist(list.Tracks));
            _musicNote = $"{list.Tracks.Count} tracks"
                       + (cfg.MusicShuffle ? ", shuffled" : "");
            return (null, path);
        } catch (Exception ex) {
            // Reading a folder can fail for reasons the operator can do
            // nothing about mid-session. Silence is the safe answer.
            _logger.LogWarning(ex, "Broadcast: could not prepare the music, continuing without it");
            _musicNote = "could not be read";
            return (null, null);
        }
    }

    private string NewRecordingPath() {
        var root = _profiles.Active?.ImageOutputDir;
        if (string.IsNullOrWhiteSpace(root)) root = Path.Combine(_profiles.DataDir, "broadcast");
        else root = Path.Combine(root, "broadcast");
        Directory.CreateDirectory(root);
        return Path.Combine(root, DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".mp4");
    }

    private static bool IsArm() =>
        RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm;

    private static string LastLine(string text) {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? text.Trim() : lines[^1];
    }

    public void Dispose() {
        Abort();
        Cleanup();
        _cts?.Dispose();
    }
}
