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

namespace NINA.Polaris.Services;

/// <summary>
/// Backend-agnostic guider contract. Both the external PHD2 integration
/// (<see cref="PHD2Client"/>) and the in-process native autoguider
/// (<see cref="NativeGuider"/>) implement this so GuiderEndpoints, the
/// status WebSocket and the GUIDE tab stay backend-neutral. A rig picks
/// the backend via <c>EquipmentProfile.GuiderDriver</c> and
/// <see cref="ActiveGuiderProvider"/> routes generic calls to the
/// active one.
///
/// <para>The DTO shapes (<see cref="GuideStep"/>, <see cref="SettleResult"/>,
/// <see cref="CalibrationData"/>) are the existing PHD2 records, reused
/// verbatim so the WebSocket JSON the frontend already reads stays
/// byte-identical regardless of which backend is active.</para>
/// </summary>
public interface IGuider {
    /// <summary>Backend identifier surfaced to the UI: "phd2" or "native".</summary>
    string Backend { get; }

    bool IsConnected { get; }

    /// <summary>Backend application state. PHD2 vocabulary is the canonical
    /// set (Stopped / Selected / Calibrating / Guiding / LostLock / Paused /
    /// Looping); the native backend maps onto the same strings so the UI
    /// stays unchanged.</summary>
    string AppState { get; }

    bool IsGuiding { get; }
    bool IsCalibrating { get; }
    bool IsPaused { get; }
    bool IsLooping { get; }
    bool IsSettling { get; }

    /// <summary>True while a dither offset is being chased back to a new lock
    /// point (until its settle completes). Lets the UI show a distinct
    /// "Dithering" state. Backends without a separate dither phase return
    /// false (PHD2 surfaces dither via its own settle reporting).</summary>
    bool IsDithering => false;

    /// <summary>Live settle telemetry for an ASIAIR-style readout (current total
    /// error vs the tolerance, time within tolerance vs the required settle time,
    /// elapsed vs timeout), or null when not settling. Native backend only;
    /// PHD2 reports settle through its own event stream.</summary>
    object? SettleProgress => null;

    /// <summary>RA/Dec correction aggressiveness (0..2 fraction of the error
    /// corrected per frame). Surfaced so the UI shows the live value; native
    /// reads it from the rig profile. PHD2 manages its own, so defaults stand.</summary>
    double RaAggression => 0.70;
    double DecAggression => 0.70;

    /// <summary>Image scale in arcsec/pixel of the guide camera + scope.</summary>
    double PixelScale { get; }

    string? LastAlert { get; }
    DateTime? LastAlertAt { get; }
    /// <summary>Severity of <see cref="LastAlert"/> for UI styling:
    /// "info" | "warn" | "error". Defaults to "warn" so existing backends
    /// (PHD2) keep their current red/amber callout without changes.</summary>
    string LastAlertSeverity => "warn";
    string? LastSettleStatus { get; }

    /// <summary>Human-readable calibration step shown in the GUIDE UI while
    /// calibrating (e.g. "Dec (south): step 4, dist 12.3 px"). Null when not
    /// calibrating. Default null for backends that don't report it (PHD2).</summary>
    string? CalibrationProgress => null;

    /// <summary>Snapshot of the last completed calibration (rates, angles,
    /// steps, geometry, RA/Dec plot points) for the GUIDE "Review Calibration"
    /// panel. Null until one completes. Default null (PHD2).</summary>
    object? CalibrationDetails => null;

    /// <summary>Native dark library / bad-pixel-map status (mode, build
    /// progress, whether a matching master dark / defect map is loaded) for
    /// the GUIDE calibration card. Native backend only; null for PHD2, which
    /// manages its own dark library in its GUI.</summary>
    object? DarkCalibration => null;

    /// <summary>Current guide-camera exposure in milliseconds, surfaced to the
    /// GUIDE panel's exposure field. 0 when not reported by the backend.</summary>
    int ExposureMs => 0;

    // Rolling guiding metrics (arcsec).
    double RmsRA { get; }
    double RmsDec { get; }
    double RmsTotal { get; }
    double PeakRA { get; }
    double PeakDec { get; }

    /// <summary>Snapshot of the recent guide-step ring buffer (oldest first).</summary>
    List<GuideStep> SnapshotSteps();

    /// <summary>Optional live guide-frame view for the PHD2-style GUIDE UI:
    /// frame dimensions/origin, the lock position, tracked star markers and a
    /// star-profile cross-section. Null when the backend does not expose frames
    /// (PHD2, which renders its own GUI). The native backend overrides it.</summary>
    object? ViewState => null;

    /// <summary>Clear the recent-step ring buffer + reset RMS/peak.</summary>
    void ClearStepHistory();

    // ---- Connection ----

    /// <summary>Connect the backend. For PHD2 the host/port address its
    /// event-server socket; the native backend ignores them (it uses the
    /// rig's selected guide camera + mount) but keeps the signature so the
    /// connect route is mechanical.</summary>
    Task ConnectAsync(string host = "localhost", int port = 4400, CancellationToken ct = default);

    Task DisconnectAsync(CancellationToken ct = default);

    // ---- Commands ----

    Task StartGuidingAsync(double settlePixels = 1.5, int settleTime = 10,
        int settleTimeout = 40, bool recalibrate = false, CancellationToken ct = default);

    Task StopAsync(CancellationToken ct = default);

    Task LoopAsync(CancellationToken ct = default);

    Task PauseAsync(CancellationToken ct = default);

    Task ResumeAsync(CancellationToken ct = default);

    Task DitherAsync(double pixels = 5.0, bool raOnly = false, double settlePixels = 1.5,
        int settleTime = 10, int settleTimeout = 40, CancellationToken ct = default);

    Task SetExposureAsync(int milliseconds, CancellationToken ct = default);

    /// <summary>Auto-select a guide star (PHD2 find_star / native star detect).</summary>
    Task AutoSelectStarAsync(CancellationToken ct = default);

    Task ClearCalibrationAsync(CancellationToken ct = default);

    /// <summary>Manually mirror the stored calibration for a German-equatorial
    /// meridian flip (RA angle +180°, optional Dec reverse) without a full
    /// recalibration. This is the manual counterpart to the native backend's
    /// automatic pier-side handler, needed when the mount driver does not report
    /// SideOfPier (so the auto-mirror can never trigger). Default no-op: PHD2
    /// flips its own calibration server-side.</summary>
    Task FlipCalibrationAsync(CancellationToken ct = default) => Task.CompletedTask;

    // ---- Events ----

    event Action<string>? AppStateChanged;
    event Action<GuideStep>? GuideStepReceived;
    event Action<string>? Alert;
    event Action<SettleResult>? Settled;
}