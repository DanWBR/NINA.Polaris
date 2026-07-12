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
//
// Run-flow semantics (out-then-in initial pass, trendline-arm-driven sweep
// extension, weighted fits, soft rejection, R² + bounds validation, reverse
// sweep under single-direction overshoot backlash) ported from N.I.N.A.
// desktop (MPL-2.0): NINA.WPF.Base/ViewModel/AutoFocus/AutoFocusVM.cs.
// Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com>
// and the N.I.N.A. contributors. That source code is subject to the terms of
// the Mozilla Public License, v. 2.0 (http://mozilla.org/MPL/2.0/).

using NINA.Image.ImageAnalysis;
using NINA.Image.ImageAnalysis.AutoFocus;
using NINA.Image.Interfaces;

namespace NINA.Polaris.Services;

/// <summary>
/// Auto-focus service — port of the N.I.N.A. desktop algorithm (AFPORT).
///
/// The sweep first moves OUT by OffsetSteps*StepSize and samples OffsetSteps+1
/// points sweeping back IN; it then keeps adding ONE point at a time on
/// whichever side of the curve minimum has fewer than OffsetSteps trendline
/// points (<see cref="AutoFocusSweepPlanner"/>) — so a start far from focus
/// grows the missing arm instead of building a one-sided curve. Every point
/// carries a 1-sigma error (HFR spread across stars, pooled over frames) and
/// every fit weights by 1/σ²; a no-stars point is soft-rejected (measure 0,
/// σ=1000) so the fits ignore it while the planner still counts it. The final
/// position comes from the configured <see cref="AFCurveFittingMethod"/>
/// (default TrendHyperbolic: trendline intersection averaged with the
/// hyperbola minimum), validated by per-fit R² gates, a vertex-in-range
/// bounds check and the worse-than-start guard, with reattempts.
///
/// Run parameters resolve from the active rig's
/// <see cref="AutoFocusSettings"/>; any non-null <see cref="AutoFocusRequest"/>
/// field overrides per run (so sequenced/triggered runs finally follow the
/// operator's tuning instead of hardcoded defaults).
/// </summary>
public class AutoFocusService {
    private readonly EquipmentManager _equip;
    private readonly ImageRelayService _relay;
    private readonly ActiveGuiderProvider _guiders;
    private readonly ProfileService _profiles;
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
                            ActiveGuiderProvider guiders,
                            ProfileService profiles,
                            ILogger<AutoFocusService> logger) {
        _equip = equip;
        _relay = relay;
        _guiders = guiders;
        _profiles = profiles;
        _logger = logger;
    }

    /// <summary>
    /// Resolve the camera + focuser pair for an auto-focus run from the
    /// optical train ("main" | "aux" | "guide"). A V-curve needs the camera
    /// that looks through the same optics as the focuser being moved, so the
    /// two are always paired by source rather than chosen independently.
    /// </summary>
    private (ICamera camera, IFocuser focuser, string source) ResolveDevices(string? focuserSource) {
        var source = (focuserSource ?? "main").Trim().ToLowerInvariant();
        ICamera? camera = source switch {
            "aux"   => _equip.AuxCamera,
            "guide" => _equip.GuideCamera,
            _       => _equip.Camera
        };
        IFocuser? focuser = source switch {
            "aux"   => _equip.AuxFocuser,
            "guide" => _equip.GuideFocuser,
            _       => _equip.Focuser
        };
        string label = source switch { "aux" => "aux", "guide" => "guide", _ => "" };
        if (camera == null)
            throw new InvalidOperationException(
                source == "main" ? "No camera connected"
                                 : $"No {label} camera connected");
        if (focuser == null)
            throw new InvalidOperationException(
                source == "main" ? "No focuser connected"
                                 : $"No {label} focuser connected");
        return (camera, focuser, source);
    }

    /// <summary>Capture one frame through the gate that matches the optical
    /// train. Main + aux each have their own static capture gate so the AF
    /// sweep never collides with another consumer of that camera; the guide
    /// camera has no shared gate (Start refuses to run while the guider owns
    /// it), so we capture it directly.</summary>
    private Task<IImageData> CaptureGated(string source, ICamera camera, double exposureSeconds,
                                          CancellationToken ct) => source switch {
        "aux"   => AuxCameraCaptureGate.RunAsync(() => camera.CaptureAsync(exposureSeconds, ct), ct),
        "guide" => camera.CaptureAsync(exposureSeconds, ct),
        _       => CameraCaptureGate.RunAsync(() => camera.CaptureAsync(exposureSeconds, ct), ct)
    };

    public void Start(AutoFocusRequest? request) {
        var options = AutoFocusRunOptions.Resolve(request, _profiles.ActiveEquipmentProfile.AutoFocus);

        lock (_stateLock) {
            if (State == AutoFocusState.Running)
                throw new InvalidOperationException("Auto-focus already running");

            // Validate the requested optical train (camera + focuser pair).
            var (_, _, source) = ResolveDevices(options.FocuserSource);

            // The guide scope shares its camera with the guider loop; running a
            // V-curve sweep while it is looping/guiding would yank the camera out
            // from under the guider. Require it stopped first.
            if (source == "guide") {
                var g = _guiders.Active;
                if (g.IsConnected && (g.IsGuiding || g.IsLooping))
                    throw new InvalidOperationException(
                        "Stop guiding/looping before auto-focusing the guide scope");
            }

            if (options.StepSize <= 0)
                throw new ArgumentException("StepSize must be positive");
            if (options.ExposureSeconds <= 0)
                throw new ArgumentException("ExposureSeconds must be positive");

            _cts = new CancellationTokenSource();
            State = AutoFocusState.Running;
            LastError = null;
            Progress = new AutoFocusProgress {
                // Progress denominator: the initial pass is OffsetSteps+1
                // points, then up to OffsetSteps-1 per side of extension in
                // the typical case; the hard cap is much higher, so show the
                // typical total (2*OffsetSteps+1) like the desktop chart does.
                Steps = options.OffsetSteps * 2 + 1,
                Points = new List<AutoFocusPoint>(),
                StartedAt = DateTime.UtcNow,
                Mode = "vcurve",
                Method = options.Method.ToString()
            };
        }

        _runTask = Task.Run(() => RunAsync(options, _cts!.Token));
        _logger.LogInformation(
            "Auto-focus started: source={Source} offsetSteps={Offset} stepSize={StepSize} exposure={Exp}s "
            + "frames/pt={Frames} method={Method} r2>={R2} backlash={In}/{Out} ({Model}) crop={Crop} brightest={N}",
            options.FocuserSource, options.OffsetSteps, options.StepSize, options.ExposureSeconds,
            options.FramesPerPoint, options.Method, options.RSquaredThreshold,
            options.BacklashIn, options.BacklashOut, options.OvershootBacklash ? "overshoot" : "absolute",
            options.InnerCropRatio, options.UseBrightestStars);
    }

    public void Abort() {
        lock (_stateLock) {
            if (State != AutoFocusState.Running) return;
            _cts?.Cancel();
            _logger.LogInformation("Auto-focus abort requested");
        }
    }

    private async Task RunAsync(AutoFocusRunOptions o, CancellationToken ct) {
        var (camera, focuser, source) = ResolveDevices(o.FocuserSource);
        int startPosition = focuser.Position;
        bool roiActive = false;

        try {
            var tracker = new AfStarTracker(o.UseBrightestStars);
            var backlash = new BacklashState();

            // Initial-HFR baseline at the start position. Desktop only takes
            // it when the R² gate is off; Polaris keeps it best-effort in all
            // cases because the worse-than-start guard (MaxHfrRatio) is a
            // field-proven independent check and the baseline frame doubles
            // as the full-frame reference that sizes the hardware ROI below.
            double initialHfr = 0;
            int fullWidth = 0, fullHeight = 0;
            try {
                var img0 = await CaptureGated(source, camera, o.ExposureSeconds, ct);
                fullWidth = img0.Properties.Width;
                fullHeight = img0.Properties.Height;
                try { await _relay.RelayImageAsync(img0, FrameKind.Focus, ct); } catch { }
                var m0 = MeasureFrame(img0, o, tracker);
                if (m0.measure > 0) initialHfr = m0.measure;
            } catch (OperationCanceledException) { throw; }
              catch (Exception ex) { _logger.LogDebug(ex, "AF initial-HFR frame failed (continuing)"); }

            // Hardware ROI: set the centered subframe ONCE for the whole run
            // (never per frame — INDI drivers wedge when CCD_FRAME is
            // rewritten every capture). Reset in finally. Falls back to the
            // software crop inside MeasureFrame when unsupported.
            if (o.InnerCropRatio < 1 && camera.Capabilities.SupportsRoi
                    && fullWidth > 0 && fullHeight > 0) {
                try {
                    int w = (int)Math.Round(fullWidth * o.InnerCropRatio);
                    int h = (int)Math.Round(fullHeight * o.InnerCropRatio);
                    int x = (fullWidth - w) / 2;
                    int y = (fullHeight - h) / 2;
                    await camera.SetSubframeAsync(x, y, w, h, ct);
                    roiActive = true;
                    o.HardwareRoiActive = true;   // MeasureFrame skips the software crop
                    _logger.LogInformation("AF ROI: {W}x{H}+{X}+{Y} (crop {Crop})", w, h, x, y, o.InnerCropRatio);
                } catch (Exception ex) {
                    _logger.LogWarning(ex, "AF ROI subframe failed, using full frame");
                }
            }

            // Reverse-sweep trick (desktop parity): with OVERSHOOT and only
            // BacklashIn configured, sweep in the opposite direction so every
            // sample move goes OUT and the single overshoot direction covers
            // the one reversal (the initial move).
            bool reverse = o.OvershootBacklash && o.BacklashIn > 0 && o.BacklashOut == 0;
            int sign = reverse ? -1 : 1;
            int maxPoints = AutoFocusSweepPlanner.MaxPointsFor(o.FramesPerPoint, o.OffsetSteps);

            string lastReason = "unknown";

            for (int attempt = 1; attempt <= o.Attempts; attempt++) {
                ct.ThrowIfCancellationRequested();

                if (attempt > 1) {
                    _logger.LogInformation("AF reattempt {N}/{Max} (previous: {Reason})",
                        attempt, o.Attempts, lastReason);
                    await MoveWithBacklashAsync(focuser, startPosition, o, backlash, ct);
                    tracker.Reset();
                }
                Progress = Progress with {
                    Points = new List<AutoFocusPoint>(), CurrentSampleIndex = -1, Attempt = attempt
                };

                // ---- Initial pass: move OUT, sweep IN through OffsetSteps+1 points ----
                int cursor = ClampToTravel(focuser, startPosition + sign * o.OffsetSteps * o.StepSize);
                for (int i = 0; i <= o.OffsetSteps; i++) {
                    ct.ThrowIfCancellationRequested();
                    await SamplePointAsync(cursor, camera, focuser, source, o, tracker, backlash, ct);
                    cursor = ClampToTravel(focuser, cursor - sign * o.StepSize);
                }

                // ---- Extension: grow whichever trendline arm is short ----
                while (true) {
                    ct.ThrowIfCancellationRequested();
                    var fitPointsNow = BuildFitPoints(Progress.Points);
                    var trend = new TrendlineFitting().Calculate(fitPointsNow);
                    var (action, target) = AutoFocusSweepPlanner.NextStep(
                        fitPointsNow, trend, o.OffsetSteps, o.StepSize, maxPoints);

                    if (action == AutoFocusSweepPlanner.SweepAction.Done) break;
                    if (action == AutoFocusSweepPlanner.SweepAction.FailNoTrend) {
                        // Not a single usable slope point: reattempting is
                        // meaningless (all-cloud / no stars). Desktop parity:
                        // restore and give up without reattempting.
                        throw new InvalidOperationException(
                            "Auto-focus found no usable V-curve slope (no stars / flat curve)");
                    }
                    if (action == AutoFocusSweepPlanner.SweepAction.FailPointLimit) {
                        _logger.LogWarning("AF point limit reached ({Max}); fitting what we have", maxPoints);
                        break;
                    }

                    int clamped = ClampToTravel(focuser, target);
                    double minX = Progress.Points.Min(p => (double)p.Position);
                    double maxX = Progress.Points.Max(p => (double)p.Position);
                    bool railed = action == AutoFocusSweepPlanner.SweepAction.SampleLeft
                        ? clamped >= minX : clamped <= maxX;
                    if (railed || focuser.Position == 0) {
                        // Travel rail / zero position: can't grow that arm any
                        // further — fit what we have and let the gates decide.
                        _logger.LogWarning("AF hit focuser travel limit while extending; fitting what we have");
                        break;
                    }

                    await SamplePointAsync(clamped, camera, focuser, source, o, tracker, backlash, ct);
                }

                // ---- Low-wing soft rejection (FIELD2-1, kept from the old
                // implementation but as ErrorY inflation instead of deletion:
                // detector-missed donuts read falsely LOW on the shoulders
                // and would drag any fit sideways). ----
                MarkLowWingOutliers(Progress.Points);

                // ---- Final fit + validation ----
                var fitPoints = BuildFitPoints(Progress.Points);
                var fitting = AutoFocusFitting.Calculate(fitPoints, o.Method);
                PublishFits(fitting);

                var final = fitting.FinalFocusPoint;
                int bestPosition = (int)Math.Round(final.X);
                double sampleMin = Progress.Points.Min(p => (double)p.Position);
                double sampleMax = Progress.Points.Max(p => (double)p.Position);

                string? reason = fitting.Validate(o.RSquaredThreshold);
                if (reason == null && (final.X < sampleMin || final.X > sampleMax)) {
                    reason = $"focus point {bestPosition} outside sampled range [{sampleMin}..{sampleMax}]";
                }
                if (reason != null) {
                    lastReason = reason;
                    _logger.LogWarning("AF attempt {N}/{Max} unreliable: {Reason}", attempt, o.Attempts, reason);
                    if (attempt < o.Attempts) continue;
                    throw new InvalidOperationException(
                        $"Auto-focus curve unreliable ({reason}) after {o.Attempts} attempt(s)");
                }

                // ---- Move to the result + confirmation ----
                await MoveWithBacklashAsync(focuser, bestPosition, o, backlash, ct);
                int finalPosition = focuser.Focuser_ReadCurrentSafely();

                double? finalHfr = null;
                int? finalStars = null;
                if (o.TakeConfirmationFrame) {
                    var (m, s, stars) = await MeasurePointFramesAsync(camera, source, o, tracker, ct);
                    finalHfr = m;
                    finalStars = stars;
                }

                if (initialHfr > 0 && finalHfr is > 0 && o.MaxHfrRatio > 0
                        && finalHfr.Value > initialHfr * o.MaxHfrRatio) {
                    lastReason = $"final HFR {finalHfr:F2} worse than initial {initialHfr:F2} (>{o.MaxHfrRatio:0.##}x)";
                    _logger.LogWarning("AF attempt {N}/{Max} rejected: {Reason}", attempt, o.Attempts, lastReason);
                    if (attempt < o.Attempts) continue;
                    throw new InvalidOperationException($"Auto-focus result worse than start ({lastReason})");
                }

                var result = new AutoFocusResult {
                    Success = true,
                    StartPosition = startPosition,
                    BestPosition = bestPosition,
                    FinalPosition = finalPosition,
                    BestPredictedHfr = final.Y,
                    FinalMeasuredHfr = finalHfr,
                    FinalStarCount = finalStars,
                    Points = new List<AutoFocusPoint>(Progress.Points),
                    // Legacy wire fields stay populated from the quadratic so
                    // older clients keep drawing a curve.
                    FitA = fitting.Quadratic.A2, FitB = fitting.Quadratic.A1, FitC = fitting.Quadratic.A0,
                    RSquared = PrimaryRSquared(fitting),
                    Fits = ToFitPayload(fitting),
                    Method = o.Method.ToString(),
                    InitialHfr = initialHfr,
                    Attempts = attempt,
                    StartedAt = Progress.StartedAt,
                    CompletedAt = DateTime.UtcNow
                };

                lock (_stateLock) {
                    LastResult = result;
                    State = AutoFocusState.Idle;
                }

                _logger.LogInformation(
                    "Auto-focus complete: start={Start} best={Best} final={Final} predictedHFR={HFR:F2} "
                    + "method={Method} R²={R2:F2} points={P} attempt={A}/{Max}",
                    startPosition, bestPosition, finalPosition, final.Y,
                    o.Method, result.RSquared, Progress.Points.Count, attempt, o.Attempts);
                return;
            }

        } catch (OperationCanceledException) {
            _logger.LogInformation("Auto-focus cancelled, restoring start position {Pos}", startPosition);
            try { await focuser.MoveAbsoluteAsync(startPosition, CancellationToken.None); } catch { }
            lock (_stateLock) {
                LastResult = FailedResult(startPosition, "Cancelled");
                LastError = "Cancelled";
                State = AutoFocusState.Idle;
            }
        } catch (Exception ex) {
            _logger.LogError(ex, "Auto-focus failed, restoring start position {Pos}", startPosition);
            try { await focuser.MoveAbsoluteAsync(startPosition, CancellationToken.None); } catch { }
            lock (_stateLock) {
                LastError = ex.Message;
                LastResult = FailedResult(startPosition, ex.Message);
                State = AutoFocusState.Idle;
            }
        } finally {
            if (roiActive) {
                try { await camera.SetSubframeAsync(0, 0, 0, 0, CancellationToken.None); }
                catch (Exception ex) { _logger.LogWarning(ex, "AF failed to clear ROI subframe"); }
            }
        }
    }

    private AutoFocusResult FailedResult(int startPosition, string error) => new() {
        Success = false,
        StartPosition = startPosition,
        BestPosition = startPosition,
        FinalPosition = startPosition,
        Points = new List<AutoFocusPoint>(Progress.Points),
        StartedAt = Progress.StartedAt,
        CompletedAt = DateTime.UtcNow,
        Error = error
    };

    /// <summary>Move to a sweep position (backlash-aware), measure a point
    /// (FramesPerPoint exposures) and append it to Progress with live fits.</summary>
    private async Task SamplePointAsync(int target, ICamera camera, IFocuser focuser, string source,
            AutoFocusRunOptions o, AfStarTracker tracker, BacklashState backlash, CancellationToken ct) {
        int logicalPos = await MoveWithBacklashAsync(focuser, target, o, backlash, ct);
        Progress = Progress with { CurrentPosition = logicalPos };

        var (measure, stdev, starCount) = await MeasurePointFramesAsync(camera, source, o, tracker, ct);

        var point = new AutoFocusPoint {
            Position = logicalPos,
            HFR = measure,
            HfrError = stdev,
            StarCount = starCount
        };
        // Copy-on-write: the WS status broadcaster and the REST status
        // endpoint serialize Progress.Points concurrently with this loop.
        // Swapping in a fresh list gives readers an immutable snapshot.
        Progress = Progress with {
            Points = new List<AutoFocusPoint>(Progress.Points) { point },
            CurrentSampleIndex = Progress.Points.Count,
            LastHfr = point.HFR,
            LastStarCount = point.StarCount
        };
        _logger.LogInformation("AF sample #{N}: pos={Pos} stars={Stars} HFR={HFR:F2}±{Err:F2}",
            Progress.Points.Count, logicalPos, starCount, measure, stdev);

        // Live fits after every point so the chart draws the growing curve.
        PublishFits(AutoFocusFitting.Calculate(BuildFitPoints(Progress.Points), o.Method));
    }

    /// <summary>Measure one sweep point: FramesPerPoint exposures, each
    /// yielding (mean star HFR, stdev across stars); frames are combined as
    /// mean-of-means with pooled stdev sqrt(Σσ²/N). Frames below MinStars
    /// contribute nothing; a point with NO starry frame is soft-rejected as
    /// (0, 1000) — near-zero weight in the fits, still counted by the
    /// planner. HFR is 0 rather than NaN because the points go straight onto
    /// the WS JSON payload (System.Text.Json rejects NaN).</summary>
    private async Task<(double measure, double stdev, int starCount)> MeasurePointFramesAsync(
            ICamera camera, string source, AutoFocusRunOptions o, AfStarTracker tracker,
            CancellationToken ct) {
        double sumMeasure = 0, sumVariance = 0;
        int goodFrames = 0, lastCount = 0;

        for (int i = 0; i < o.FramesPerPoint; i++) {
            ct.ThrowIfCancellationRequested();
            var image = await CaptureGated(source, camera, o.ExposureSeconds, ct);
            try { await _relay.RelayImageAsync(image, FrameKind.Focus, ct); }
            catch (Exception ex) { _logger.LogDebug(ex, "AF frame relay failed (non-fatal)"); }

            var (measure, stdev, count) = MeasureFrame(image, o, tracker);
            lastCount = count;
            if (measure > 0) {
                sumMeasure += measure;
                sumVariance += stdev * stdev;
                goodFrames++;
            }
        }

        if (goodFrames == 0) return (0, 1000, lastCount);
        return (sumMeasure / goodFrames, Math.Sqrt(sumVariance / goodFrames), lastCount);
    }

    /// <summary>Detect stars on one frame and compute (mean HFR, stdev across
    /// stars, star count). Applies the software center-crop when a hardware
    /// ROI isn't active, and the brightest-N tracker.</summary>
    private (double measure, double stdev, int starCount) MeasureFrame(
            IImageData image, AutoFocusRunOptions o, AfStarTracker tracker) {
        // FIELD2-1: tune the detector for autofocus, NOT the live-tracking
        // case the defaults target. A heavily-defocused star is a big faint
        // DONUT; EightConnected keeps the faint ring one blob (4-conn
        // shatters it into arcs reading HFR~1), CurveOfGrowthHfr measures the
        // 50%-enclosed-flux radius with local background subtracted so the
        // donut's true (large) radius is reported, and the size/HFR ceilings
        // are lifted so the shoulders of the V still register.
        var detector = new StarDetector {
            EightConnected   = true,
            CurveOfGrowthHfr = true,
            MaxStarSize = 20000,
            MaxHfr      = 200
        };

        ushort[] data = image.Data;
        int width = image.Properties.Width;
        int height = image.Properties.Height;

        // Software center-crop when the camera couldn't give us a real ROI.
        // Detection cost scales with area, so this is still a big speed win
        // on large sensors even without the readout/transfer savings.
        if (o.InnerCropRatio < 1 && !o.HardwareRoiActive) {
            int cw = Math.Max(64, (int)Math.Round(width * o.InnerCropRatio));
            int chh = Math.Max(64, (int)Math.Round(height * o.InnerCropRatio));
            int cx = (width - cw) / 2;
            int cy = (height - chh) / 2;
            var cropped = new ushort[cw * chh];
            for (int row = 0; row < chh; row++) {
                Array.Copy(data, (cy + row) * width + cx, cropped, row * cw, cw);
            }
            data = cropped;
            width = cw;
            height = chh;
        }

        var stars = detector.Detect(data, width, height);
        stars = tracker.Filter(stars);

        if (stars.Count < o.MinStars) {
            _logger.LogDebug("Only {Count} stars detected (min={Min}), point soft-rejected",
                stars.Count, o.MinStars);
            return (0, 1000, stars.Count);
        }

        // Robust central HFR across stars. At a fixed focuser position every
        // real star is defocused by the SAME amount, so their HFRs cluster
        // tightly; spurious detections — merged donuts, nebula structure, hot
        // regions — read far larger and, under a plain mean, spike the whole
        // point (the HFR 28-30 outliers that shatter an otherwise smooth
        // V-curve). Sigma-clip around the median (MAD-scaled) and average only
        // the survivors so the point tracks the true defocus size — what keeps
        // ASIAIR's curve uniform. The survivor stdev still feeds the 1/σ² fit
        // weights.
        var (mean, stdev, _) = RobustMeanHfr(stars.Select(s => (double)s.HFR).ToList());
        return (mean, stdev, stars.Count);
    }

    /// <summary>Robust per-frame central HFR: sigma-clip the per-star HFRs
    /// around their median (MAD-scaled, floored so a genuinely tight frame
    /// isn't over-trimmed) and return the (mean, sample-stdev, kept-count) of
    /// the survivors. Rejects the spurious large-HFR detections that make a
    /// plain mean jump around; pure math, unit-tested.</summary>
    public static (double mean, double stdev, int kept) RobustMeanHfr(IReadOnlyList<double> hfrs) {
        var h = hfrs.Where(x => x > 0 && !double.IsNaN(x)).OrderBy(x => x).ToList();
        if (h.Count == 0) return (0, 0, 0);
        if (h.Count <= 2) { double m0 = h.Average(); return (m0, 0, h.Count); }

        double median = MedianSorted(h);
        var absdev = h.Select(x => Math.Abs(x - median)).OrderBy(x => x).ToList();
        double sigma = 1.4826 * MedianSorted(absdev);            // MAD → σ estimate
        // Clip band: 3σ, but never tighter than a small fraction of the
        // median (+floor) so a naturally tight distribution keeps all stars.
        double band = Math.Max(3.0 * sigma, 0.15 * median + 0.05);
        var kept = h.Where(x => Math.Abs(x - median) <= band).ToList();
        if (kept.Count == 0) kept = h;

        double mean = kept.Average();
        double variance = kept.Count > 1
            ? kept.Sum(x => (x - mean) * (x - mean)) / (kept.Count - 1)
            : 0;
        return (mean, Math.Sqrt(variance), kept.Count);
    }

    /// <summary>Median of an already-ascending-sorted list.</summary>
    private static double MedianSorted(IReadOnlyList<double> sorted) {
        int n = sorted.Count;
        if (n == 0) return 0;
        return (n & 1) == 1 ? sorted[n / 2] : 0.5 * (sorted[n / 2 - 1] + sorted[n / 2]);
    }

    /// <summary>Fit-space projection of the sampled points: X = position,
    /// Y = HFR, ErrorY = stdev floored at 0.001 — except soft/low-wing
    /// rejected points which carry σ=1000 so their fit weight is ~1e-6.</summary>
    private static List<FocusPoint> BuildFitPoints(IReadOnlyList<AutoFocusPoint> points) {
        return points
            .OrderBy(p => p.Position)
            .Select(p => new FocusPoint(
                p.Position,
                p.Rejected ? 0 : p.HFR,
                p.Rejected || p.HFR <= 0 ? 1000 : Math.Max(0.001, p.HfrError)))
            .ToList();
    }

    /// <summary>
    /// Flag "low wing" samples: HFR readings that DROP as the star defocuses
    /// further from focus. A real V-curve is convex — moving away from the
    /// vertex, HFR only increases — so a wing sample lower than the highest
    /// HFR already seen on its way out is unphysical (detector missed the
    /// faint donut and latched onto noise). Flagged points keep their
    /// measured values for the chart but are soft-rejected in the fit
    /// (σ=1000 via <see cref="BuildFitPoints"/>).
    /// </summary>
    public static void MarkLowWingOutliers(IReadOnlyList<AutoFocusPoint> points, double tolFraction = 0.2) {
        var sorted = points.Where(p => p.HFR > 0 && !p.Rejected).OrderBy(p => p.Position).ToList();
        int n = sorted.Count;
        if (n < 5) return;   // too few to tell a wing from the bowl

        int vertex = 0;
        for (int i = 1; i < n; i++)
            if (sorted[i].HFR < sorted[vertex].HFR) vertex = i;
        double min = sorted.Min(p => p.HFR), max = sorted.Max(p => p.HFR);
        double range = max - min;
        if (range <= 0) return;
        double tol = tolFraction * range;

        var drop = new List<AutoFocusPoint>();
        double runMax = sorted[vertex].HFR;
        for (int i = vertex - 1; i >= 0; i--) {           // left wing, outward
            if (sorted[i].HFR < runMax - tol) drop.Add(sorted[i]);
            else runMax = Math.Max(runMax, sorted[i].HFR);
        }
        runMax = sorted[vertex].HFR;
        for (int i = vertex + 1; i < n; i++) {            // right wing, outward
            if (sorted[i].HFR < runMax - tol) drop.Add(sorted[i]);
            else runMax = Math.Max(runMax, sorted[i].HFR);
        }

        // Keep at least 3 measured points fittable; if over-zealous, bail.
        if (drop.Count == 0 || n - drop.Count < 3) return;
        foreach (var p in drop) p.Rejected = true;
    }

    private void PublishFits(AutoFocusFitting fitting) {
        Progress = Progress with { Fits = ToFitPayload(fitting) };
    }

    private static double PrimaryRSquared(AutoFocusFitting f) => f.Method switch {
        AFCurveFittingMethod.Parabolic or AFCurveFittingMethod.TrendParabolic => f.Quadratic.RSquared,
        AFCurveFittingMethod.Trendlines =>
            Math.Min(f.Trendlines.LeftTrend.RSquared, f.Trendlines.RightTrend.RSquared),
        _ => f.Hyperbolic.RSquared
    };

    private static AutoFocusFitPayload ToFitPayload(AutoFocusFitting f) => new() {
        Method = f.Method.ToString(),
        HyperbolicA = f.Hyperbolic.A,
        HyperbolicB = f.Hyperbolic.B,
        HyperbolicP = f.Hyperbolic.P,
        HyperbolicRSquared = f.Hyperbolic.RSquared,
        HasHyperbolic = f.Hyperbolic.HasFit,
        QuadA2 = f.Quadratic.A2,
        QuadA1 = f.Quadratic.A1,
        QuadA0 = f.Quadratic.A0,
        QuadRSquared = f.Quadratic.RSquared,
        HasQuad = f.Quadratic.HasFit,
        LeftSlope = f.Trendlines.LeftTrend.Slope,
        LeftIntercept = f.Trendlines.LeftTrend.Offset,
        LeftRSquared = f.Trendlines.LeftTrend.RSquared,
        LeftPoints = f.Trendlines.LeftTrend.DataPoints.Count,
        RightSlope = f.Trendlines.RightTrend.Slope,
        RightIntercept = f.Trendlines.RightTrend.Offset,
        RightRSquared = f.Trendlines.RightTrend.RSquared,
        RightPoints = f.Trendlines.RightTrend.DataPoints.Count,
        IntersectionX = f.Trendlines.Intersection.X,
        IntersectionY = f.Trendlines.Intersection.Y,
        FinalX = f.FinalFocusPoint.X,
        FinalY = f.FinalFocusPoint.Y
    };

    // ----- Focuser movement -----

    /// <summary>Per-run backlash bookkeeping for the ABSOLUTE model.</summary>
    private sealed class BacklashState {
        public int LastDirection;   // -1 in, +1 out, 0 unknown
        public int Offset;          // persistent position offset (ABSOLUTE)
    }

    /// <summary>
    /// Backlash-aware absolute move (port of the desktop compensation
    /// decorators). OVERSHOOT: when a direction's backlash is configured,
    /// overshoot PAST the target by that amount, settle, then approach the
    /// target — so the final approach always loads the gears the same way.
    /// ABSOLUTE: keep a persistent offset that grows/shrinks on every
    /// direction reversal; the commanded physical position includes it while
    /// the LOGICAL position (returned, recorded as the curve X) does not.
    /// </summary>
    private async Task<int> MoveWithBacklashAsync(IFocuser focuser, int target,
            AutoFocusRunOptions o, BacklashState st, CancellationToken ct) {
        target = ClampToTravel(focuser, target);
        int current = focuser.Position;
        if (target == current) return target;
        bool movingIn = target < current;

        if (!o.OvershootBacklash) {   // ABSOLUTE model
            int dir = movingIn ? -1 : 1;
            if (st.LastDirection != 0 && dir != st.LastDirection) {
                st.Offset += movingIn ? -o.BacklashIn : o.BacklashOut;
            }
            st.LastDirection = dir;
            await MoveAndSettleAsync(focuser, ClampToTravel(focuser, target + st.Offset), ct);
            return target;   // logical position (offset hidden, desktop parity)
        }

        // OVERSHOOT model
        if (movingIn && o.BacklashIn > 0) {
            int overshoot = ClampToTravel(focuser, target - o.BacklashIn);
            if (overshoot < target) await MoveAndSettleAsync(focuser, overshoot, ct);
        } else if (!movingIn && o.BacklashOut > 0) {
            int overshoot = ClampToTravel(focuser, target + o.BacklashOut);
            if (overshoot > target) await MoveAndSettleAsync(focuser, overshoot, ct);
        }
        await MoveAndSettleAsync(focuser, target, ct);
        return focuser.Position;
    }

    // Settle delay (ms) after the focuser reports it reached the target, to let
    // the optics/mechanics come to rest before the measurement exposure.
    private const int FocuserSettleMs = 300;

    /// <summary>Move the focuser to <paramref name="target"/> and do NOT return
    /// until it has actually arrived there and stopped.</summary>
    /// <remarks>
    /// The bug this fixes: INDI flips ABS_FOCUS_POSITION to Busy
    /// *asynchronously*, so polling <c>IsMoving</c> immediately after the move
    /// command reads the stale Idle state and returns while the motor is still
    /// travelling. The measurement then ran mid-move and the position read came
    /// back stale, so two consecutive samples landed at nearly the same motor
    /// position — wrecking the V-curve. Gating on "reached the requested
    /// position AND not moving" is deterministic regardless of how the driver
    /// sequences its Busy/Idle transitions or how fast the move completes.
    /// </remarks>
    private async Task MoveAndSettleAsync(IFocuser focuser, int target, CancellationToken ct) {
        // Clamp to the focuser's travel BEFORE commanding the move. Backends
        // like IndiFocuser clamp internally, so an out-of-range request would
        // stop at the rail while WaitForFocuserReached spins its full 60s
        // deadline waiting for a target the hardware can never report.
        target = ClampToTravel(focuser, target);
        await focuser.MoveAbsoluteAsync(target, ct);
        await WaitForFocuserReached(focuser, target, ct);
    }

    /// <summary>Clamp a requested focuser target to the device's physical
    /// travel. MaxPosition can read 0 before a driver populates it (INDI
    /// FOCUS_MAX); in that case only the lower bound is enforced.</summary>
    private static int ClampToTravel(IFocuser focuser, int target) {
        int max = 0;
        try { max = focuser.MaxPosition; } catch { }
        return max > 0 ? Math.Clamp(target, 0, max) : Math.Max(0, target);
    }

    private async Task WaitForFocuserReached(IFocuser focuser, int target, CancellationToken ct) {
        // Drivers may settle a step or two off the exact request; don't spin
        // forever chasing a position the hardware will never report verbatim.
        const int toleranceSteps = 2;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline) {
            ct.ThrowIfCancellationRequested();
            bool reached = Math.Abs(focuser.Position - target) <= toleranceSteps;
            if (reached && !focuser.IsMoving) {
                await Task.Delay(FocuserSettleMs, ct);
                // Re-confirm after the settle: some drivers briefly report Idle
                // mid-travel, so a single "not moving" reading isn't enough.
                if (!focuser.IsMoving && Math.Abs(focuser.Position - target) <= toleranceSteps)
                    return;
            }
            await Task.Delay(150, ct);
        }
        _logger.LogWarning("Focuser did not reach {Target} (now {Pos}, moving={Moving}) within 60s",
            target, focuser.Position, focuser.IsMoving);
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

