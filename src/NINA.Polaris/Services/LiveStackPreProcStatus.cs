namespace NINA.Polaris.Services;

/// <summary>
/// Mutable per-session counters + last-applied master names for the
/// live-stack pre-processing pipeline (LSPP). Owned by
/// LiveStackingService; exposed via the WS status payload so the
/// LIVE-tab UI can show real-time "X calibrated / Y fallback" badges
/// without needing to poll a REST endpoint.
///
/// BGE counters are populated by client-stack-progress messages
/// (LSPP-5 wires app.js to post bgeApplied/bgeFallback flags into
/// LiveStackingService.InjectClientStackMetrics). Calibration
/// counters are populated server-side in LiveStackingService
/// directly (LSPP-4 splice).
///
/// All writes happen on the AddFrameAsync chain (serialised) so no
/// lock is needed. Reads from the WS broadcaster may race but the
/// fields are int / string -- a torn read is at worst a stale
/// counter for one tick.
/// </summary>
public class LiveStackPreProcStatus {
    // Calibration ----------------------------------------------------
    public int FramesCalibrated { get; private set; }
    public int FramesCalibrationFallback { get; private set; }
    public int FramesCalibrationNoMatch { get; private set; }
    public string? MasterDarkUsed { get; private set; }
    public string? MasterFlatUsed { get; private set; }
    public string? MasterBiasUsed { get; private set; }
    public string? LastCalibrationError { get; private set; }

    // BGE (client-side, MetricsOnly only) ----------------------------
    public bool BgeSupportedThisSession { get; set; }
    public int FramesBgeProcessed { get; private set; }
    public int FramesBgeFallback { get; private set; }
    public string? LastBgeError { get; private set; }

    public void Reset() {
        FramesCalibrated = 0;
        FramesCalibrationFallback = 0;
        FramesCalibrationNoMatch = 0;
        MasterDarkUsed = null;
        MasterFlatUsed = null;
        MasterBiasUsed = null;
        LastCalibrationError = null;
        FramesBgeProcessed = 0;
        FramesBgeFallback = 0;
        LastBgeError = null;
        // BgeSupportedThisSession is recomputed per-frame from the
        // current StackMode, so leaving it alone is fine -- the next
        // frame overwrites it.
    }

    public void RecordCalibrationApplied(PreProcessResult res) {
        FramesCalibrated++;
        // Hold the names so the UI can show "Currently using: dark=X,
        // flat=Y, bias=Z". They're stable for the session unless the
        // operator overrides via settings (which resets the cache).
        MasterDarkUsed = res.MasterDarkUsed;
        MasterFlatUsed = res.MasterFlatUsed;
        MasterBiasUsed = res.MasterBiasUsed;
        LastCalibrationError = null;
    }

    public void RecordCalibrationFallback(string? error) {
        FramesCalibrationFallback++;
        LastCalibrationError = error;
    }

    public void RecordCalibrationNoMatch() {
        FramesCalibrationNoMatch++;
        // Clear master names because nothing was applied this frame.
        MasterDarkUsed = null;
        MasterFlatUsed = null;
        MasterBiasUsed = null;
    }

    /// <summary>Called by client-stack-progress handler (LSPP-5) so
    /// the server-side WS broadcast can mirror the client-side BGE
    /// counters back to ALL connected browsers.</summary>
    public void InjectClientBgeMetrics(int processed, int fallback, string? error) {
        FramesBgeProcessed = processed;
        FramesBgeFallback = fallback;
        if (error != null) LastBgeError = error;
    }
}
