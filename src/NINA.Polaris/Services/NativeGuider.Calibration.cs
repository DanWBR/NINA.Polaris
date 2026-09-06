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

using NINA.Core.Enum;
using NINA.Guider.Portable;
using NINA.Image.Interfaces;
using PortableGuideStep = NINA.Guider.Portable.GuideStep;

namespace NINA.Polaris.Services;

// Part of the NativeGuider class — split from NativeGuider.cs for
// readability. See NativeGuider.cs for the type overview + fields.
public sealed partial class NativeGuider {
    // ----- Calibration -----

    private async Task CalibrateAsync(CancellationToken ct) {
        EnsureConnected();
        var cam = _equipment.GuideCamera!;
        var mount = _equipment.Telescope;
        if (mount == null || !mount.IsConnected || !mount.Capabilities.SupportsPulseGuide) {
            RaiseAlert("Calibration needs a connected, pulse-guide-capable mount.");
            return;
        }
        if (!_haveLock) {
            await AutoSelectStarAsync(ct);
            if (!_haveLock) return;
        }

        SetAppState("Calibrating");
        try { await cam.SetSubframeAsync(0, 0, 0, 0, ct); } catch { }

        double decRad = double.IsNaN(mount.Declination) ? double.NaN : mount.Declination * Deg2Rad;
        int stepMs = Math.Max(50, Rig.NativeCalibrationStepMs);
        // Threshold scales with frame so big sensors don't undershoot.
        var process = new CalibrationProcess(stepMs, 25.0, 60, decRad);

        // During calibration the star sweeps well beyond the normal search
        // window, so widen it to follow the moving star (we also re-centre the
        // search on the last measured position each step). Size it to cover one
        // pulse step plus margin so a coarse Calibration Step never loses lock.
        int calRegion = Math.Max(SearchRegion, 50);

        _calProgress = "Calibrating: locating star...";
        try {
            // Seed the process with the current centroid.
            var (curX, curY, found) = await FindStarWithRetryAsync(cam, ct);
            if (!found) { RaiseAlert("Calibration failed: star lost at start."); SetAppState("Stopped"); return; }
            // Track the star: re-centre the search window on the last position.
            _lockX = curX; _lockY = curY;
            // Pin the displayed crosshair here for the whole calibration; the star
            // itself is shown moving via its marker circle each step.
            _calAnchorX = curX; _calAnchorY = curY; _calAnchorActive = true;
            BuildView(curX, curY, 0, true);

            string? lastPhase = null;
            double phStartX = curX, phStartY = curY;
            int phaseStep = 0;
            double oX = curX, oY = curY; // calibration origin for the plot
            var raPts = new List<double[]>();
            var decPts = new List<double[]>();

            for (int i = 0; i < 200 && !ct.IsCancellationRequested; i++) {
                var step = process.Tick(curX, curY);
                if (step.Failed) {
                    RaiseAlert($"Calibration failed: {step.Phase}");
                    SetAppState("Stopped");
                    return;
                }
                if (step.Done) break;

                // Reset the per-phase step counter + reference position whenever
                // the calibration phase changes (West -> East -> Dec ...).
                if (step.Phase != lastPhase) {
                    lastPhase = step.Phase; phStartX = curX; phStartY = curY; phaseStep = 0;
                }
                phaseStep++;
                double dx = curX - phStartX, dy = curY - phStartY;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                _calProgress = $"{step.Phase}: step {phaseStep}, dist {dist:F1} px";

                if (step.Pulse && step.DurationMs > 0) {
                    try {
                        await mount.PulseGuideAsync(step.Direction, step.DurationMs, ct);
                    } catch (Exception ex) {
                        RaiseAlert($"Calibration pulse failed: {ex.Message}");
                        SetAppState("Stopped");
                        return;
                    }
                    await SettleAfterPulse(step.DurationMs, ct);
                }
                (curX, curY, found) = await FindStarWithRetryAsync(cam, ct, calRegion);
                if (!found) {
                    RaiseAlert("Calibration failed: star lost mid-sequence (no frame after retries — check guide camera USB/power, especially at slew/reversal).");
                    SetAppState("Stopped");
                    return;
                }
                _logger.LogInformation("Calibration {Phase} step {Step}: star at ({X:F1},{Y:F1}), {Dist:F1} px from phase start, SNR {Snr:F1}",
                    step.Phase, phaseStep, curX, curY, Math.Sqrt((curX - phStartX) * (curX - phStartX) + (curY - phStartY) * (curY - phStartY)), _lastFindSnr);
                // Re-centre the (wide) search window on the new position so the
                // next step keeps following the star as it sweeps.
                _lockX = curX; _lockY = curY;
                // Refresh the live view: crosshair stays at the anchor (see
                // BuildView), the star marker moves to its new measured spot.
                BuildView(curX, curY, 0, true);
                // Record the measured points per axis for the Review-Calibration plot.
                if (lastPhase != null && lastPhase.StartsWith("RA (") && raPts.Count < 80)
                    raPts.Add(new[] { curX - oX, curY - oY });
                else if (lastPhase != null && lastPhase.StartsWith("Dec (south") && decPts.Count < 80)
                    decPts.Add(new[] { curX - oX, curY - oY });
            }

            _calibration = process.Result;
            if (_calibration.IsValid) {
                // Stamp the pier side this calibration was measured on so a later
                // meridian flip can mirror it instead of forcing a recalibration.
                _calibration = _calibration with { CalibrationPierSide = mount.SideOfPier };
                // Re-lock at the recentred position.
                _lockX = curX; _lockY = curY;
                _calProgress = "Calibration complete";
                _calDetails = BuildCalibrationDetails(process, raPts, decPts, mount);
                PersistCalibration(process, raPts, decPts);
                _logger.LogInformation(
                    "Native calibration complete: xAngle={Xa:F3} xRate={Xr:F5} yAngle={Ya:F3} yRate={Yr:F5}",
                    _calibration.XAngle, _calibration.XRate, _calibration.YAngle, _calibration.YRate);
            } else {
                RaiseAlert("Calibration did not complete.");
            }
            SetAppState("Stopped");
        } catch (OperationCanceledException) {
            // Stop pressed during calibration: clean abort, not an error.
            _logger.LogInformation("Native calibration cancelled");
            SetAppState("Stopped");
        } finally {
            _calProgress = null;
            // Release the crosshair anchor so the live lock drives it again
            // (guiding pins the crosshair to the lock the star is held at).
            _calAnchorActive = false;
        }
    }

