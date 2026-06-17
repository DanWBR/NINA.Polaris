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

// Data models / DTOs extracted from PolarAlignmentService.cs for readability.
// Plain serialisable types owned by PolarAlignmentService; no behaviour here.

namespace NINA.Polaris.Services;

/// <summary>RDPA-2: kick off a fresh rudimentary session. Target is
/// the bright object the user picked from the catalog dropdown or
/// typed in by RA/Dec. SlewToTarget=false skips the GoTo (for manual
/// mounts or when the operator already pointed by hand).</summary>
public record RudimentaryStartRequest(
    double TargetRaHours,
    double TargetDecDeg,
    string? TargetName,
    bool SlewToTarget = true,
    double ExposureSeconds = 3.0,
    int Gain = 100,
    int SettleSeconds = 2);

/// <summary>Outcome of a single rudimentary iteration (Start or
/// Re-solve). The job lives on in CurrentJob with TargetRaHours +
/// History populated so the UI can render the sparkline.</summary>
public record RudimentaryStepResult(
    bool Ok,
    string? Error,
    double SolvedRaHours,
    double SolvedDecDeg,
    double AzErrorArcsec,
    double AltErrorArcsec,
    double TotalErrorArcsec,
    int IterationCount);

/// <summary>One entry in the rudimentary iteration history. The UI
/// renders these as a sparkline so the user can see the error trend
/// downward (convergence) vs flat / increasing (still need work).</summary>
public record RudimentaryIteration(double TotalErrorArcsec, DateTime AtUtc);

/// <summary>User-supplied TPPA options. All fields have sensible defaults
/// from the active rig's profile, the UI typically passes the rig
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
    /// continuous loop, "rudimentary" for the single-target iterative
    /// workflow (RDPA). Drives UI labelling.</summary>
    public string Mode { get; set; } = "tppa";
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    internal CancellationTokenSource? Cts { get; set; }
    internal Task? Task { get; set; }

    /// <summary>RDPA-2: target the user picked for rudimentary mode.
    /// Null in TPPA mode. Persists across re-solve iterations so the
    /// math + sky markers always reference the same intended point.</summary>
    public double? TargetRaHours { get; set; }
    public double? TargetDecDeg { get; set; }
    public string? TargetName { get; set; }

    /// <summary>RDPA-2: most recent plate-solved pointing. Used by
    /// the sky-map markers (target vs actual) and the canvas arrow.</summary>
    public double? SolvedRaHours { get; set; }
    public double? SolvedDecDeg { get; set; }

    /// <summary>RDPA-2: rolling history of (totalError, at) per
    /// re-solve. UI renders a small sparkline so user can see the
    /// trend converge (or not) across knob adjustments. Capped at
    /// 20 entries in the service.</summary>
    public List<RudimentaryIteration> History { get; set; } = new();

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
    /// mount isn't left 60° off where they expected. Cosmetic, TPPA
    /// has already produced the error vector at this point.</summary>
    SlewingHome,
    Ok,
    Failed,
    Cancelled,
    /// <summary>PA-5: continuous capture+solve loop while the user
    /// adjusts the mount knobs.</summary>
    Refining,
    /// <summary>RDPA-2: rudimentary single-target alignment phases.
    /// Lumped under one mode but split into 3 sub-phases so the WS
    /// payload + UI status pill can show "Slewing to target" vs
    /// "Capturing" vs "Solving" without ambiguity.</summary>
    RudimentarySlewing,
    RudimentaryCapturing,
    RudimentarySolving,
}