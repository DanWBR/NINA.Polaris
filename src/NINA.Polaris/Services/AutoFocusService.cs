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
                var image = await CameraCaptureGate.RunAsync(
                    () => camera.CaptureAsync(request.ExposureSeconds, ct), ct);
                // Push each AF frame through the image relay so the
                // Focus tab preview canvas (and the Live canvas) can
                // render the sweep frames as the user watches the run.
                try { await _relay.RelayImageAsync(image, FrameKind.Focus, ct); }
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

            // FIELD2-1: fit parabola only over points that cleared
            // the operator's min-stars threshold. The earlier filter
            // `StarCount > 0 && HFR > 0` accepted "I saw 1 noise blob"
            // points whose HFR was a random small value that yanked
            // the parabola fit sideways. MeasureHFR sentinels invalid
            // samples as HFR=0; the `>0` check survives both that and
            // the rare degenerate-flat-frame case.
            var validPoints = Progress.Points
                .Where(p => p.StarCount >= request.MinStars && p.HFR > 0)
                .ToList();

            if (validPoints.Count < 3) {
                throw new InvalidOperationException(
                    $"Not enough valid samples to fit parabola ({validPoints.Count} of {positions.Count})");
            }

            // Trim the flat shoulders first: on a fast scope / coarse step the
            // sweep extremes saturate (HFR stops growing), so the V-curve looks
            // like a skate ramp with a plateau on each side. Those plateaus are
            // many consistent points the residual sigma-clip below won't catch,
            // and they pull the parabola wider/flatter and shift the vertex off
            // true focus. Fit only the inner V. Dropped points are flagged (not
            // deleted) so the chart still shows them.
            var innerPoints = TrimPlateaus(validPoints);
            foreach (var p in validPoints) {
                if (!innerPoints.Contains(p)) p.Rejected = true;
            }
            int trimmedCount = validPoints.Count - innerPoints.Count;
            if (trimmedCount > 0) {
                _logger.LogInformation(
                    "AF: trimmed {Count} flat plateau point(s) from the V-curve shoulders before fitting",
                    trimmedCount);
            }

            // Reject spurious points: a single bad HFR (passing cloud, cosmic
            // ray, a satellite trail mis-measured as a tight star) sits far off
            // the V-curve and drags the least-squares parabola sideways, landing
            // the focuser away from true focus. Iteratively sigma-clip points
            // whose residual to the fit is an outlier, then refit on the
            // survivors. The dropped points are flagged (not deleted) so the
            // chart still shows them.
            var (fit, inliers, rejected) = FitParabolaRobust(innerPoints);
            foreach (var r in rejected) r.Rejected = true;
            if (rejected.Count > 0) {
                _logger.LogInformation(
                    "AF: ignored {Count} spurious point(s) at {Positions} (off the V-curve)",
                    rejected.Count, string.Join(", ", rejected.Select(p => p.Position)));
            }
            int bestPosition = (int)Math.Round(fit.MinX);

            // Validate: best position should be inside (or near) the swept range
            int rangeMin = positions.Min();
            int rangeMax = positions.Max();
            int padding = request.StepSize * 2;
            if (bestPosition < rangeMin - padding || bestPosition > rangeMax + padding) {
                _logger.LogWarning(
                    "Fitted best position {Best} is far outside swept range [{Lo}..{Hi}], fit unreliable",
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
                var image = await CameraCaptureGate.RunAsync(
                    () => camera.CaptureAsync(request.ExposureSeconds, ct), ct);
                try { await _relay.RelayImageAsync(image, FrameKind.Focus, ct); }
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
            _logger.LogInformation("Auto-focus cancelled, restoring start position {Pos}", startPosition);
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
            _logger.LogError(ex, "Auto-focus failed, restoring start position {Pos}", startPosition);
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
        // FIELD2-1: tune the detector for autofocus, NOT the live
        // tracking case the defaults target. A heavily defocused star
        // is a donut that the live-default 200-pixel MaxStarSize ceiling
        // (and the matching HFR<50 sanity gate) reject entirely. The
        // result: stars.Count = 0 at the sweep extremes -> the V-curve
        // gets zero / NaN points at the ends instead of forming a real
        // V. Loosen both knobs so the sweep can still recognise a
        // donut as a "star" (we don't care about precise photometry
        // here, only the HFR magnitude that drives the parabola fit).
        var detector = new StarDetector {
            MaxStarSize = 2000,    // donut on 478 mm + ASI224 sensor ~ 50 px diameter ≈ 2000 px area
            MaxHfr      = 100      // far-out-of-focus HFR can easily exceed the live default of 50
        };
        var stars = detector.Detect(image.Data, image.Properties.Width, image.Properties.Height);

        if (stars.Count < minStars) {
            _logger.LogDebug("Only {Count} stars detected (min={Min}), HFR unreliable", stars.Count, minStars);
            // FIELD2-1: return 0 (not NaN) because System.Text.Json
            // rejects NaN/Infinity by default and the V-curve points
            // are serialized straight onto the WS status payload --
            // a NaN here would 500 the broadcast. The downstream
            // filters (RunAsync below + the client's V-curve chart
            // helper) both treat HFR == 0 OR StarCount < minStars
            // as "invalid sample" and skip it cleanly.
            return (0, stars.Count);
        }

        // Use median HFR, robust against outliers
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
            // Degenerate (line), return point of minimum y in sample
            var min = points.OrderBy(p => p.HFR).First();
            return new ParabolaFit { A = a, B = b, C = c, MinX = min.Position, MinY = min.HFR };
        }

        double minX = -b / (2 * a);
        double minY = a * minX * minX + b * minX + c;

        return new ParabolaFit { A = a, B = b, C = c, MinX = minX, MinY = minY };
    }

    /// <summary>
    /// Robust parabola fit with iterative outlier rejection (sigma-clipping).
    /// Fits the V-curve, measures each sample's residual to the fit, and drops
    /// samples whose residual exceeds <paramref name="sigmaThreshold"/> times a
    /// robust scale estimate (median absolute deviation), then refits on the
    /// survivors. Repeats until nothing else is clipped, the iteration cap is
    /// hit, or removing more would leave fewer than 3 points.
    ///
    /// The MAD-based scale is used instead of the plain standard deviation
    /// precisely because a single gross outlier inflates the stddev enough to
    /// hide itself; the median is insensitive to it.
    /// </summary>
    /// <returns>
    /// The final fit, the inlier set it was computed from, and the rejected
    /// (spurious) samples in detection order.
    /// </returns>
    public static (ParabolaFit fit, List<AutoFocusPoint> inliers, List<AutoFocusPoint> rejected)
        FitParabolaRobust(IReadOnlyList<AutoFocusPoint> points,
                          double sigmaThreshold = 2.5,
                          int maxIterations = 5) {
        if (points.Count < 3)
            throw new ArgumentException("Need at least 3 points for parabola fit");

        var inliers = points.ToList();
        var rejected = new List<AutoFocusPoint>();
        var fit = FitParabola(inliers);

        for (int iter = 0; iter < maxIterations; iter++) {
            fit = FitParabola(inliers);

            var residuals = inliers
                .Select(p => p.HFR - (fit.A * p.Position * p.Position + fit.B * p.Position + fit.C))
                .ToList();

            double sigma = RobustSigma(residuals);
            if (sigma <= 1e-9) break;  // (near-)perfect fit, nothing to clip

            double threshold = sigmaThreshold * sigma;
            var keep = new List<AutoFocusPoint>();
            var drop = new List<AutoFocusPoint>();
            for (int i = 0; i < inliers.Count; i++) {
                if (Math.Abs(residuals[i]) > threshold) drop.Add(inliers[i]);
                else keep.Add(inliers[i]);
            }

            if (drop.Count == 0) break;   // converged: every survivor fits
            if (keep.Count < 3) break;    // refuse to clip below a fittable set

            rejected.AddRange(drop);
            inliers = keep;
        }

        fit = FitParabola(inliers);
        return (fit, inliers, rejected);
    }

    /// <summary>
    /// Trim the flat shoulders ("plateaus") from the ends of a V-curve before
    /// fitting. With a fast scope or a coarse step the extreme samples saturate:
    /// the star defocuses into a large blob whose measured HFR stops growing, so
    /// the curve looks like a skate ramp — a V with a flat platform on each side.
    /// Those platforms are many mutually-consistent points (not isolated
    /// outliers, so the residual sigma-clip in <see cref="FitParabolaRobust"/>
    /// won't catch them) and they drag a least-squares parabola wider and
    /// flatter, pushing the fitted vertex away from true focus. This keeps only
    /// the inner V: from each end it drops trailing points whose slope toward
    /// the vertex is nearly flat relative to the steepest slope on the curve.
    /// </summary>
    /// <param name="points">Valid sweep samples (any order).</param>
    /// <param name="levelTolFraction">Two trailing samples count as the same
    /// saturation level when their HFR differs by less than this fraction of
    /// the curve's HFR range. A shelf is only trimmed when it has at least two
    /// co-level samples, so a clean V (whose extreme is a single high point,
    /// not a flat run) is never trimmed.</param>
    /// <param name="minKeep">Never trim below this many points; if trimming
    /// would, the untrimmed set is returned so a fit is still possible.</param>
    /// <returns>The inner points, sorted by position.</returns>
    public static List<AutoFocusPoint> TrimPlateaus(
            IReadOnlyList<AutoFocusPoint> points,
            double levelTolFraction = 0.1,
            int minKeep = 5) {
        var sorted = points.OrderBy(p => p.Position).ToList();
        int n = sorted.Count;
        if (n <= minKeep) return sorted;

        // Vertex = lowest HFR (best focus). Plateaus are the high-HFR ends.
        int vertex = 0;
        double minHfr = sorted[0].HFR, maxHfr = sorted[0].HFR;
        for (int i = 1; i < n; i++) {
            if (sorted[i].HFR < sorted[vertex].HFR) vertex = i;
            if (sorted[i].HFR < minHfr) minHfr = sorted[i].HFR;
            if (sorted[i].HFR > maxHfr) maxHfr = sorted[i].HFR;
        }
        double range = maxHfr - minHfr;
        if (range <= 0) return sorted;   // perfectly flat — nothing to do
        double levelTol = levelTolFraction * range;

        // Right shelf: a contiguous run of trailing samples all co-level with
        // the extreme (within levelTol). It's a saturation plateau only when it
        // holds >= 2 samples — a clean V's extreme is a lone high point. Drop
        // the whole shelf; keep down to the sample just inside it.
        int rp = n - 1;
        while (rp - 1 > vertex && Math.Abs(sorted[rp - 1].HFR - sorted[n - 1].HFR) <= levelTol)
            rp--;
        int hi = (n - rp >= 2 && rp - 1 > vertex) ? rp - 1 : n - 1;

        // Left shelf: mirror from the low-position end.
        int lp = 0;
        while (lp + 1 < vertex && Math.Abs(sorted[lp + 1].HFR - sorted[0].HFR) <= levelTol)
            lp++;
        int lo = (lp + 1 >= 2 && lp + 1 < vertex) ? lp + 1 : 0;

        var trimmed = sorted.GetRange(lo, hi - lo + 1);
        return trimmed.Count >= minKeep ? trimmed : sorted;
    }

    /// <summary>
    /// Robust 1-sigma estimate from residuals via the median absolute deviation:
    /// 1.4826 * median(|r - median(r)|), the consistency factor making MAD match
    /// the standard deviation for normally distributed data.
    /// </summary>
    private static double RobustSigma(IReadOnlyList<double> residuals) {
        if (residuals.Count == 0) return 0;
        double med = Median(residuals);
        var absDev = residuals.Select(r => Math.Abs(r - med)).ToList();
        return 1.4826 * Median(absDev);
    }

    private static double Median(IReadOnlyList<double> values) {
        var sorted = values.OrderBy(x => x).ToList();
        int n = sorted.Count;
        if (n == 0) return 0;
        return (n % 2 == 1)
            ? sorted[n / 2]
            : 0.5 * (sorted[n / 2 - 1] + sorted[n / 2]);
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
    // Widened from IndiFocuser to IFocuser so AscomComFocuser and any
    // future backend get the same safe-read behaviour without the
    // call site caring about the concrete type.
    public static int Focuser_ReadCurrentSafely(this NINA.Image.Interfaces.IFocuser f) {
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
    /// <summary>
    /// True when the robust V-curve fit flagged this sample as a spurious
    /// outlier and excluded it from the parabola. Kept in the points list
    /// so the chart can still draw it (greyed/struck out) instead of
    /// silently dropping it.
    /// </summary>
    public bool Rejected { get; set; }
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