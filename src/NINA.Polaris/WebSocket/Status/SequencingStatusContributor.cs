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

using NINA.Polaris.Services;

namespace NINA.Polaris.WebSocket.Status;

/// <summary>
/// Sequences, plans, and the routines that run inside them.
///
/// Blocks owned: sequence, advSeq, plan, autoFocus, meridianFlip, flatWizard.
/// </summary>
public sealed class SequencingStatusContributor : IStatusContributor {
    private readonly NINA.Polaris.Services.Sequencer.AdvancedSequenceEngine _advEngine;
    private readonly AutoFocusService _autoFocus;
    private readonly EquipmentManager _equip;
    private readonly FlatWizardService _flatWizard;
    private readonly MeridianFlipService _meridianFlip;
    private readonly NINA.Polaris.Services.Plan.PlanRunnerService _planRunner;
    private readonly ProfileService _profile;
    private readonly MountSafetyGuardService _safetyGuard;
    private readonly SequenceEngine _sequence;

    public SequencingStatusContributor(NINA.Polaris.Services.Sequencer.AdvancedSequenceEngine advEngine, AutoFocusService autoFocus, EquipmentManager equip, FlatWizardService flatWizard, MeridianFlipService meridianFlip, NINA.Polaris.Services.Plan.PlanRunnerService planRunner, ProfileService profile, MountSafetyGuardService safetyGuard, SequenceEngine sequence) {
        _advEngine = advEngine;
        _autoFocus = autoFocus;
        _equip = equip;
        _flatWizard = flatWizard;
        _meridianFlip = meridianFlip;
        _planRunner = planRunner;
        _profile = profile;
        _safetyGuard = safetyGuard;
        _sequence = sequence;
    }

    public IReadOnlyCollection<string> Keys { get; } = new[] { "sequence", "advSeq", "plan", "autoFocus", "meridianFlip", "flatWizard" };