/// <summary>
/// Per-run overrides for an auto-focus run. Every field is optional: null
/// falls back to the active rig's <see cref="AutoFocusSettings"/>, so
/// <c>new AutoFocusRequest()</c> (the sequencer instruction / trigger path)
/// runs with the operator's per-rig tuning. Legacy fields from the retired
/// grid/adaptive modes are still accepted and mapped
/// (<see cref="AutoFocusRunOptions.Resolve"/>).
/// </summary>
public class AutoFocusRequest {
    /// <summary>LEGACY (grid mode): total points. Mapped to
    /// OffsetSteps = Steps/2 when no explicit OffsetSteps is given.</summary>
    public int? Steps { get; set; }
    /// <summary>Distance in focuser units between consecutive samples.</summary>
    public int? StepSize { get; set; }
    public double? ExposureSeconds { get; set; }
    /// <summary>Soft-reject a sample as 'no stars' below this count.</summary>
    public int? MinStars { get; set; }
    /// <summary>LEGACY: single approach-from-below backlash. Mapped to
    /// BacklashIn when no explicit BacklashIn is given.</summary>
    public int? BacklashSteps { get; set; }
    public bool TakeConfirmationFrame { get; set; } = true;
    /// <summary>Optical train to focus: "main" (imaging camera + focuser),
    /// "aux", or "guide". The camera is paired to the focuser since a V-curve
    /// needs the camera looking through the same optics.</summary>
    public string FocuserSource { get; set; } = "main";

