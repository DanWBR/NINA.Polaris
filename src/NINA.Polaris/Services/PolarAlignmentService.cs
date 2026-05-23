using System.Collections.Concurrent;
using NINA.Image.FileFormat.FITS;
using NINA.Image.Interfaces;

namespace NINA.Polaris.Services;

/// <summary>
/// TPPA (Three-Point Polar Alignment) orchestrator. Multi-phase state
/// machine that mirrors PHD2CalibrationOrchestrator in shape:
///   - StartJob spins a Task.Run(RunAsync) with a CancellationTokenSource
///   - Job state is broadcast via JobUpdated → StatusStreamHandler folds
///     into /ws/status under polarAlignment
///   - Abort cancels the CTS, RunAsync's finally lands at Phase.Cancelled
///
/// PA-1 lays the skeleton (enum + records + stubs). PA-2 fills in the
/// capture/slew/solve loop. PA-3 plugs in the polar-axis math. PA-5
/// adds the continuous Refinement mode (sliding-window solve loop
/// while the user adjusts knobs).
///
/// Refinement uses a separate CTS so the user can Stop refinement
/// without affecting any in-progress TPPA job (in practice TPPA must
/// complete before Refine becomes available, but the lifecycle plumbing
/// is independent in case we want to allow re-running TPPA from a
/// refinement state later).
/// </summary>
public class PolarAlignmentService {
    private readonly EquipmentManager _equip;
    private readonly PlateSolveService _plateSolve;
    private readonly ProfileService _profiles;
    private readonly NotificationService _notify;
    private readonly ILogger<PolarAlignmentService> _logger;

    private readonly ConcurrentDictionary<string, PolarAlignmentJob> _jobs = new();

    /// <summary>Most recent job — Idle when nothing has run yet. The WS
    /// broadcaster reads this. Set to a fresh job by StartJob; mutated
    /// in-place by RunAsync; preserved post-completion so the UI can
    /// keep showing the last computed error vector.</summary>
    public PolarAlignmentJob? CurrentJob { get; private set; }

    /// <summary>Fires on every phase transition + every new solved
    /// point. StatusStreamHandler subscribes so it can push an
    /// immediate WS frame instead of waiting for the next 1Hz tick.</summary>
    public event Action<PolarAlignmentJob>? JobUpdated;

    public PolarAlignmentService(EquipmentManager equip,
                                 PlateSolveService plateSolve,
                                 ProfileService profiles,
                                 NotificationService notify,
                                 ILogger<PolarAlignmentService> logger) {
        _equip = equip;
        _plateSolve = plateSolve;
        _profiles = profiles;
        _notify = notify;
        _logger = logger;
    }

    public PolarAlignmentJob StartJob(PolarAlignmentOptions opts) {
        // Refuse to start a second TPPA on top of a running one — the
        // mount can't be in two places at once. Refinement is gated
        // separately (see StartRefinement).
        if (CurrentJob != null && CurrentJob.IsActive) {
            throw new InvalidOperationException(
                "A polar-alignment job is already in progress. Abort it first.");
        }

        var job = new PolarAlignmentJob {
            Id = Guid.NewGuid().ToString("N"),
            Options = opts,
            Phase = PolarAlignmentPhase.Preflight,
            Mode = "tppa",
            StartedAt = DateTime.UtcNow
        };
        _jobs[job.Id] = job;
        CurrentJob = job;
        job.Cts = new CancellationTokenSource();
        job.Task = Task.Run(() => RunAsync(job, job.Cts.Token));
        return job;
    }

    public PolarAlignmentJob? GetJob(string id) =>
        _jobs.TryGetValue(id, out var j) ? j : null;

    public void Abort(string id) {
        if (_jobs.TryGetValue(id, out var j)) {
            j.Cts?.Cancel();
        }
    }

    /// <summary>Cancel whatever job is currently active (TPPA or
    /// refinement). Convenience for the UI "Stop everything" button.</summary>
    public void AbortCurrent() {
        var j = CurrentJob;
        if (j != null && j.IsActive) {
            j.Cts?.Cancel();
        }
    }

    /// <summary>PA-5: kick off a continuous capture+solve refinement
    /// loop. Requires a completed TPPA job (so we have a baseline of
    /// 3 solved points). Implemented in PA-5 — stubbed here so the
    /// endpoint shape is stable from PA-1 forward.</summary>
    public PolarAlignmentJob StartRefinement() {
        throw new NotImplementedException("Refinement loop ships in PA-5.");
    }

    public void StopRefinement() {
        // PA-5 will replace this with CTS cancellation for the
        // refinement task. No-op until then.
    }

