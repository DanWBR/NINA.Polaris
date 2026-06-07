using System.Diagnostics;
using NINA.Image.Interfaces;

namespace NINA.Polaris.Services;

/// <summary>
/// Camera sensor analysis, the same idea as SharpCap's Sensor Analysis:
/// measure conversion gain (e/ADU), read noise (e), full well (e) and
/// dynamic range across the camera's gain range using the Photon Transfer
/// Curve (mean-variance) method.
///
/// Method, per gain setting:
/// - Point the camera at a uniform light source. A short "bias" pair
///   (two frames at the minimum exposure) gives the read noise directly:
///   the difference of two frames cancels fixed-pattern noise, so its
///   standard deviation / sqrt(2) is the read noise in ADU.
/// - Sweep exposure to get several flat levels. For each level capture a
///   pair, take the mean signal (minus bias) and the variance of the
///   difference / 2. For shot-noise-limited flats the variance grows
///   linearly with signal; the slope is 1/gain, so gain (e/ADU) = 1/slope.
/// - Full well (e) = saturation_ADU * gain; read noise (e) = readNoiseADU
///   * gain; dynamic range (stops) = log2(fullWell / readNoise).
///
/// Justification-robust: the per-frame quantisation step is detected (a
/// 12-bit sensor delivered left-justified in a 16-bit container steps by
/// 16) and divided out, so the gain is expressed in native ADU.
///
/// Automatic: no manual brightness wizard. It just needs a reasonably
/// uniform, constant light and sweeps exposure itself, so it also runs
/// end-to-end against the simulator for testing. On-demand (not a hosted
/// service); runs on a background task with progress on /ws/status.
/// </summary>
public class SensorAnalysisService {
    private readonly ILogger<SensorAnalysisService> _logger;
    private readonly EquipmentManager _equipment;
    private readonly CameraStreamService _cameraStream;
    private readonly LiveStackingService _liveStack;
    private readonly SensorAnalysisStore _store;

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;

    // Central box (pixels) used for the statistics: keeps it off the
    // vignetted edges and bounds the per-frame compute.
    private const int RoiBox = 400;

    public string State { get; private set; } = "idle"; // idle|running|complete|error
    public int Progress { get; private set; }
    public string Phase { get; private set; } = "";
    public string? LastError { get; private set; }
    public SensorAnalysisResult? LastResult { get; private set; }

    public bool IsRunning => State == "running";

    public SensorAnalysisService(
        ILogger<SensorAnalysisService> logger,
        EquipmentManager equipment,
        CameraStreamService cameraStream,
        LiveStackingService liveStack,
        SensorAnalysisStore store) {
        _logger = logger;
        _equipment = equipment;
        _cameraStream = cameraStream;
        _liveStack = liveStack;
        _store = store;
    }

    public object GetStatus() => new {
        state = State,
        progress = Progress,
        phase = Phase,
        lastError = LastError,
        lastResult = LastResult
    };

    public string? Start(SensorAnalysisRequest req) {
        lock (_gate) {
            if (State == "running") return "A sensor analysis is already running.";
            var cam = _equipment.Camera;
            if (cam == null || !cam.IsConnected) return "Connect a camera first.";
            if (_liveStack.IsRunning) return "Stop live stacking before running sensor analysis.";
            if (_cameraStream.IsRunning) return "Stop the video stream before running sensor analysis.";

            _cts = new CancellationTokenSource();
            State = "running";
            Progress = 0;
            Phase = "Starting";
            LastError = null;
            var ct = _cts.Token;
            _ = Task.Run(() => RunInternalAsync(req ?? new SensorAnalysisRequest(), cam, ct));
            return null;
        }
    }

    public void Cancel() => _cts?.Cancel();