    /// <summary>Minimum R² the configured fitting method must reach
    /// (including BOTH trendline arms for TREND* methods). 0 disables.</summary>
    public double? RSquaredThreshold { get; set; }
    /// <summary>Full-sweep attempts before giving up when a quality gate fails.</summary>
    public int? Attempts { get; set; }
    /// <summary>Reject a run whose confirmation HFR is worse than the starting
    /// HFR by more than this factor. 0 disables.</summary>
    public double? MaxHfrRatio { get; set; }

    /// <summary>LEGACY (retired adaptive mode): accepted and ignored.</summary>
    public bool? Adaptive { get; set; }
    /// <summary>LEGACY (adaptive): mapped to OffsetSteps when no explicit
    /// OffsetSteps is given.</summary>
    public int? PointsPerSide { get; set; }
    /// <summary>LEGACY (adaptive): accepted and ignored; the point cap is now
    /// the desktop formula framesPerPoint*offsetSteps*10.</summary>
    public int? MaxPoints { get; set; }

    // ---- AFPORT fields ----
    /// <summary>Points required on each trendline arm; the initial pass is
    /// OffsetSteps+1 points from +OffsetSteps*StepSize back to start.</summary>
    public int? OffsetSteps { get; set; }
    /// <summary>Exposures averaged per sweep point.</summary>
    public int? FramesPerPoint { get; set; }
    /// <summary>TRENDLINES | PARABOLIC | TRENDPARABOLIC | HYPERBOLIC | TRENDHYPERBOLIC.</summary>
    public string? Method { get; set; }
    /// <summary>Centered crop ratio for AF frames (1 = full frame).</summary>
    public double? InnerCropRatio { get; set; }
    /// <summary>Track only the N brightest stars across the sweep (0 = all).</summary>
    public int? UseBrightestStars { get; set; }
    public int? BacklashIn { get; set; }
    public int? BacklashOut { get; set; }
    /// <summary>OVERSHOOT | ABSOLUTE.</summary>
    public string? BacklashModel { get; set; }
}