    /// <summary>Assemble the "Review Calibration" snapshot (rates in px/sec +
    /// arcsec/sec, angles, steps, geometry, and the measured RA/Dec plot
    /// points) shown in the GUIDE calibration panel.</summary>
    private object BuildCalibrationDetails(CalibrationProcess process,
            List<double[]> raPts, List<double[]> decPts, ITelescope mount) {
        var cal = _calibration;
        double scale = PixelScale; // arcsec/px
        int binning = Math.Clamp(Rig.NativeGuideBin <= 0 ? 1 : Rig.NativeGuideBin, 1, 4);
        double raPxPerSec = cal.XRate * 1000.0;
        double decPxPerSec = cal.YRate * 1000.0;
        double sidereal = 15.041; // arcsec/sec at the celestial equator
        return new {
            valid = cal.IsValid,
            raSteps = process.RaSteps,
            decSteps = process.DecSteps,
            backlashSteps = process.BacklashSteps,
            backlashMs = cal.BacklashMs,
            cameraAngleDeg = cal.XAngle * 180.0 / Math.PI,
            orthoErrorDeg = cal.OrthogonalityErrorDeg,
            raRatePxPerSec = raPxPerSec,
            decRatePxPerSec = decPxPerSec,
            raRateArcsecPerSec = raPxPerSec * scale,
            decRateArcsecPerSec = decPxPerSec * scale,
            expectedRateArcsecPerSec = sidereal, // mount tracks at ~sidereal; guide rate ~1x
            pixelScale = scale,
            binning,
            focalLengthMm = Rig.GuiderFocalLengthMm > 0 ? Rig.GuiderFocalLengthMm : DefaultGuiderFocalLengthMm,
            declinationDeg = mount.Declination,
            pierSide = mount.SideOfPier.ToString().Replace("pier", ""),
            createdAtUtc = DateTime.UtcNow.ToString("o"),
            raPoints = raPts,
            decPoints = decPts,
        };
    }

