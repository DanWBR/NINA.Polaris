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
/// Mutable per-session counters + last-applied master names for the
/// live-stack pre-processing pipeline (LSPP). Owned by
/// LiveStackingService; exposed via the WS status payload so the
/// LIVE-tab UI can show real-time "X calibrated / Y fallback" badges
/// without needing to poll a REST endpoint.
///
/// BGE and calibration counters are populated server-side in
/// LiveStackingService as each frame is integrated.
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

    // BGE ------------------------------------------------------------
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
        // BgeSupportedThisSession is recomputed per-frame, so leaving it
        // alone is fine -- the next frame overwrites it.
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

    /// <summary>Server-side BGE: increment the per-session
    /// counters one frame at a time as GraXpert BGE runs on the host.</summary>
    public void RecordServerBge(bool ok, string? error) {
        if (ok) FramesBgeProcessed++;
        else { FramesBgeFallback++; if (error != null) LastBgeError = error; }
    }
}