/// <summary>Fully-resolved run parameters (request overrides applied on top
/// of the rig profile). Pure + static Resolve so the fallback and legacy
/// mapping are unit-testable.</summary>
public sealed record AutoFocusRunOptions {
    public int StepSize { get; init; } = 50;
    public int OffsetSteps { get; init; } = 4;
    public double ExposureSeconds { get; init; } = 2.0;
    public int FramesPerPoint { get; init; } = 1;
    public AFCurveFittingMethod Method { get; init; } = AFCurveFittingMethod.TrendHyperbolic;
    public double RSquaredThreshold { get; init; } = 0.7;
    public int Attempts { get; init; } = 2;
    public double MaxHfrRatio { get; init; } = 1.15;
    public double InnerCropRatio { get; init; } = 1.0;
    public int UseBrightestStars { get; init; }
    public int BacklashIn { get; init; }
    public int BacklashOut { get; init; }
    public bool OvershootBacklash { get; init; } = true;
    public int MinStars { get; init; } = 5;
    public string FocuserSource { get; init; } = "main";
    public bool TakeConfirmationFrame { get; init; } = true;
    /// <summary>Set by the run when a hardware ROI was applied, so the
    /// software crop in MeasureFrame doesn't crop twice.</summary>
    public bool HardwareRoiActive { get; set; }