    /// <summary>Save the just-completed calibration to the active rig profile so
    /// it can be restored after an app restart.</summary>
    private void PersistCalibration(CalibrationProcess process,
            List<double[]> raPts, List<double[]> decPts) {
        var cal = _calibration;
        if (!cal.IsValid) return;
        var data = new NativeCalibrationData {
            XAngle = cal.XAngle, YAngle = cal.YAngle,
            XRate = cal.XRate, YRate = cal.YRate,
            DeclinationRad = cal.DeclinationRad,
            BacklashMs = cal.BacklashMs,
            PierSide = (int)cal.CalibrationPierSide,
            RaSteps = process.RaSteps, DecSteps = process.DecSteps,
            PixelScale = PixelScale,
            Binning = Math.Clamp(Rig.NativeGuideBin <= 0 ? 1 : Rig.NativeGuideBin, 1, 4),
            SavedAtUtc = DateTime.UtcNow.ToString("o"),
            // Persist the measured scatter so the restored Review panel can plot it.
            RaPoints = raPts.ToArray(),
            DecPoints = decPts.ToArray(),
        };
        var key = CalibrationKey();
        data.Key = key;
        const int cap = 12;  // keep a handful of equipment combos per rig
        try {
            _profiles.UpdateEquipmentProfile(Rig.Id, r => {
                r.NativeCalibration = data;          // legacy single slot (last cal)
                r.NativeCalibrations ??= new();
                // Replace any prior calibration for this exact equipment, then add.
                r.NativeCalibrations.RemoveAll(c =>
                    string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));
                r.NativeCalibrations.Add(data);
                if (r.NativeCalibrations.Count > cap)
                    r.NativeCalibrations.RemoveRange(0, r.NativeCalibrations.Count - cap);
            });
        } catch (Exception ex) { _logger.LogWarning(ex, "Failed to persist native calibration"); }
    }

    /// <summary>Restore the last saved calibration for this rig (if any) into the
    /// in-memory state, so guiding can start without recalibrating after a
    /// restart. Returns true when a calibration was restored.</summary>
    private bool TryRestoreCalibration() {
        // Prefer the calibration whose equipment signature matches the gear
        // currently fitted. This is what lets a rig hold several calibrations
        // and restore the right one after swapping equipment back and forth.
        var key = CalibrationKey();
        var list = Rig.NativeCalibrations;
        NativeCalibrationData? d = null;
        if (list is { Count: > 0 }) {
            d = list.LastOrDefault(c =>
                string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));
            // Keyed entries exist but none match the current equipment -> the
            // gear changed; do NOT restore a stale calibration. Say so instead of
            // leaving the operator to discover an empty calibration on their own:
            // the saved one is still there, it just does not belong to this gear
            // (field report 2026-09-05, "I lost the calibration").
            if (d == null) {
                LastRestoreMismatch =
                    $"{list.Count} saved calibration(s) for this rig, none for the gear fitted now. "
                    + "Recalibrate, or load one from a file in Calibration details.";
                _logger.LogInformation(
                    "Native guide: no stored calibration matches. Wanted key '{Key}'; stored: {Stored}",
                    key, string.Join(" | ", list.Select(c => c.Key)));
                return false;
            }
        } else {
            // No keyed entries (pre-migration rig): fall back to the legacy slot.
            d = Rig.NativeCalibration;
        }
        if (d == null) return false;
        _calibration = new GuideCalibration(d.XAngle, d.YAngle, d.XRate, d.YRate,
            d.DeclinationRad, true, d.BacklashMs, (PierSide)d.PierSide);
        // Minimal details snapshot (no plot points) so the Review panel shows the
        // restored numbers and flags it as restored.
        double raPxPerSec = d.XRate * 1000.0, decPxPerSec = d.YRate * 1000.0;
        _calDetails = new {
            valid = true,
            restored = true,
            raSteps = d.RaSteps, decSteps = d.DecSteps,
            backlashSteps = 0, backlashMs = d.BacklashMs,
            cameraAngleDeg = d.XAngle * 180.0 / Math.PI,
            orthoErrorDeg = _calibration.OrthogonalityErrorDeg,
            raRatePxPerSec = raPxPerSec, decRatePxPerSec = decPxPerSec,
            raRateArcsecPerSec = raPxPerSec * d.PixelScale,
            decRateArcsecPerSec = decPxPerSec * d.PixelScale,
            expectedRateArcsecPerSec = 15.041,
            pixelScale = d.PixelScale, binning = d.Binning,
            focalLengthMm = Rig.GuiderFocalLengthMm > 0 ? Rig.GuiderFocalLengthMm : DefaultGuiderFocalLengthMm,
            declinationDeg = double.IsNaN(d.DeclinationRad) ? double.NaN : d.DeclinationRad * 180.0 / Math.PI,
            pierSide = ((PierSide)d.PierSide).ToString().Replace("pier", ""),
            createdAtUtc = d.SavedAtUtc,
            raPoints = d.RaPoints ?? Array.Empty<double[]>(),
            decPoints = d.DecPoints ?? Array.Empty<double[]>(),
        };
        _logger.LogInformation("Restored saved native calibration from {When}", d.SavedAtUtc);
        return true;
    }

    /// <summary>Snapshot the active calibration as a portable record for "Save
    /// calibration to file". Prefers the persisted record for the gear currently
    /// fitted (it carries the steps + scatter for the Review plot); falls back to
    /// the legacy slot, then to synthesising from the live in-memory calibration.
    /// Returns null when there is nothing to save.</summary>
    public NativeCalibrationData? ExportCalibrationData() {
        var key = CalibrationKey();
        var persisted = Rig.NativeCalibrations?
            .LastOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase))
            ?? Rig.NativeCalibration;
        if (persisted != null) return persisted;
        if (!_calibration.IsValid) return null;
        return new NativeCalibrationData {
            Key = key,
            XAngle = _calibration.XAngle, YAngle = _calibration.YAngle,
            XRate = _calibration.XRate, YRate = _calibration.YRate,
            DeclinationRad = _calibration.DeclinationRad,
            BacklashMs = _calibration.BacklashMs,
            PierSide = (int)_calibration.CalibrationPierSide,
            PixelScale = PixelScale,
            Binning = Math.Clamp(Rig.NativeGuideBin <= 0 ? 1 : Rig.NativeGuideBin, 1, 4),
            SavedAtUtc = DateTime.UtcNow.ToString("o"),
        };
    }

    /// <summary>Load a calibration from a saved file into the running guider and
    /// persist it. Re-keys the record to the equipment currently fitted so it
    /// becomes the one restored for this gear from now on — the point of loading a
    /// known-good file is to make it stick — while keeping every measured value.
    /// Returns false when the file carries no usable rates.</summary>
    public bool ImportCalibrationData(NativeCalibrationData d) {
        if (d == null) return false;
        // A calibration with no rates would send the guider nowhere; reject it
        // rather than load a dead one.
        if (d.XRate == 0 && d.YRate == 0) {
            RaiseAlert("Calibration file has no guide rates.");
            return false;
        }
        d.Key = CalibrationKey();
        if (string.IsNullOrWhiteSpace(d.SavedAtUtc)) d.SavedAtUtc = DateTime.UtcNow.ToString("o");
        _calibration = new GuideCalibration(d.XAngle, d.YAngle, d.XRate, d.YRate,
            d.DeclinationRad, true, d.BacklashMs, (PierSide)d.PierSide);
        _raAlgo.Reset();
        _decAlgo.Reset();
        _backlashComp.Reset();
        const int cap = 12;
        try {
            _profiles.UpdateEquipmentProfile(Rig.Id, r => {
                r.NativeCalibration = d;
                r.NativeCalibrations ??= new();
                r.NativeCalibrations.RemoveAll(c =>
                    string.Equals(c.Key, d.Key, StringComparison.OrdinalIgnoreCase));
                r.NativeCalibrations.Add(d);
                if (r.NativeCalibrations.Count > cap)
                    r.NativeCalibrations.RemoveRange(0, r.NativeCalibrations.Count - cap);
            });
        } catch (Exception ex) { _logger.LogWarning(ex, "Failed to persist imported calibration"); }
        // Rebuild the Review-panel snapshot from the freshly persisted record.
        TryRestoreCalibration();
        RaiseInfo("Guide calibration loaded from file.");
        _logger.LogInformation(
            "Imported native calibration: xAngle={Xa:F3} xRate={Xr:F5} yAngle={Ya:F3} yRate={Yr:F5}",
            _calibration.XAngle, _calibration.XRate, _calibration.YAngle, _calibration.YRate);
        return true;
    }

}