    private async Task RunAsync(PolarAlignmentJob job, CancellationToken ct) {
        // PA-2 implements the capture/slew/solve sequence. PA-3 wires
        // in the math at the Computing phase. For PA-1 this is a stub
        // so the endpoint surface compiles and the WS broadcaster has
        // something to serialize.
        try {
            SetPhase(job, PolarAlignmentPhase.Preflight);
            await Task.Delay(100, ct); // give the WS one tick to see Preflight
            Fail(job, "Polar alignment loop not yet implemented (PA-2).");
        } catch (OperationCanceledException) {
            SetPhase(job, PolarAlignmentPhase.Cancelled);
            job.CompletedAt = DateTime.UtcNow;
        } catch (Exception ex) {
            _logger.LogError(ex, "Polar alignment RunAsync crashed");
            Fail(job, ex.Message);
        }
    }

    private void SetPhase(PolarAlignmentJob job, PolarAlignmentPhase phase) {
        job.Phase = phase;
        try { JobUpdated?.Invoke(job); }
        catch (Exception ex) { _logger.LogDebug(ex, "JobUpdated handler threw"); }
    }

    private void Fail(PolarAlignmentJob job, string error) {
        job.LastError = error;
        job.Phase = PolarAlignmentPhase.Failed;
        job.CompletedAt = DateTime.UtcNow;
        _logger.LogWarning("Polar alignment failed: {Error}", error);
        try { JobUpdated?.Invoke(job); } catch { }
        _notify.Push("error", "Polar alignment failed: " + error);
    }

    /// <summary>Write an IImageData to a freshly-created temp FITS so
    /// the plate solver (which takes a file path, not a buffer) can
    /// consume it. Caller is responsible for deleting the file.
    /// Lives here rather than in ImageWriterService because that
    /// service writes to the configured ImageOutputDir using session
    /// metadata; for polar alignment we want a throwaway temp file.</summary>
    internal static string WriteTempFits(IImageData image) {
        var path = Path.Combine(Path.GetTempPath(),
            "polaris-polar-" + Guid.NewGuid().ToString("N") + ".fits");
        FITSWriter.Write(image, path);
        return path;
    }
}

/// <summary>User-supplied TPPA options. All fields have sensible defaults
/// from the active rig's profile — the UI typically passes the rig
/// values verbatim, but the orchestrator accepts overrides so a
/// follow-up "tighten alignment" run can use different exposure /
/// gain without writing them back to the profile.</summary>
public record PolarAlignmentOptions(
    int SlewStepDegrees = 30,
    double ExposureSeconds = 3.0,
    int SettleSeconds = 2,
    int Gain = 100);

/// <summary>One solved point in a TPPA run. The triple of these gets
/// fed into PolarAlignmentMath.ComputeError to derive the mount's
/// polar-axis offset.</summary>
public record PolarPoint(
    int Index,
    double RaHours,
    double DecDeg,
    double RotationDeg,
    DateTime AtUtc);

public class PolarAlignmentJob {
    public string Id { get; set; } = "";
    public PolarAlignmentOptions Options { get; set; } = new();
    public PolarAlignmentPhase Phase { get; set; } = PolarAlignmentPhase.Idle;
    public List<PolarPoint> Points { get; set; } = new();
    public double AzErrorArcsec { get; set; }
    public double AltErrorArcsec { get; set; }
    public double TotalErrorArcsec { get; set; }
    public string? LastError { get; set; }
    /// <summary>"tppa" for the initial 3-point run, "refine" for the
    /// continuous loop. Drives UI labelling.</summary>
    public string Mode { get; set; } = "tppa";
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    internal CancellationTokenSource? Cts { get; set; }
    internal Task? Task { get; set; }

    /// <summary>True while RunAsync is still chewing through phases.
    /// Used by the second-StartJob guard.</summary>
    public bool IsActive => Phase != PolarAlignmentPhase.Idle
                         && Phase != PolarAlignmentPhase.Ok
                         && Phase != PolarAlignmentPhase.Failed
                         && Phase != PolarAlignmentPhase.Cancelled;
}

public enum PolarAlignmentPhase {
    Idle,
    Preflight,
    MovingToPoint1,
    SolvingPoint1,
    MovingToPoint2,
    SolvingPoint2,
    MovingToPoint3,
    SolvingPoint3,
    Computing,
    /// <summary>Cleanup slew back to the user's original RA/Dec so the
    /// mount isn't left 60° off where they expected. Cosmetic — TPPA
    /// has already produced the error vector at this point.</summary>
    SlewingHome,
    Ok,
    Failed,
    Cancelled,
    /// <summary>PA-5: continuous capture+solve loop while the user
    /// adjusts the mount knobs.</summary>
    Refining,
}