    public static AutoFocusRunOptions Resolve(AutoFocusRequest? req, AutoFocusSettings? profile) {
        var p = profile ?? new AutoFocusSettings();

        // Legacy mapping: an explicit OffsetSteps wins; otherwise the old
        // adaptive PointsPerSide, then half the old grid Steps.
        int offsetSteps = req?.OffsetSteps
            ?? req?.PointsPerSide
            ?? (req?.Steps is int s ? Math.Max(2, s / 2) : p.OffsetSteps);

        return new AutoFocusRunOptions {
            StepSize = Math.Max(1, req?.StepSize ?? p.StepSize),
            OffsetSteps = Math.Clamp(offsetSteps, 1, 10),
            ExposureSeconds = req?.ExposureSeconds ?? p.ExposureSeconds,
            FramesPerPoint = Math.Clamp(req?.FramesPerPoint ?? p.FramesPerPoint, 1, 10),
            Method = AutoFocusFitting.ParseMethod(req?.Method ?? p.Method),
            RSquaredThreshold = Math.Clamp(req?.RSquaredThreshold ?? p.RSquaredThreshold, 0, 1),
            Attempts = Math.Clamp(req?.Attempts ?? p.Attempts, 1, 5),
            MaxHfrRatio = Math.Max(0, req?.MaxHfrRatio ?? p.MaxHfrRatio),
            InnerCropRatio = Math.Clamp(req?.InnerCropRatio ?? p.InnerCropRatio, 0.1, 1.0),
            UseBrightestStars = Math.Max(0, req?.UseBrightestStars ?? p.UseBrightestStars),
            BacklashIn = Math.Max(0, req?.BacklashIn ?? req?.BacklashSteps ?? p.BacklashIn),
            BacklashOut = Math.Max(0, req?.BacklashOut ?? p.BacklashOut),
            OvershootBacklash = !string.Equals(
                req?.BacklashModel ?? p.BacklashModel, "ABSOLUTE",
                StringComparison.OrdinalIgnoreCase),
            MinStars = Math.Max(1, req?.MinStars ?? p.MinStars),
            FocuserSource = string.IsNullOrWhiteSpace(req?.FocuserSource) ? "main"
                : req!.FocuserSource.Trim().ToLowerInvariant(),
            TakeConfirmationFrame = req?.TakeConfirmationFrame ?? true
        };
    }
}

