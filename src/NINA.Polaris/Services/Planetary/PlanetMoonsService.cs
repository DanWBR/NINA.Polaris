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

using NINA.Core.Enum;
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using SkiaSharp;

namespace NINA.Polaris.Services.Planetary;

/// <summary>
/// Planet + moons, start to finish, on the running video stream:
/// meter the planet, record it, meter the moons (disc saturated), record
/// them, stack both recordings and merge the stacks into one picture.
///
/// <para>The gain, the ROI and the framing are the operator's: the stream
/// must already show the planet. Only the exposure is changed, and it is put
/// back when the job ends.</para>
/// </summary>
public class PlanetMoonsService {
    private readonly CameraStreamService _stream;
    private readonly VideoRecordingService _recorder;
    private readonly PlanetaryStackerService _stacker;
    private readonly ILogger<PlanetMoonsService> _logger;
    private readonly object _lock = new();

    public PlanetMoonsJob? Current { get; private set; }

    public PlanetMoonsService(CameraStreamService stream, VideoRecordingService recorder,
                              PlanetaryStackerService stacker, ILogger<PlanetMoonsService> logger) {
        _stream = stream;
        _recorder = recorder;
        _stacker = stacker;
        _logger = logger;
    }

    public PlanetMoonsJob Start(PlanetMoonsConfig cfg) {
        lock (_lock) {
            if (Current is { Running: true })
                throw new InvalidOperationException("A planet + moons capture is already running");
            if (cfg.FromFiles) {
                if (!File.Exists(cfg.PlanetSer)) throw new InvalidOperationException("The planet video was not found: " + cfg.PlanetSer);
                if (!File.Exists(cfg.MoonSer)) throw new InvalidOperationException("The moons video was not found: " + cfg.MoonSer);
            } else {
                if (!_stream.IsRunning)
                    throw new InvalidOperationException("Start the video stream with the planet in view first");
                if (_recorder.IsRecording)
                    throw new InvalidOperationException("A recording is in progress, stop it first");
            }
            var stack = _stacker.CurrentJob;
            if (stack != null && stack.CompletedAt == null)
                throw new InvalidOperationException("A stack is running, wait for it to finish");

            var job = new PlanetMoonsJob {
                Id = Guid.NewGuid().ToString("N"),
                Config = cfg,
                Running = true,
                StartedAt = DateTime.UtcNow,
                Phase = cfg.FromFiles ? "check" : "meter-planet",
                Message = cfg.FromFiles ? "Checking the two videos" : "Metering the planet",
            };
            job.Cts = new CancellationTokenSource();
            Current = job;
            job.Task = Task.Run(() => RunAsync(job, job.Cts.Token));
            return job;
        }
    }

    public void Abort() {
        var job = Current;
        if (job is { Running: true }) job.Cts?.Cancel();
    }

