using NINA.Image.ImageAnalysis;
using NINA.Image.Interfaces;
using NINA.Image.ImageData;

namespace NINA.Polaris.Services;

/// <summary>
/// Auto-focus service. Performs a symmetric sweep around the current focuser
/// position, measures HFR at each sample via star detection, fits a parabola
/// to the (position, HFR) points, then moves the focuser to the fitted minimum.
///
/// Math is exposed via static helpers so the parabola fit can be unit-tested
/// independently of any camera/focuser hardware.
/// </summary>
public class AutoFocusService {
    private readonly EquipmentManager _equip;
    private readonly ImageRelayService _relay;
    private readonly ILogger<AutoFocusService> _logger;

    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private readonly object _stateLock = new();

    public AutoFocusState State { get; private set; } = AutoFocusState.Idle;
    public AutoFocusProgress Progress { get; private set; } = new();
    public AutoFocusResult? LastResult { get; private set; }
    public string? LastError { get; private set; }

    public AutoFocusService(EquipmentManager equip,
                            ImageRelayService relay,
                            ILogger<AutoFocusService> logger) {
        _equip = equip;
        _relay = relay;
        _logger = logger;
    }

    public void Start(AutoFocusRequest request) {
        lock (_stateLock) {
            if (State == AutoFocusState.Running)
                throw new InvalidOperationException("Auto-focus already running");

            if (_equip.Camera == null)
                throw new InvalidOperationException("No camera connected");

            if (_equip.Focuser == null)
                throw new InvalidOperationException("No focuser connected");

            if (request.Steps < 3)
                throw new ArgumentException("Steps must be >= 3 (need at least 3 points for parabola fit)");

            if (request.StepSize <= 0)
                throw new ArgumentException("StepSize must be positive");

            if (request.ExposureSeconds <= 0)
                throw new ArgumentException("ExposureSeconds must be positive");

            _cts = new CancellationTokenSource();
            State = AutoFocusState.Running;
            LastError = null;
            Progress = new AutoFocusProgress {
                Steps = request.Steps,
                Points = new List<AutoFocusPoint>(),
                StartedAt = DateTime.UtcNow
            };
        }

        _runTask = Task.Run(() => RunAsync(request, _cts!.Token));
        _logger.LogInformation("Auto-focus started: steps={Steps} stepSize={StepSize} exposure={Exp}s",
            request.Steps, request.StepSize, request.ExposureSeconds);
    }

    public void Abort() {
        lock (_stateLock) {
            if (State != AutoFocusState.Running) return;
            _cts?.Cancel();
            _logger.LogInformation("Auto-focus abort requested");
        }
    }