public record AutoFocusProgress {
    public int Steps { get; init; }
    public int CurrentSampleIndex { get; init; } = -1;
    public int CurrentPosition { get; init; }
    public double LastHfr { get; init; }
    public int LastStarCount { get; init; }
    public List<AutoFocusPoint> Points { get; init; } = new();
    public DateTime StartedAt { get; init; }
    /// <summary>Current attempt number (1-based) when the quality gate triggers
    /// a reattempt, so the UI can show "attempt 2/2".</summary>
    public int Attempt { get; init; } = 1;
    /// <summary>Sweep flavour; always "vcurve" since AFPORT (the legacy
    /// "grid"/"adaptive" modes were replaced by the desktop algorithm).</summary>
    public string Mode { get; init; } = "vcurve";
    /// <summary>Active curve-fitting method name (e.g. "TrendHyperbolic").</summary>
    public string Method { get; init; } = "";
    /// <summary>Live fit parameters recomputed after every sampled point so
    /// the chart can draw the hyperbola/trendlines as the sweep grows.</summary>
    public AutoFocusFitPayload? Fits { get; init; }
}

public class AutoFocusPoint {
    public int Position { get; set; }
    public double HFR { get; set; }
    /// <summary>1-sigma uncertainty of <see cref="HFR"/> (stdev across stars,
    /// pooled over frames). 1000 marks a soft-rejected no-stars sample.</summary>
    public double HfrError { get; set; }
    public int StarCount { get; set; }
    /// <summary>
    /// True when this sample was soft-rejected (low-wing outlier) and carries
    /// ~zero weight in the fits. Kept in the points list so the chart can
    /// still draw it (greyed) instead of silently dropping it.
    /// </summary>
    public bool Rejected { get; set; }
}