    public void Contribute(StatusTick tick) {
        var advEngine = _advEngine;
        var autoFocus = _autoFocus;
        var equip = _equip;
        var flatWizard = _flatWizard;
        var meridianFlip = _meridianFlip;
        var planRunner = _planRunner;
        var profile = _profile;
        var safetyGuard = _safetyGuard;
        var sequence = _sequence;

        var seqStatus = sequence.GetStatus();

        // Meridian flip live status (LST + time-to-meridian for the current mount RA)
        double? lstHours = null, hourAngleHours = null, timeToMeridianHours = null;
        double? timeToFlipHours = null, timeToSetHours = null;
        if (equip.Telescope != null && equip.Telescope.IsConnected) {
            var raHours = equip.Telescope.RightAscension;
            if (!double.IsNaN(raHours)) {
                lstHours = MeridianFlipService.ComputeLstHours(DateTime.UtcNow, profile.Active.Longitude);
                var ha = lstHours.Value - raHours;
                while (ha > 12) ha -= 24;
                while (ha < -12) ha += 24;
                hourAngleHours = ha;
                timeToMeridianHours = MeridianFlipService.HoursUntilMeridian(
                    raHours, DateTime.UtcNow, profile.Active.Longitude);
                // Time until the flip point (HA = MinutesAfterMeridian);
                // drives the LIVE-tab countdown.
                timeToFlipHours = MeridianFlipService.HoursUntilFlip(
                    raHours, DateTime.UtcNow, profile.Active.Longitude,
                    meridianFlip.Settings.MinutesAfterMeridian);
                // Time until the target sets below the horizon. For a
                // target already past the meridian (descending west),
                // this is the useful countdown — "time until meridian"
                // is meaningless there (it already crossed). Needs Dec.
                var decDeg = equip.Telescope.Declination;
                if (!double.IsNaN(decDeg)) {
                    timeToSetHours = AltitudeService.HoursUntilSet(
                        raHours, decDeg, DateTime.UtcNow,
                        profile.Active.Latitude, profile.Active.Longitude);
                }
            }
        }

        var meridianPayload = new {
            state = meridianFlip.State.ToString().ToLowerInvariant(),
            settings = meridianFlip.Settings,
            flipsCompleted = meridianFlip.FlipsCompleted,
            lastFlipAt = meridianFlip.LastFlipAt,
            lastFlipError = meridianFlip.LastFlipError,
            lstHours,
            hourAngleHours,
            timeToMeridianHours,
            timeToMeridianMinutes = timeToMeridianHours * 60,
            timeToFlipHours,
            timeToFlipMinutes = timeToFlipHours * 60,
            timeToSetHours,
            timeToSetMinutes = timeToSetHours * 60,
            safetyTripped = safetyGuard.Tripped,
            safetyReason = safetyGuard.TripReason,
            safetyTrippedAt = safetyGuard.TrippedAt
        };

        var autoFocusPayload = new {
            state = autoFocus.State.ToString().ToLowerInvariant(),
            currentSampleIndex = autoFocus.Progress.CurrentSampleIndex,
            steps = autoFocus.Progress.Steps,
            lastHfr = autoFocus.Progress.LastHfr,
            lastStarCount = autoFocus.Progress.LastStarCount,
            points = autoFocus.Progress.Points,
            bestPosition = autoFocus.LastResult?.BestPosition,
            bestHfr = autoFocus.LastResult?.BestPredictedHfr,
            success = autoFocus.LastResult?.Success,
            mode = autoFocus.Progress.Mode,
            // AFPORT (additive): live fit parameters + method +
            // attempt so the chart draws the hyperbola/trendlines
            // while the sweep grows.
            method = autoFocus.Progress.Method,
            attempt = autoFocus.Progress.Attempt,
            fits = autoFocus.Progress.Fits,
            // Which pass is running ("sweep" / "refining") plus where
            // the focuser is. The refinement pass steps by a quarter
            // of the coarse step and does NOT advance the sample
            // counter, so with neither of these the panel looks
            // frozen while it works.
            phase = autoFocus.Progress.Phase,
            currentPosition = autoFocus.Progress.CurrentPosition,
            // The tracked star + the frame it was measured in, so
            // the FOCUS panel can mark it and magnify it.
            starX = autoFocus.Progress.StarX,
            starY = autoFocus.Progress.StarY,
            starHfr = autoFocus.Progress.StarHfr,
            frameWidth = autoFocus.Progress.FrameWidth,
            frameHeight = autoFocus.Progress.FrameHeight
        };

            tick.Blocks["sequence"] = new {
                state = seqStatus.State,
                currentItemIndex = seqStatus.CurrentItemIndex,
                currentFrameInItem = seqStatus.CurrentFrameInItem,
                totalFrames = seqStatus.TotalFrames,
                totalFramesCompleted = seqStatus.TotalFramesCompleted,
                elapsedSeconds = seqStatus.ElapsedSeconds,
                estimatedRemainingSeconds = seqStatus.EstimatedRemainingSeconds,
                lastError = seqStatus.LastError,
                items = seqStatus.Items,
                dithersIssued = seqStatus.DithersIssued,
                framesSinceDither = seqStatus.FramesSinceDither,
                dither = seqStatus.Dither
            };

            // Camera video-stream lifecycle (PREVIEW tab Stream button).
            // Always present so the UI button can read mode/fps even
            // while idle.
            tick.Blocks["advSeq"] = new {
                state = advEngine.State.ToString(),
                framesCompleted = advEngine.FramesCompleted,
                lastError = advEngine.LastError,
                startedAt = advEngine.StartedAt
            };

            tick.Blocks["plan"] = planRunner.GetStatus();

            // Advanced sequencer runs entirely on the host; put
            // its state on the 1 Hz stream so the activity-bar
            // chip works from ANY tab and survives a client that
            // disconnects and reconnects hours later (the ADV
            // tab's 2 s poll only runs while that tab is open).
            tick.Blocks["autoFocus"] = autoFocusPayload;

            tick.Blocks["meridianFlip"] = meridianPayload;

            tick.Blocks["flatWizard"] = new {
                state = flatWizard.State.ToString().ToLowerInvariant(),
                lastError = flatWizard.LastError,
                progress = flatWizard.State == FlatWizardState.Idle
                           && flatWizard.Progress.TotalFilters == 0
                    ? null
                    : (object)new {
                        startedAt = flatWizard.Progress.StartedAt,
                        totalFilters = flatWizard.Progress.TotalFilters,
                        currentFilterIndex = flatWizard.Progress.CurrentFilterIndex,
                        currentFilter = flatWizard.Progress.CurrentFilter,
                        phase = flatWizard.Progress.Phase,
                        searchAttempt = flatWizard.Progress.SearchAttempt,
                        currentExposure = flatWizard.Progress.CurrentExposure,
                        lastMedian = flatWizard.Progress.LastMedian,
                        totalFramesPerFilter = flatWizard.Progress.TotalFramesPerFilter,
                        framesCaptured = flatWizard.Progress.FramesCaptured,
                        filterResults = flatWizard.Progress.FilterResults
                    }
            };

            // Auto-slew-preview state (SKY tab inset card).
    }

}