    private async Task RunAsync(AutoFocusRequest request, CancellationToken ct) {
        var camera = _equip.Camera!;
        var focuser = _equip.Focuser!;
        int startPosition = focuser.Position;
        int half = request.Steps / 2;

        try {
            // Build sweep positions: symmetric around current, lowest first
            var positions = new List<int>();
            for (int i = -half; i <= half; i++) {
                if (positions.Count >= request.Steps) break;
                positions.Add(startPosition + i * request.StepSize);
            }

            // Optional backlash compensation: overshoot in one direction then move forward
            if (request.BacklashSteps > 0) {
                _logger.LogDebug("Backlash compensation: moving below first position by {Backlash} steps",
                    request.BacklashSteps);
                await focuser.MoveAbsoluteAsync(positions[0] - request.BacklashSteps, ct);
                await WaitForFocuserSettle(ct);
            }

            for (int i = 0; i < positions.Count; i++) {
                ct.ThrowIfCancellationRequested();

                int targetPos = positions[i];
                Progress = Progress with { CurrentSampleIndex = i, CurrentPosition = targetPos };

                _logger.LogDebug("AF sample {I}/{N}: moving to {Pos}", i + 1, positions.Count, targetPos);
                await focuser.MoveAbsoluteAsync(targetPos, ct);
                await WaitForFocuserSettle(ct);

                int actualPos = focuser.Position;
                var image = await camera.CaptureAsync(request.ExposureSeconds, ct);
                // Push each AF frame through the image relay so the
                // Focus tab preview canvas (and the Live canvas) can
                // render the sweep frames as the user watches the run.
                try { await _relay.RelayImageAsync(image, ct); }
                catch (Exception ex) { _logger.LogDebug(ex, "AF frame relay failed (non-fatal)"); }
                var hfr = MeasureHFR(image, request.MinStars);

                var point = new AutoFocusPoint {
                    Position = actualPos,
                    HFR = hfr.medianHfr,
                    StarCount = hfr.starCount
                };

                Progress.Points.Add(point);
                Progress = Progress with { LastHfr = point.HFR, LastStarCount = point.StarCount };

                _logger.LogInformation("AF sample {I}/{N}: pos={Pos} stars={Stars} HFR={HFR:F2}",
                    i + 1, positions.Count, actualPos, point.StarCount, point.HFR);
            }

            // Fit parabola only over points with valid HFR (star count > 0)
            var validPoints = Progress.Points.Where(p => p.StarCount > 0 && p.HFR > 0).ToList();

            if (validPoints.Count < 3) {
                throw new InvalidOperationException(
                    $"Not enough valid samples to fit parabola ({validPoints.Count} of {positions.Count})");
            }

            var fit = FitParabola(validPoints);
            int bestPosition = (int)Math.Round(fit.MinX);

            // Validate: best position should be inside (or near) the swept range
            int rangeMin = positions.Min();
            int rangeMax = positions.Max();
            int padding = request.StepSize * 2;
            if (bestPosition < rangeMin - padding || bestPosition > rangeMax + padding) {
                _logger.LogWarning(
                    "Fitted best position {Best} is far outside swept range [{Lo}..{Hi}] — fit unreliable",
                    bestPosition, rangeMin, rangeMax);
            }

            // Backlash compensation again for the final move
            if (request.BacklashSteps > 0 && bestPosition < focuser.Position) {
                await focuser.MoveAbsoluteAsync(bestPosition - request.BacklashSteps, ct);
                await WaitForFocuserSettle(ct);
            }

            await focuser.MoveAbsoluteAsync(bestPosition, ct);
            await WaitForFocuserSettle(ct);

            int finalPosition = focuser.Focuser_ReadCurrentSafely();

            // Optional: take a confirmation exposure to record the achieved HFR
            double? finalHfr = null;
            int? finalStars = null;
            if (request.TakeConfirmationFrame) {
                var image = await camera.CaptureAsync(request.ExposureSeconds, ct);
                try { await _relay.RelayImageAsync(image, ct); }
                catch (Exception ex) { _logger.LogDebug(ex, "AF confirmation frame relay failed (non-fatal)"); }
                var hfr = MeasureHFR(image, request.MinStars);
                finalHfr = hfr.medianHfr;
                finalStars = hfr.starCount;
            }

            var result = new AutoFocusResult {
                Success = true,
                StartPosition = startPosition,
                BestPosition = bestPosition,
                FinalPosition = finalPosition,
                BestPredictedHfr = fit.MinY,
                FinalMeasuredHfr = finalHfr,
                FinalStarCount = finalStars,
                Points = new List<AutoFocusPoint>(Progress.Points),
                FitA = fit.A, FitB = fit.B, FitC = fit.C,
                StartedAt = Progress.StartedAt,
                CompletedAt = DateTime.UtcNow
            };

            lock (_stateLock) {
                LastResult = result;
                State = AutoFocusState.Idle;
            }

            _logger.LogInformation(
                "Auto-focus complete: start={Start} best={Best} final={Final} predictedHFR={HFR:F2}",
                startPosition, bestPosition, finalPosition, fit.MinY);

        } catch (OperationCanceledException) {
            _logger.LogInformation("Auto-focus cancelled — restoring start position {Pos}", startPosition);
            try { await focuser.MoveAbsoluteAsync(startPosition, CancellationToken.None); } catch { }
            lock (_stateLock) {
                LastResult = new AutoFocusResult {
                    Success = false,
                    StartPosition = startPosition,
                    BestPosition = startPosition,
                    FinalPosition = startPosition,
                    Points = new List<AutoFocusPoint>(Progress.Points),
                    StartedAt = Progress.StartedAt,
                    CompletedAt = DateTime.UtcNow,
                    Error = "Cancelled"
                };
                LastError = "Cancelled";
                State = AutoFocusState.Idle;
            }
        } catch (Exception ex) {
            _logger.LogError(ex, "Auto-focus failed — restoring start position {Pos}", startPosition);
            try { await focuser.MoveAbsoluteAsync(startPosition, CancellationToken.None); } catch { }
            lock (_stateLock) {
                LastError = ex.Message;
                LastResult = new AutoFocusResult {
                    Success = false,
                    StartPosition = startPosition,
                    BestPosition = startPosition,
                    FinalPosition = startPosition,
                    Points = new List<AutoFocusPoint>(Progress.Points),
                    StartedAt = Progress.StartedAt,
                    CompletedAt = DateTime.UtcNow,
                    Error = ex.Message
                };
                State = AutoFocusState.Idle;
            }
        }
    }