    private async Task RunInternalAsync(SensorAnalysisRequest req, ICamera cam, CancellationToken ct) {
        try {
            var gains = BuildGainList(req.MinGain, req.MaxGain, req.GainSteps);
            int bitDepth = cam.BitDepth > 0 ? cam.BitDepth : 16;
            double satAdu = Math.Pow(2, bitDepth) - 1;
            int frames = Math.Clamp(req.FramesPerLevel, 2, 4);
            int expSteps = Math.Clamp(req.ExposureSteps, 3, 20);
            double maxExp = Math.Clamp(req.MaxExposureSec, 0.01, 30.0);
            double minExp = Math.Clamp(req.MinExposureSec, 0.0, maxExp);

            int detectedQuant = 1;
            var rows = new List<SensorAnalysisRow>();
            double maxLinearMean = 0;

            int totalCaptures = gains.Count * (frames + expSteps * frames);
            int done = 0;

            foreach (var gain in gains) {
                ct.ThrowIfCancellationRequested();
                Phase = $"Gain {gain}";

                int w = 0, h = 0;

                // Bias pair at the shortest exposure -> read noise.
                var biasA = await CaptureAsync(cam, minExp, gain, ct); done++;
                var biasB = await CaptureAsync(cam, minExp, gain, ct); done++;
                Progress = (int)(5 + 90.0 * done / totalCaptures);
                w = biasA.Properties.Width; h = biasA.Properties.Height;
                int q = DetectQuantStep(biasA.Data);
                if (q > detectedQuant) detectedQuant = q;
                double biasMean = RegionMean(biasA.Data, w, h) / q;
                double readNoiseAdu = Math.Sqrt(RegionDiffVar(biasA.Data, biasB.Data, w, h)) / Math.Sqrt(2.0) / q;

                // Exposure sweep -> mean/variance points.
                var sig = new List<double>();
                var var = new List<double>();
                for (int s = 0; s < expSteps; s++) {
                    ct.ThrowIfCancellationRequested();
                    double frac = (s + 1) / (double)expSteps;
                    double exp = minExp + (maxExp - minExp) * frac;
                    var a = await CaptureAsync(cam, exp, gain, ct); done++;
                    var b = await CaptureAsync(cam, exp, gain, ct); done++;
                    Progress = (int)(5 + 90.0 * done / totalCaptures);

                    double meanAdu = (RegionMean(a.Data, w, h) + RegionMean(b.Data, w, h)) / 2.0 / q;
                    double signal = meanAdu - biasMean;
                    double v = RegionDiffVar(a.Data, b.Data, w, h) / 2.0 / (q * (double)q);
                    // Keep only the linear region: above the noise floor and
                    // below ~70% of saturation (avoids the non-linear knee).
                    if (signal > 5 && meanAdu < 0.7 * satAdu) {
                        sig.Add(signal);
                        var.Add(v);
                        if (meanAdu > maxLinearMean) maxLinearMean = meanAdu;
                    }
                }

                rows.Add(BuildRow(gain, sig, var, readNoiseAdu, satAdu));
            }

            // Relative gain vs the lowest-gain row that produced a valid e/ADU.
            var baseRow = rows.FirstOrDefault(r => r.ElectronsPerAdu > 0);
            double baseEadu = baseRow?.ElectronsPerAdu ?? 0;
            if (baseEadu > 0) {
                for (int i = 0; i < rows.Count; i++) {
                    if (rows[i].ElectronsPerAdu > 0) {
                        double rel = baseEadu / rows[i].ElectronsPerAdu;
                        rows[i] = rows[i] with {
                            RelativeGain = Math.Round(rel, 2),
                            RelativeGainDb = Math.Round(20 * Math.Log10(rel), 2)
                        };
                    }
                }
            }

            var dev = _equipment.Camera?.DeviceName ?? "camera";
            var result = new SensorAnalysisResult(
                Timestamp: DateTime.UtcNow.ToString("o"),
                Camera: dev,
                BitDepth: bitDepth,
                QuantizationStep: detectedQuant,
                LinearToPercent: satAdu > 0 ? Math.Round(100.0 * maxLinearMean / satAdu, 1) : 0,
                UnityGain: FindUnityGain(rows),
                Rows: rows);

            try { await _store.SaveResultAsync(result, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to persist sensor analysis result"); }

            LastResult = result;
            State = "complete";
            Progress = 100;
            Phase = "Done";
            _logger.LogInformation("Sensor analysis complete on {Cam}: {N} gain points", dev, rows.Count);
        } catch (OperationCanceledException) {
            State = "idle"; Progress = 0; Phase = "Cancelled";
        } catch (Exception ex) {
            _logger.LogError(ex, "Sensor analysis failed");
            LastError = ex.Message; State = "error"; Phase = "Error";
        } finally {
            lock (_gate) { _cts?.Dispose(); _cts = null; }
        }
    }

    private static async Task<IImageData> CaptureAsync(ICamera cam, double exp, int gain, CancellationToken ct) {
        return await cam.CaptureAsync(exp, new CaptureOptions(Gain: gain, ImageType: "FLAT"), ct);
    }

    // ----- math (static + testable) -----

    /// <summary>Build one result row from the collected mean/variance
    /// points via a linear PTC fit. Returns zeros (with Valid=false) when
    /// there aren't enough points or the slope is non-physical.</summary>
    internal static SensorAnalysisRow BuildRow(int gain, IList<double> signal, IList<double> variance,
                                               double readNoiseAdu, double satAdu) {
        if (signal.Count < 2) {
            return new SensorAnalysisRow(gain, 0, 0, 0, 1, 0, 0, signal.Count, 0, false,
                "Not enough usable exposure levels (light too dim/bright?).");
        }
        var (slope, _, r2) = LinFit(signal, variance);
        if (!(slope > 1e-9)) {
            return new SensorAnalysisRow(gain, 0, 0, 0, 1, 0, 0, signal.Count, Math.Round(r2, 3), false,
                "Variance did not rise with signal (non-shot-noise source).");
        }
        double eAdu = 1.0 / slope;                 // electrons per ADU
        double readNoiseE = readNoiseAdu * eAdu;
        double fullWellE = satAdu * eAdu;
        double dr = readNoiseE > 0 ? Math.Log2(fullWellE / readNoiseE) : 0;
        return new SensorAnalysisRow(
            Gain: gain,
            ElectronsPerAdu: Math.Round(eAdu, 4),
            ReadNoiseE: Math.Round(readNoiseE, 2),
            FullWellE: Math.Round(fullWellE, 0),
            RelativeGain: 1,
            RelativeGainDb: 0,
            DynamicRangeStops: Math.Round(dr, 2),
            Points: signal.Count,
            FitR2: Math.Round(r2, 3),
            Valid: true,
            Note: null);
    }

    /// <summary>Ordinary least-squares fit y = slope*x + intercept, plus
    /// the coefficient of determination R^2.</summary>
    internal static (double slope, double intercept, double r2) LinFit(IList<double> xs, IList<double> ys) {
        int n = xs.Count;
        if (n < 2) return (0, 0, 0);
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        for (int i = 0; i < n; i++) { sx += xs[i]; sy += ys[i]; sxx += xs[i] * xs[i]; sxy += xs[i] * ys[i]; }
        double denom = n * sxx - sx * sx;
        if (Math.Abs(denom) < 1e-12) return (0, sy / n, 0);
        double slope = (n * sxy - sx * sy) / denom;
        double intercept = (sy - slope * sx) / n;
        double meanY = sy / n, ssTot = 0, ssRes = 0;
        for (int i = 0; i < n; i++) {
            double f = slope * xs[i] + intercept;
            ssRes += (ys[i] - f) * (ys[i] - f);
            ssTot += (ys[i] - meanY) * (ys[i] - meanY);
        }
        double r2 = ssTot > 1e-12 ? 1 - ssRes / ssTot : 1;
        return (slope, intercept, r2);
    }

    /// <summary>Quantisation step of the data: the spacing between
    /// achievable values. A 12-bit sensor left-justified in a 16-bit
    /// container has every value a multiple of 16, so the OR of all
    /// samples has 4 trailing zero bits -> step 16. Right-justified data
    /// returns 1.</summary>
    internal static int DetectQuantStep(ushort[] data) {
        ushort orAll = 0;
        // Sample up to ~100k pixels for speed.
        int stride = Math.Max(1, data.Length / 100_000);
        for (int i = 0; i < data.Length; i += stride) orAll |= data[i];
        if (orAll == 0) return 1;
        int t = 0;
        while ((orAll & 1) == 0) { orAll >>= 1; t++; }
        return 1 << t;
    }

    internal static double RegionMean(ushort[] d, int w, int h) {
        var (x0, y0, x1, y1) = Roi(w, h);
        double sum = 0; long n = 0;
        for (int y = y0; y < y1; y++) {
            int row = y * w;
            for (int x = x0; x < x1; x++) { sum += d[row + x]; n++; }
        }
        return n > 0 ? sum / n : 0;
    }

    /// <summary>Variance of the per-pixel difference a-b over the central
    /// ROI (sample variance). The difference cancels fixed-pattern noise,
    /// leaving 2x the temporal noise; callers divide by 2.</summary>
    internal static double RegionDiffVar(ushort[] a, ushort[] b, int w, int h) {
        var (x0, y0, x1, y1) = Roi(w, h);
        double sum = 0, sumSq = 0; long n = 0;
        for (int y = y0; y < y1; y++) {
            int row = y * w;
            for (int x = x0; x < x1; x++) {
                double diff = a[row + x] - b[row + x];
                sum += diff; sumSq += diff * diff; n++;
            }
        }
        if (n < 2) return 0;
        double mean = sum / n;
        return (sumSq - n * mean * mean) / (n - 1);
    }

    private static (int x0, int y0, int x1, int y1) Roi(int w, int h) {
        int bw = Math.Min(RoiBox, w), bh = Math.Min(RoiBox, h);
        int x0 = (w - bw) / 2, y0 = (h - bh) / 2;
        return (x0, y0, x0 + bw, y0 + bh);
    }

    /// <summary>Log-spaced, de-duplicated integer gain list.</summary>
    internal static List<int> BuildGainList(int minGain, int maxGain, int steps) {
        steps = Math.Clamp(steps, 2, 20);
        if (maxGain <= minGain) return new List<int> { Math.Max(0, minGain) };
        var list = new List<int>();
        double lo = Math.Max(1, minGain);
        double ratio = maxGain / lo;
        for (int i = 0; i < steps; i++) {
            double g = lo * Math.Pow(ratio, i / (double)(steps - 1));
            int gi = (int)Math.Round(g);
            if (list.Count == 0 || gi != list[^1]) list.Add(gi);
        }
        if (minGain <= 0 && list[0] != 0) list.Insert(0, 0);
        return list;
    }

    /// <summary>Interpolate (in log-gain space) the gain at which the
    /// conversion gain crosses 1.0 e/ADU (unity gain), or null if the
    /// measured range never crosses it.</summary>
    internal static int? FindUnityGain(IList<SensorAnalysisRow> rows) {
        for (int i = 1; i < rows.Count; i++) {
            var a = rows[i - 1]; var b = rows[i];
            if (!a.Valid || !b.Valid) continue;
            double ea = a.ElectronsPerAdu, eb = b.ElectronsPerAdu;
            if ((ea - 1.0) * (eb - 1.0) <= 0 && ea != eb && a.Gain > 0 && b.Gain > 0) {
                double la = Math.Log(a.Gain), lb = Math.Log(b.Gain);
                double t = (1.0 - ea) / (eb - ea);
                return (int)Math.Round(Math.Exp(la + t * (lb - la)));
            }
        }
        return null;
    }
}

// ----- DTOs -----

public record SensorAnalysisRequest(
    int MinGain = 0,
    int MaxGain = 1000,
    int GainSteps = 8,
    double MinExposureSec = 0.01,
    double MaxExposureSec = 2.0,
    int ExposureSteps = 7,
    int FramesPerLevel = 2);

public record SensorAnalysisRow(
    int Gain,
    double ElectronsPerAdu,
    double ReadNoiseE,
    double FullWellE,
    double RelativeGain,
    double RelativeGainDb,
    double DynamicRangeStops,
    int Points,
    double FitR2,
    bool Valid,
    string? Note);

public record SensorAnalysisResult(
    string Timestamp,
    string Camera,
    int BitDepth,
    int QuantizationStep,
    double LinearToPercent,
    int? UnityGain,
    List<SensorAnalysisRow> Rows);
