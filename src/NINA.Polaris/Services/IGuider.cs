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

    /// <summary>Image scale in arcsec/pixel of the guide camera + scope.</summary>
    double PixelScale { get; }

    string? LastAlert { get; }
    DateTime? LastAlertAt { get; }
    string? LastSettleStatus { get; }

    // Rolling guiding metrics (arcsec).
    double RmsRA { get; }
    double RmsDec { get; }
    double RmsTotal { get; }
    double PeakRA { get; }
    double PeakDec { get; }

    /// <summary>Snapshot of the recent guide-step ring buffer (oldest first).</summary>
    List<GuideStep> SnapshotSteps();

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

    // ---- Events ----

    event Action<string>? AppStateChanged;
    event Action<GuideStep>? GuideStepReceived;
    event Action<string>? Alert;
    event Action<SettleResult>? Settled;
}