    private async Task WaitForFocuserSettle(CancellationToken ct) {
        var focuser = _equip.Focuser!;
        // Wait up to 30s for IsMoving to clear
        for (int i = 0; i < 60; i++) {
            ct.ThrowIfCancellationRequested();
            if (!focuser.IsMoving) {
                // small settle delay
                await Task.Delay(300, ct);
                return;
            }
            await Task.Delay(500, ct);
        }
        _logger.LogWarning("Focuser did not stop moving within 30s");
    }

    private (double medianHfr, int starCount) MeasureHFR(IImageData image, int minStars) {
        var detector = new StarDetector();
        var stars = detector.Detect(image.Data, image.Properties.Width, image.Properties.Height);

        if (stars.Count < minStars) {
            _logger.LogDebug("Only {Count} stars detected (min={Min}) — HFR unreliable", stars.Count, minStars);
            return (0, stars.Count);
        }

        // Use median HFR — robust against outliers
        var hfrs = stars.Select(s => s.HFR).OrderBy(h => h).ToList();
        double median = hfrs[hfrs.Count / 2];
        return (median, stars.Count);
    }

    // ----- Parabola fitting (public/static for unit testing) -----

    /// <summary>
    /// Least-squares parabola fit y = a*x² + b*x + c. Requires at least 3 points.
    /// Returns coefficients plus the vertex (MinX, MinY).
    /// </summary>
    public static ParabolaFit FitParabola(IReadOnlyList<AutoFocusPoint> points) {
        if (points.Count < 3)
            throw new ArgumentException("Need at least 3 points for parabola fit");

        int n = points.Count;
        double sumX = 0, sumX2 = 0, sumX3 = 0, sumX4 = 0;
        double sumY = 0, sumXY = 0, sumX2Y = 0;

        foreach (var p in points) {
            double x = p.Position;
            double y = p.HFR;
            sumX += x;
            sumX2 += x * x;
            sumX3 += x * x * x;
            sumX4 += x * x * x * x;
            sumY += y;
            sumXY += x * y;
            sumX2Y += x * x * y;
        }

        // Normal equations matrix:
        // | n     sumX    sumX2 |   |c|   |sumY  |
        // | sumX  sumX2   sumX3 | * |b| = |sumXY |
        // | sumX2 sumX3   sumX4 |   |a|   |sumX2Y|
        double[,] m = {
            { n,     sumX,  sumX2 },
            { sumX,  sumX2, sumX3 },
            { sumX2, sumX3, sumX4 }
        };
        double[] v = { sumY, sumXY, sumX2Y };

        var sol = Solve3x3(m, v);
        double c = sol[0], b = sol[1], a = sol[2];

        // Vertex of y = ax² + bx + c is at x = -b/(2a)
        if (Math.Abs(a) < 1e-12) {
            // Degenerate (line) — return point of minimum y in sample
            var min = points.OrderBy(p => p.HFR).First();
            return new ParabolaFit { A = a, B = b, C = c, MinX = min.Position, MinY = min.HFR };
        }

        double minX = -b / (2 * a);
        double minY = a * minX * minX + b * minX + c;

        return new ParabolaFit { A = a, B = b, C = c, MinX = minX, MinY = minY };
    }