/// <summary>Wire payload with every fitted curve's parameters so the client
/// can draw the hyperbola, both trendline arms, their intersection and the
/// derived final focus point without re-deriving anything.</summary>
public class AutoFocusFitPayload {
    public string Method { get; set; } = "";
    public double HyperbolicA { get; set; }
    public double HyperbolicB { get; set; }
    public double HyperbolicP { get; set; }
    public double HyperbolicRSquared { get; set; }
    public bool HasHyperbolic { get; set; }
    public double QuadA2 { get; set; }
    public double QuadA1 { get; set; }
    public double QuadA0 { get; set; }
    public double QuadRSquared { get; set; }
    public bool HasQuad { get; set; }
    public double LeftSlope { get; set; }
    public double LeftIntercept { get; set; }
    public double LeftRSquared { get; set; }
    public int LeftPoints { get; set; }
    public double RightSlope { get; set; }
    public double RightIntercept { get; set; }
    public double RightRSquared { get; set; }
    public int RightPoints { get; set; }
    public double IntersectionX { get; set; }
    public double IntersectionY { get; set; }
    public double FinalX { get; set; }
    public double FinalY { get; set; }
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
    /// <summary>Legacy quadratic coefficients (kept populated for old clients
    /// that re-derive the curve from A/B/C).</summary>
    public double FitA { get; set; }
    public double FitB { get; set; }
    public double FitC { get; set; }
    /// <summary>R² of the accepted PRIMARY fit for the chosen method.</summary>
    public double RSquared { get; set; }
    /// <summary>Full fit parameters (hyperbola + parabola + trendlines).</summary>
    public AutoFocusFitPayload? Fits { get; set; }
    /// <summary>Curve fitting method the run used.</summary>
    public string Method { get; set; } = "";
    /// <summary>HFR measured at the start position before the sweep (0 if it
    /// couldn't be measured — too far out of focus).</summary>
    public double InitialHfr { get; set; }
    /// <summary>How many sweep attempts it took to land an accepted curve.</summary>
    public int Attempts { get; set; } = 1;
    public DateTime StartedAt { get; set; }
    public DateTime CompletedAt { get; set; }
    public string? Error { get; set; }
}