    private async Task RunAsync(PlanetMoonsJob job, CancellationToken ct) {
        var cfg = job.Config;
        // Two videos recorded earlier: the camera is not involved at all, so
        // the stream, the recorder and the exposure are left alone.
        bool fromFiles = cfg.FromFiles;
        double originalExposure = _stream.ExposureSeconds;
        try {
            if (fromFiles) {
                var check = await Task.Run(() => CheckRecordings(cfg.PlanetSer!, cfg.MoonSer!), ct);
                job.PlanetSer = check.PlanetSer;
                job.MoonSer = check.MoonSer;
                job.MoonsFound = check.MoonsFound;
                job.MoonsSeen = check.MoonsSeen;
                if (check.Swapped)
                    job.Warnings.Add("The planet video was the brighter of the two, so Polaris used the two videos the other way round");
            } else {
                // 1. Planet exposure.
                double planetExp = await MeterPlanetAsync(job, ct);
                job.PlanetExposureMs = planetExp * 1000;

                // 2. Planet recording.
                Step(job, "record-planet", $"Recording the planet at {planetExp * 1000:F1} ms");
                job.PlanetSer = await RecordAsync(job, planetExp, cfg.PlanetFrames, cfg.PlanetMaxSeconds, ct);

                // 3. Moons exposure.
                Step(job, "meter-moons", "Metering the moons");
                double moonExp = await MeterMoonsAsync(job, planetExp, ct);
                job.MoonExposureMs = moonExp * 1000;

                // 4. Moons recording.
                Step(job, "record-moons", $"Recording the moons at {moonExp * 1000:F0} ms");
                job.MoonSer = await RecordAsync(job, moonExp, cfg.MoonFrames, cfg.MoonMaxSeconds, ct);

                // The camera is free for the operator again while the stacks run.
                await RestoreExposureAsync(originalExposure);
            }

            // 5. Stacks.
            var dir = Path.GetDirectoryName(job.PlanetSer) ?? ".";
            var stackDir = Path.Combine(dir, "stacked");
            var name = Sanitize(cfg.Target);
            Step(job, "stack-planet", "Stacking the planet");
            var planetStack = await StackAsync(job,
                PlanetStackConfig(job.PlanetSer, stackDir, name, cfg.KeepPercent), ct);
            job.PlanetStack = planetStack.OutputPath;

            Step(job, "stack-moons", "Stacking the moons");
            var moonStack = await StackAsync(job,
                MoonStackConfig(job.MoonSer, stackDir, name, planetStack.WhiteBalanceApplied), ct);
            job.MoonStack = moonStack.OutputPath;

            // 6. Composite.
            Step(job, "composite", "Merging the two stacks");
            var written = await Task.Run(() => WriteCompositeFiles(
                job.PlanetStack!, job.MoonStack!, Path.Combine(dir, "composite"), name), ct);
            _logger.LogInformation("Planet + moons composite written: {Path} (shift {Dx:F1},{Dy:F1} px)",
                written.Png, written.Result.ShiftX, written.Result.ShiftY);
            job.CompositeFits = written.Fits;
            job.CompositePng = written.Png;
            Step(job, "done", job.MoonsFound
                ? "Done"
                : fromFiles
                    ? "Done, but no moon was found in the moons video"
                    : "Done, but no moon was found at the longest exposure; the outer field is from that exposure");
        } catch (OperationCanceledException) {
            job.Error = "Cancelled";
            Step(job, "cancelled", "Cancelled");
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Planet + moons capture failed");
            job.Error = ex.Message;
            Step(job, "failed", ex.Message);
        } finally {
            // A recording the operator started while the files were stacking is theirs.
            if (!fromFiles && _recorder.IsRecording) { try { await _recorder.StopAsync(); } catch { } }
            if (job.Phase is "cancelled" or "failed" && job.StackJobId is string sid) _stacker.Abort(sid);
            if (!fromFiles) await RestoreExposureAsync(originalExposure);
            job.Running = false;
            job.CompletedAt = DateTime.UtcNow;
        }
    }

    private static void Step(PlanetMoonsJob job, string phase, string message) {
        job.Phase = phase;
        job.Message = message;
    }

    private async Task RestoreExposureAsync(double exposure) {
        if (!_stream.IsRunning || exposure <= 0) return;
        if (Math.Abs(_stream.ExposureSeconds - exposure) < 1e-9) return;
        try { await _stream.UpdateLiveAsync(exposure, null); }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not restore the stream exposure"); }
    }

    // -----------------------------------------------------------------
    // Metering
    // -----------------------------------------------------------------

    private sealed record Reading(PlanetMoonsMath.DiscReading Disc, PlanetMoonsMath.MoonReading Moons);