    /// <summary>Cramer's rule for a 3x3 linear system.</summary>
    private static double[] Solve3x3(double[,] m, double[] v) {
        double det = Determinant3(m);
        if (Math.Abs(det) < 1e-12)
            throw new InvalidOperationException("Singular matrix in parabola fit");

        double[,] mx = (double[,])m.Clone();
        double[,] my = (double[,])m.Clone();
        double[,] mz = (double[,])m.Clone();

        for (int i = 0; i < 3; i++) {
            mx[i, 0] = v[i];
            my[i, 1] = v[i];
            mz[i, 2] = v[i];
        }

        return new[] {
            Determinant3(mx) / det,
            Determinant3(my) / det,
            Determinant3(mz) / det
        };
    }

    private static double Determinant3(double[,] m) {
        return m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1])
             - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0])
             + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);
    }
}

internal static class IndiFocuserExtensions {
    // Tiny helper to make the read site explicit; never throws.
    public static int Focuser_ReadCurrentSafely(this NINA.INDI.Devices.IndiFocuser f) {
        try { return f.Position; } catch { return 0; }
    }
}

public enum AutoFocusState { Idle, Running }

public class AutoFocusRequest {
    /// <summary>Number of focus positions to sample (odd, >= 3).</summary>
    public int Steps { get; set; } = 9;
    /// <summary>Distance in focuser units between consecutive samples.</summary>
    public int StepSize { get; set; } = 50;
    public double ExposureSeconds { get; set; } = 2.0;
    /// <summary>Skip a sample as 'no stars' below this count.</summary>
    public int MinStars { get; set; } = 5;
    /// <summary>Overshoot below the first position by this many steps to compensate backlash. 0 to disable.</summary>
    public int BacklashSteps { get; set; }
    public bool TakeConfirmationFrame { get; set; } = true;
}

public record AutoFocusProgress {
    public int Steps { get; init; }
    public int CurrentSampleIndex { get; init; } = -1;
    public int CurrentPosition { get; init; }
    public double LastHfr { get; init; }
    public int LastStarCount { get; init; }
    public List<AutoFocusPoint> Points { get; init; } = new();
    public DateTime StartedAt { get; init; }
}

public class AutoFocusPoint {
    public int Position { get; set; }
    public double HFR { get; set; }
    public int StarCount { get; set; }
}

public class AutoFocusResult {
    public bool Success { get; set; }
    public int StartPosition { get; set; }
    public int BestPosition { get; set; }
    public int FinalPosition { get; set; }
    public double BestPredictedHfr { get; set; }
    public double? FinalMeasuredHfr { get; set; }
    public int? FinalStarCount { get; set; }
    public List<AutoFocusPoint> Points { get; set; } = new();
    public double FitA { get; set; }
    public double FitB { get; set; }
    public double FitC { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime CompletedAt { get; set; }
    public string? Error { get; set; }
}

public class ParabolaFit {
    public double A { get; set; }
    public double B { get; set; }
    public double C { get; set; }
    public double MinX { get; set; }
    public double MinY { get; set; }
}