    /// <summary>Set the exposure, let the frames taken at the old one pass,
    /// and read three frames. Returns the reading with the median disc signal
    /// and the median moon signal.</summary>
    private async Task<Reading> MeasureAsync(double exposure, CancellationToken ct) {
        if (!_stream.IsRunning) throw new InvalidOperationException("The video stream stopped");
        await _stream.UpdateLiveAsync(exposure, null);
        var settleUntil = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Max(0.4, 2 * exposure + 0.3));
        var readings = new List<Reading>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int busy = 0;
        void OnFrame(IImageData f) {
            if (DateTime.UtcNow < settleUntil || f.Data == null) return;
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            try {
                var (plane, w, h) = PlanetMoonsMath.Bin2(f.Data, f.Properties.Width, f.Properties.Height);
                var disc = PlanetMoonsMath.ReadDisc(plane, w, h);
                var moons = PlanetMoonsMath.ReadMoons(plane, w, h, disc);
                lock (readings) {
                    if (readings.Count < 3) readings.Add(new Reading(disc, moons));
                    if (readings.Count >= 3) done.TrySetResult();
                }
            } catch (Exception ex) {
                _logger.LogDebug(ex, "Metering frame failed");
            } finally { Interlocked.Exchange(ref busy, 0); }
        }
        using (_stream.SubscribeFrames(OnFrame)) {
            var timeout = TimeSpan.FromSeconds(15 + 6 * exposure);
            var finished = await Task.WhenAny(done.Task, Task.Delay(timeout, ct));
            ct.ThrowIfCancellationRequested();
            if (finished != done.Task)
                throw new InvalidOperationException("The video stream stopped sending frames while metering");
        }
        var sorted = readings.OrderBy(r => r.Disc.Signal).ToList();
        var mid = sorted[sorted.Count / 2];
        var moonSignals = readings.Select(r => r.Moons.Signal).OrderBy(v => v).ToList();
        var moonsFound = readings.Count(r => r.Moons.Found) >= 2;
        return mid with {
            Moons = new PlanetMoonsMath.MoonReading(moonsFound, moonSignals[moonSignals.Count / 2],
                                                    readings.Max(r => r.Moons.Count))
        };
    }

    private async Task<double> MeterPlanetAsync(PlanetMoonsJob job, CancellationToken ct) {
        var cfg = job.Config;
        double min = 0.0001, max = Math.Max(min, cfg.PlanetMaxExposureMs / 1000.0);
        double exp = Math.Clamp(_stream.ExposureSeconds > 0 ? _stream.ExposureSeconds : 0.01, min, max);
        for (int i = 0; i < 14; i++) {
            ct.ThrowIfCancellationRequested();
            job.Message = $"Metering the planet at {exp * 1000:F2} ms";
            var r = await MeasureAsync(exp, ct);
            if (!r.Disc.Found) {
                if (exp >= max) throw new InvalidOperationException(
                    "The planet is not visible in the frame, even at the longest planet exposure");
                exp = Math.Min(max, exp * 4);
                continue;
            }
            double target = 0.75 * (PlanetMoonsMath.FullScale - r.Disc.Background);
            bool saturated = r.Disc.SaturatedFraction > 0.002;
            job.PlanetLevel = r.Disc.Signal / (PlanetMoonsMath.FullScale - r.Disc.Background);
            if (!saturated && PlanetMoonsMath.Converged(r.Disc.Signal, target, 0.12)) return exp;
            double next = PlanetMoonsMath.NextExposure(exp, r.Disc.Signal, target, saturated, min, max);
            if (Math.Abs(next - exp) < 1e-9) {
                if (saturated) throw new InvalidOperationException(
                    "The planet saturates even at the shortest exposure: lower the gain");
                job.Warnings.Add($"The planet reaches {job.PlanetLevel:P0} of full scale at the {max * 1000:F0} ms limit: raise the gain for a brighter disc");
                return exp;
            }
            exp = next;
        }
        job.Warnings.Add("The planet exposure did not settle; the last value is used");
        return exp;
    }

    private async Task<double> MeterMoonsAsync(PlanetMoonsJob job, double planetExp, CancellationToken ct) {
        var cfg = job.Config;
        double max = Math.Max(planetExp, cfg.MoonMaxExposureMs / 1000.0);
        double min = planetExp;
        double exp = Math.Clamp(planetExp * 10, min, max);
        for (int i = 0; i < 14; i++) {
            ct.ThrowIfCancellationRequested();
            job.Message = $"Metering the moons at {exp * 1000:F0} ms";
            var r = await MeasureAsync(exp, ct);
            if (!r.Disc.Found) throw new InvalidOperationException("The planet left the frame while metering the moons");
            job.MoonsSeen = r.Moons.Count;
            if (!r.Moons.Found) {
                if (exp >= max) {
                    job.MoonsFound = false;
                    job.Warnings.Add($"No moon found up to {max * 1000:F0} ms; the outer field is recorded at that exposure");
                    return exp;
                }
                exp = Math.Min(max, exp * 3);
                continue;
            }
            job.MoonsFound = true;
            double room = PlanetMoonsMath.FullScale - r.Disc.Background;
            double target = 0.6 * room;
            bool saturated = r.Moons.Signal >= 0.95 * room;
            job.MoonLevel = r.Moons.Signal / room;
            if (!saturated && PlanetMoonsMath.Converged(r.Moons.Signal, target, 0.15)) return exp;
            double next = PlanetMoonsMath.NextExposure(exp, r.Moons.Signal, target, saturated, min, max);
            if (Math.Abs(next - exp) < 1e-9) {
                if (!saturated)
                    job.Warnings.Add($"The brightest moon reaches {job.MoonLevel:P0} of full scale at the {max * 1000:F0} ms limit");
                return exp;
            }
            exp = next;
        }
        job.Warnings.Add("The moons exposure did not settle; the last value is used");
        return exp;
    }

    // -----------------------------------------------------------------
    // Recording and stacking
    // -----------------------------------------------------------------

    private async Task<string> RecordAsync(PlanetMoonsJob job, double exposure, int frames,
                                           double maxSeconds, CancellationToken ct) {
        await _stream.UpdateLiveAsync(exposure, null);
        // Frames still in flight at the previous exposure must not open the SER.
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.4, 2 * exposure + 0.3)), ct);

        var saved = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnSaved(string p) => saved.TrySetResult(p);
        _recorder.RecordingSaved += OnSaved;
        try {
            _recorder.Start(new RecordingConfig(
                TargetName: string.IsNullOrWhiteSpace(job.Config.Target) ? "planet" : job.Config.Target,
                MaxFrames: Math.Max(10, frames),
                MaxDuration: TimeSpan.FromSeconds(Math.Max(5, maxSeconds)),
                BitDepth: job.Config.BitDepth == 8 ? 8 : 16));
            job.FramesTarget = Math.Max(10, frames);
            while (_recorder.IsRecording) {
                job.FramesRecorded = _recorder.FrameCount;
                await Task.Delay(400, ct);
            }
            job.FramesRecorded = _recorder.FrameCount;
            var finished = await Task.WhenAny(saved.Task, Task.Delay(TimeSpan.FromSeconds(30), ct));
            ct.ThrowIfCancellationRequested();
            if (finished != saved.Task)
                throw new InvalidOperationException(_recorder.LastError ?? "The recording did not produce a file");
            return saved.Task.Result;
        } finally {
            _recorder.RecordingSaved -= OnSaved;
        }
    }

    private async Task<StackJob> StackAsync(PlanetMoonsJob job, StackConfig cfg, CancellationToken ct) {
        var s = _stacker.StartJob(cfg);
        job.StackJobId = s.Id;
        while (s.Phase is not (StackPhase.Ok or StackPhase.Fail)) {
            job.StackPhase = s.Phase.ToString();
            job.StackFrames = s.FramesStacked;
            job.StackTotal = s.TotalFrames;
            await Task.Delay(500, ct);
        }
        job.StackJobId = null;
        if (s.Phase == StackPhase.Fail || s.OutputPath == null)
            throw new InvalidOperationException("Stacking failed: " + (s.Error ?? "no output"));
        return s;
    }

    // -----------------------------------------------------------------
    // Videos recorded earlier
    // -----------------------------------------------------------------

    internal sealed record RecordingCheck(string PlanetSer, string MoonSer, bool Swapped,
                                          bool MoonsFound, int MoonsSeen);

    /// <summary>Check two SER files picked as the planet and the moons
    /// videos before minutes of stacking: they must be different files of
    /// the same frame size. When the middle frame of each shows which one was
    /// exposed longer and the operator picked them the other way round, the
    /// two are swapped. The moons reading comes from the longer video's
    /// middle frame; when a frame cannot be read (an RGB file, a disc that
    /// is not found), the files are taken as given and the moons as found.</summary>
    internal static RecordingCheck CheckRecordings(string planetSer, string moonSer) {
        if (string.Equals(Path.GetFullPath(planetSer), Path.GetFullPath(moonSer), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose two different videos, one for the planet and one for the moons");
        using var p = new SerFileReader(planetSer);
        using var m = new SerFileReader(moonSer);
        if (p.FrameCount < 2) throw new InvalidOperationException("The planet video has fewer than 2 frames");
        if (m.FrameCount < 2) throw new InvalidOperationException("The moons video has fewer than 2 frames");
        if (p.Width != m.Width || p.Height != m.Height)
            throw new InvalidOperationException(
                $"The two videos differ in size ({p.Width}x{p.Height} and {m.Width}x{m.Height}): record both with the same camera and the same ROI");
        var rp = Probe(p);
        var rm = Probe(m);
        bool swapped = rp is { } a && rm is { } b && ExposedLonger(a.Disc, b.Disc);
        var moons = swapped ? rp : rm;
        return new RecordingCheck(
            swapped ? moonSer : planetSer,
            swapped ? planetSer : moonSer,
            swapped,
            moons?.Moons.Found ?? true,
            moons?.Moons.Count ?? 0);
    }

    private static Reading? Probe(SerFileReader r) {
        if (r.ColorMode is SerColorMode.Rgb or SerColorMode.Bgr || r.BitDepth is not (8 or 16)) return null;
        try {
            var frame = r.ReadFrameAsUshort(r.FrameCount / 2);
            var (plane, w, h) = PlanetMoonsMath.Bin2(frame, r.Width, r.Height);
            var disc = PlanetMoonsMath.ReadDisc(plane, w, h);
            if (!disc.Found) return null;
            return new Reading(disc, PlanetMoonsMath.ReadMoons(plane, w, h, disc));
        } catch (Exception) {
            return null;
        }
    }

    /// <summary>Whether disc <paramref name="a"/> was clearly exposed longer
    /// than disc <paramref name="b"/>: saturated while the other is not, or,
    /// with neither saturated, more than twice as bright.</summary>
    internal static bool ExposedLonger(PlanetMoonsMath.DiscReading a, PlanetMoonsMath.DiscReading b) {
        const double clipped = 0.05, clean = 0.005;
        if (a.SaturatedFraction > clipped && b.SaturatedFraction < clean) return true;
        return a.SaturatedFraction < clean && b.SaturatedFraction < clean && a.Signal > 2 * b.Signal;
    }

    // -----------------------------------------------------------------
    // Composite
    // -----------------------------------------------------------------

    /// <summary>The planet stack: the disc is the subject, so the
    /// alignment-point mesh and a grey-world balance on it. Levels are left
    /// raw; the composite sets its own.</summary>
    internal static StackConfig PlanetStackConfig(string ser, string outDir, string name, double keepPercent) =>
        new(SerPath: ser, OutputDir: outDir,
            KeepPercent: Math.Clamp(keepPercent, 1, 100),
            OutputName: name + "_planet", Target: "planet",
            AlignmentPoints: true, NormalizeLevels: false, WhiteBalance: "auto");

    /// <summary>The moons stack: the disc is saturated, so a mesh on it finds
    /// nothing, and the planet's own balance is reused (a grey-world balance
    /// on a saturated disc would tint the moons).</summary>
    internal static StackConfig MoonStackConfig(string ser, string outDir, string name, double[]? planetWb) {
        bool wb = planetWb is { Length: 3 } && planetWb[1] > 0;
        return new(SerPath: ser, OutputDir: outDir,
            KeepPercent: 60, OutputName: name + "_moons", Target: "planet",
            AlignmentPoints: false, NormalizeLevels: false,
            WhiteBalance: wb ? "manual" : "off",
            WbRed: wb ? planetWb![0] / planetWb[1] : 1.0,
            WbBlue: wb ? planetWb![2] / planetWb[1] : 1.0);
    }

    /// <summary>Merge the two stacked FITS and write the result as a 16-bit
    /// FITS and a PNG.</summary>
    internal static (string Fits, string Png, PlanetMoonsMath.CompositeResult Result) WriteCompositeFiles(
            string planetFits, string moonFits, string dir, string name) {
        var planet = Load(planetFits);
        var moons = Load(moonFits);
        var result = PlanetMoonsMath.Composite(planet, moons);
        Directory.CreateDirectory(dir);
        var stamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH-mm-ss");
        var fitsPath = Path.Combine(dir, $"{name}_moons_composite_{stamp}.fits");
        var pngPath = Path.Combine(dir, $"{name}_moons_composite_{stamp}.png");

        int n = result.Width * result.Height;
        var u16 = new ushort[result.Rgb.Length];
        for (int i = 0; i < u16.Length; i++) u16[i] = (ushort)Math.Round(Math.Clamp(result.Rgb[i], 0f, 1f) * 65535);
        var image = new BaseImageData(u16, new ImageProperties {
            Width = result.Width, Height = result.Height, BitDepth = 16, Channels = result.Channels,
            IsBayered = false, BayerPattern = BayerPatternEnum.None
        }, new ImageMetaData());
        FITSWriter.Write(image, fitsPath);

        using (var bmp = new SKBitmap(result.Width, result.Height, SKColorType.Rgba8888, SKAlphaType.Opaque)) {
            var px = new byte[n * 4];
            for (int i = 0; i < n; i++) {
                byte r = ToByte(result.Rgb[i]);
                byte g = result.Channels >= 3 ? ToByte(result.Rgb[n + i]) : r;
                byte b = result.Channels >= 3 ? ToByte(result.Rgb[2 * n + i]) : r;
                px[4 * i] = r; px[4 * i + 1] = g; px[4 * i + 2] = b; px[4 * i + 3] = 255;
            }
            System.Runtime.InteropServices.Marshal.Copy(px, 0, bmp.GetPixels(), px.Length);
            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            using var fs = File.Create(pngPath);
            data.SaveTo(fs);
        }
        return (fitsPath, pngPath, result);
    }

    private static byte ToByte(float v) => (byte)Math.Round(Math.Clamp(v, 0f, 1f) * 255);

    private static PlanetMoonsMath.Plane16 Load(string path) {
        var img = FITSReader.Read(File.ReadAllBytes(path));
        var channels = Math.Max(1, img.Properties.Channels);
        return new PlanetMoonsMath.Plane16(img.Data, img.Properties.Width, img.Properties.Height, channels);
    }

    private static string Sanitize(string? s) {
        var name = string.IsNullOrWhiteSpace(s) ? "planet" : s.Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Replace(' ', '_');
    }
}

public record PlanetMoonsConfig(
    string Target = "Jupiter",
    int PlanetFrames = 1500,
    double PlanetMaxSeconds = 60,
    int MoonFrames = 200,
    double MoonMaxSeconds = 60,
    double PlanetMaxExposureMs = 100,
    double MoonMaxExposureMs = 1000,
    double KeepPercent = 25,
    int BitDepth = 16,
    /// <summary>Two SER files recorded earlier. When both are set, the job
    /// skips the metering and the recordings and starts at the stacks.</summary>
    string? PlanetSer = null,
    string? MoonSer = null) {
    public bool FromFiles => !string.IsNullOrWhiteSpace(PlanetSer) && !string.IsNullOrWhiteSpace(MoonSer);
}

public class PlanetMoonsJob {
    public string Id { get; set; } = "";
    public PlanetMoonsConfig Config { get; set; } = new();
    public bool Running { get; set; }
    /// <summary>meter-planet, record-planet, meter-moons, record-moons (or
    /// check, for two videos recorded earlier), stack-planet, stack-moons,
    /// composite, done, failed, cancelled.</summary>
    public string Phase { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Error { get; set; }
    public List<string> Warnings { get; } = new();
    public double? PlanetExposureMs { get; set; }
    public double? MoonExposureMs { get; set; }
    /// <summary>Last metered level, as a fraction of full scale above the sky.</summary>
    public double? PlanetLevel { get; set; }
    public double? MoonLevel { get; set; }
    public bool MoonsFound { get; set; }
    public int MoonsSeen { get; set; }
    public int FramesRecorded { get; set; }
    public int FramesTarget { get; set; }
    public string? StackPhase { get; set; }
    public int StackFrames { get; set; }
    public int StackTotal { get; set; }
    public string? PlanetSer { get; set; }
    public string? MoonSer { get; set; }
    public string? PlanetStack { get; set; }
    public string? MoonStack { get; set; }
    public string? CompositeFits { get; set; }
    public string? CompositePng { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    internal string? StackJobId { get; set; }
    internal Task? Task { get; set; }
    internal CancellationTokenSource? Cts { get; set; }
}
