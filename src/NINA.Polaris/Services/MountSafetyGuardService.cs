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

namespace NINA.Polaris.Services;

/// <summary>
/// Fail-safe watchdog that protects the mount + cabling during unattended
/// sessions. Born from a real ASIAIR incident: a mount tracked hours past the
/// meridian WITHOUT a flip (plate-solve angle stayed constant all night) while
/// a clouded-out run looped on guide-star recovery forever — the RA axis wound
/// the cabling into a corkscrew until a USB cable physically ripped.
///
/// Two guards, both opt-out, both purely *halting* (never command a risky move):
///
///  1. <b>Past-meridian cable-wrap limit</b> — if the target tracks more than
///     <see cref="MeridianFlipSettings.MaxMinutesPastMeridian"/> past the
///     meridian and no flip happened (GEM pier side unchanged, or a flip-less
///     strain-wave mount), stop tracking + abort.
///  2. <b>Guiding circuit breaker</b> — if the guider loses the star
///     <see cref="MeridianFlipSettings.MaxConsecutiveGuideFailures"/> times in
///     a row with no recovery, stop tracking + abort instead of looping.
///
/// On trip: abort the sequencer + LIVE loop + live stack, stop the guider,
/// turn tracking OFF (this is what actually stops the winding), optionally
/// park, and surface the trip on the WS status so the UI can banner it.
/// </summary>
public class MountSafetyGuardService : BackgroundService {
    private readonly EquipmentManager _equip;
    private readonly ProfileService _profile;
    private readonly ActiveGuiderProvider _guiders;
    private readonly SequenceEngine _sequence;
    private readonly LiveCaptureService _liveCapture;
    private readonly LiveStackingService _liveStack;
    private readonly MeridianFlipService _meridian;
    private readonly ILogger<MountSafetyGuardService> _logger;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    // While the mount is slewing we poll fast so the anti-crash altitude floor
    // can abort a wrong-way slew before the OTA reaches the pier/tripod.
    private static readonly TimeSpan FastPollInterval = TimeSpan.FromSeconds(1);

    // ---- public trip state (read by the meridian-flip /status endpoint + WS) ----
    public bool Tripped { get; private set; }
    public string? TripReason { get; private set; }
    public DateTime? TrippedAt { get; private set; }

    /// <summary>True when a post-trip re-home is required before slewing is
    /// allowed again (guard tripped + the setting is on). Read by the slew path.</summary>
    public bool RequireHomeAfterTrip =>
        Tripped && _meridian.Settings.RequireHomeAfterSafetyStop;

    /// <summary>Effective anti-crash altitude floor (deg, 0 = off). The value is
    /// per-rig (<see cref="EquipmentProfile.SlewFloorDeg"/>, null ⇒
    /// <see cref="MountSlewSafety.AltitudeFloorDeg"/>) but the global
    /// <c>SafetyStopEnabled</c> stays the master on/off switch. Read by the slew
    /// pre-check and the live abort poll.</summary>
    public double AltitudeFloorDeg {
        get {
            if (!_meridian.Settings.SafetyStopEnabled) return 0;
            return _profile.ActiveEquipmentProfile?.SlewFloorDeg ?? MountSlewSafety.AltitudeFloorDeg;
        }
    }

    // ---- meridian-crossing tracking ----
    private double? _prevHa;
    private PierSide _pierAtCrossing = PierSide.pierUnknown;
    private bool _sawCrossing;
    private bool _flippedSinceCrossing;

    // ---- guiding circuit breaker ----
    private int _consecutiveGuideFailures;
    private IGuider? _subscribedGuider;
    private bool _wasSessionActive;

    // ---- tracking-drop diagnostic ----
    // Logs a full mount snapshot whenever tracking stops UNEXPECTEDLY (not a
    // slew, flip, park or our own safety trip). Built to chase an intermittent
    // ZWO AM3 that stops tracking near the zenith on the west side: comparing
    // several events shows which invariant is constant — a fixed hour angle
    // (meridian/tracking limit), a fixed altitude (altitude limit), or a fixed
    // RA-axis angle (travel limit / hardware).
    private bool? _prevTracking;
    private bool _prevSlewing;
    public string? LastTrackingDrop { get; private set; }

    public MountSafetyGuardService(
            EquipmentManager equip, ProfileService profile, ActiveGuiderProvider guiders,
            SequenceEngine sequence, LiveCaptureService liveCapture, LiveStackingService liveStack,
            MeridianFlipService meridian, ILogger<MountSafetyGuardService> logger) {
        _equip = equip;
        _profile = profile;
        _guiders = guiders;
        _sequence = sequence;
        _liveCapture = liveCapture;
        _liveStack = liveStack;
        _meridian = meridian;
        _logger = logger;
    }

    /// <summary>Clear a trip (UI dismiss). Does not restart anything.</summary>
    public void Reset() {
        Tripped = false;
        TripReason = null;
        TrippedAt = null;
        _consecutiveGuideFailures = 0;
    }

    // ----------------------- pure decision helpers (unit-tested) -----------------

    /// <summary>
    /// Should the cable-wrap guard trip? <paramref name="haHours"/> is the
    /// target hour angle (negative = east of meridian, positive = west).
    /// A GEM that has flipped since crossing the meridian is always safe; a
    /// flip-less mount (no pier-side support) trips purely on hours-past.
    /// </summary>
    public static bool ShouldTripMeridian(double haHours, bool supportsPierSide,
            bool flippedSinceCrossing, double maxMinutesPastMeridian) {
        if (maxMinutesPastMeridian <= 0) return false;       // guard disabled
        if (haHours <= 0) return false;                       // east of meridian, no wind
        if (haHours * 60.0 <= maxMinutesPastMeridian) return false;
        // Past the limit: a flipped GEM is safe; otherwise the axis is winding.
        if (supportsPierSide && flippedSinceCrossing) return false;
        return true;
    }

    /// <summary>Should the guiding circuit breaker trip?</summary>
    public static bool ShouldTripBreaker(int consecutiveFailures, int threshold)
        => threshold > 0 && consecutiveFailures >= threshold;

    // ---------------------------------- loop ------------------------------------

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        try { await Task.Delay(TimeSpan.FromSeconds(12), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested) {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogDebug(ex, "Safety-guard tick failed (non-fatal)"); }
            // Poll fast while the mount is slewing so the anti-crash altitude
            // floor can abort a wrong-way slew in ~1 s instead of up to 15 s.
            var scope = _equip.Telescope;
            var delay = (scope is { IsConnected: true, IsSlewing: true }) ? FastPollInterval : PollInterval;
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private bool SessionActive =>
        _sequence.State == SequenceState.Running
        || _liveCapture.IsRunning
        || _liveStack.IsRunning;

    private async Task TickAsync(CancellationToken ct) {
        EnsureGuiderSubscription();

        // Diagnostic: log a snapshot whenever tracking stops unexpectedly.
        CheckTrackingDropDiagnostic();

        var s = _meridian.Settings;

        // ---- Guard 3: anti-crash altitude floor ----
        // Runs INDEPENDENTLY of a session (a manual SKY "Go To" while idle can
        // still drive the OTA the wrong way toward the pier/tripod). If the mount
        // is slewing and its current pointing is below the floor, abort the slew
        // and stop. This is the backstop that catches a wrong-way slew already in
        // motion, regardless of why it started.
        // Per-rig floor via AltitudeFloorDeg (already gated on SafetyStopEnabled;
        // 0 = off). Keeps the abort backstop in step with the per-rig setting the
        // slew pre-check uses.
        double floorDeg = AltitudeFloorDeg;
        if (!Tripped && floorDeg > 0) {
            var scope = _equip.Telescope;
            if (scope is { IsConnected: true, IsSlewing: true }) {
                double ra = scope.RightAscension, dec = scope.Declination;
                if (!double.IsNaN(ra) && !double.IsNaN(dec)) {
                    var (altDeg, _) = AltitudeService.RaDecToAltAz(
                        ra, dec, DateTime.UtcNow, _profile.Active.Latitude, _profile.Active.Longitude);
                    if (MountSlewSafety.ShouldAbortForAltitude(altDeg, floorDeg, true)) {
                        try { await scope.AbortSlewAsync(ct); }
                        catch (Exception ex) { _logger.LogWarning(ex, "Safety: abort-slew (altitude) failed"); }
                        await TripAsync(
                            $"Slew aborted: the OTA dropped to {altDeg:F0}° altitude, below the " +
                            $"{floorDeg:F0}° floor — a wrong-way slew heading for the mount/tripod.",
                            s, ct);
                        return;
                    }
                }
            }
        }

        bool active = SessionActive;

        // Re-arm on a fresh session start (idle → active).
        if (active && !_wasSessionActive) {
            _consecutiveGuideFailures = 0;
            // Don't auto-clear a standing trip here; the UI/Reset does that.
        }
        _wasSessionActive = active;

        if (!s.SafetyStopEnabled || !active || Tripped) {
            // Still track the meridian-crossing state machine so we have an
            // accurate flip history the moment a session starts.
            UpdateMeridianState();
            return;
        }

        // ---- Guard 1: past-meridian cable wrap ----
        var (haHours, supportsPier, flipped) = UpdateMeridianState();
        if (haHours.HasValue && _sawCrossing
                && ShouldTripMeridian(haHours.Value, supportsPier, flipped, s.MaxMinutesPastMeridian)
                && _meridian.State == MeridianFlipState.Idle) {
            var mins = haHours.Value * 60.0;
            await TripAsync(
                $"Mount tracked {mins:F0} min past the meridian without a flip " +
                $"(limit {s.MaxMinutesPastMeridian:F0} min) — stopping to prevent a cable wrap.",
                s, ct);
            return;
        }

        // ---- Guard 2: guiding circuit breaker ----
        if (ShouldTripBreaker(_consecutiveGuideFailures, s.MaxConsecutiveGuideFailures)) {
            await TripAsync(
                $"Guider lost the star {_consecutiveGuideFailures} times in a row with no " +
                $"recovery (limit {s.MaxConsecutiveGuideFailures}) — stopping the session.",
                s, ct);
        }
    }

    /// <summary>
    /// Log a full mount snapshot the instant tracking stops unexpectedly, to
    /// diagnose an intermittent tracking dropout (e.g. the AM3-near-zenith-west
    /// report). Skips the legitimate reasons tracking goes off: an in-progress
    /// slew (or one that just finished), a meridian flip, or our own safety trip.
    /// Records RA/Dec, hour angle, altitude, azimuth and pier side so several
    /// events can be compared — whichever value is constant across dropouts
    /// points at the cause (fixed HA = meridian limit, fixed altitude = altitude
    /// limit, fixed RA-axis angle = travel limit / hardware).
    /// </summary>
    private void CheckTrackingDropDiagnostic() {
        var scope = _equip.Telescope;
        if (scope == null || !scope.IsConnected || !scope.Capabilities.SupportsTrackingToggle) {
            _prevTracking = null; _prevSlewing = false;
            return;
        }

        bool tracking = scope.IsTracking;
        bool slewing = scope.IsSlewing;
        bool wasTracking = _prevTracking ?? tracking;   // first sample: no transition
        bool prevSlewing = _prevSlewing;
        _prevTracking = tracking;
        _prevSlewing = slewing;

        // Only care about a genuine on -> off transition.
        if (!(wasTracking && !tracking)) return;
        // Expected reasons tracking is off — not a fault:
        //  • slewing now, or slewing on the previous tick (tracking re-engages a
        //    beat after a GoTo completes);
        //  • a meridian flip in progress;
        //  • our own safety guard stopped it.
        if (slewing || prevSlewing) return;
        if (_meridian.State != MeridianFlipState.Idle) return;
        if (Tripped) return;

        double ra = scope.RightAscension, dec = scope.Declination;
        if (double.IsNaN(ra) || double.IsNaN(dec)) return;

        double lst = MeridianFlipService.ComputeLstHours(DateTime.UtcNow, _profile.Active.Longitude);
        double haMin = MountSlewSafety.TargetHaMinutes(ra, lst);
        var (alt, az) = AltitudeService.RaDecToAltAz(
            ra, dec, DateTime.UtcNow, _profile.Active.Latitude, _profile.Active.Longitude);
        var pier = scope.Capabilities.SupportsPierSide ? scope.SideOfPier.ToString().Replace("pier", "") : "n/a";

        string snap =
            $"RA={ra:F4}h Dec={dec:F3}° HA={Math.Abs(haMin):F0} min {(haMin >= 0 ? "west" : "east")} " +
            $"Alt={alt:F1}° Az={az:F1}° pier={pier}";
        LastTrackingDrop = $"{DateTime.UtcNow:HH:mm:ss}Z  {snap}";
        _logger.LogWarning(
            "MOUNT TRACKING STOPPED unexpectedly (diagnostic — not a slew/flip/safety-stop): {Snap}. " +
            "Compare several of these: constant HA ⇒ meridian/tracking limit; constant Alt ⇒ altitude limit; " +
            "constant RA/pier ⇒ RA-axis travel limit or hardware.", snap);
    }

    /// <summary>
    /// Advance the meridian-crossing state machine and return the current
    /// (haHours, supportsPierSide, flippedSinceCrossing). haHours is null when
    /// no connected mount / RA is available.
    /// </summary>
    private (double? haHours, bool supportsPier, bool flipped) UpdateMeridianState() {
        var scope = _equip.Telescope;
        if (scope == null || !scope.IsConnected) {
            _prevHa = null; _sawCrossing = false; _flippedSinceCrossing = false;
            return (null, false, false);
        }

        var ra = scope.RightAscension;
        if (double.IsNaN(ra)) return (null, false, false);

        double lst = MeridianFlipService.ComputeLstHours(DateTime.UtcNow, _profile.Active.Longitude);
        double ha = lst - ra;
        while (ha > 12) ha -= 24;
        while (ha < -12) ha += 24;

        bool supportsPier = scope.Capabilities.SupportsPierSide;
        var pier = scope.SideOfPier;

        // Clearly east again (new approach / next night): reset the machine.
        if (ha < -0.1) {
            _sawCrossing = false;
            _flippedSinceCrossing = false;
            _pierAtCrossing = PierSide.pierUnknown;
        }

        // Detect the meridian crossing (HA goes − → ≥0): record the pier side then.
        if (_prevHa.HasValue && _prevHa.Value < 0 && ha >= 0) {
            _sawCrossing = true;
            _pierAtCrossing = pier;
            _flippedSinceCrossing = false;
        }

        // A flip is "seen" once the pier side differs from the crossing side.
        if (_sawCrossing && supportsPier
                && pier != PierSide.pierUnknown && _pierAtCrossing != PierSide.pierUnknown
                && pier != _pierAtCrossing) {
            _flippedSinceCrossing = true;
        }

        _prevHa = ha;
        return (ha, supportsPier, _flippedSinceCrossing);
    }

    private async Task TripAsync(string reason, MeridianFlipSettings s, CancellationToken ct) {
        Tripped = true;
        TripReason = reason;
        TrippedAt = DateTime.UtcNow;
        _logger.LogError("MOUNT SAFETY STOP: {Reason}", reason);

        // 1. Abort the running session(s) so nothing re-commands the mount.
        try { _sequence.Stop(); } catch (Exception ex) { _logger.LogWarning(ex, "Safety: sequence stop failed"); }
        try { _liveCapture.Stop(); } catch (Exception ex) { _logger.LogWarning(ex, "Safety: live capture stop failed"); }
        try { _liveStack.Stop(); } catch (Exception ex) { _logger.LogWarning(ex, "Safety: live stack stop failed"); }

        // 2. Stop the guider so it stops pulse-guiding / re-selecting.
        try {
            var g = _guiders.Active;
            if (g.IsConnected) await g.StopAsync(ct);
        } catch (Exception ex) { _logger.LogWarning(ex, "Safety: guider stop failed"); }

        // 3. THE important one — turn tracking off so the RA axis stops winding.
        var scope = _equip.Telescope;
        if (scope != null && scope.IsConnected) {
            try { await scope.SetTrackingAsync(false, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Safety: stop-tracking failed"); }

            // 4. Optional park (unwinds fully) — opt-in to avoid a surprise re-home.
            if (s.ParkOnSafetyStop) {
                try { await scope.ParkAsync(ct); }
                catch (Exception ex) { _logger.LogWarning(ex, "Safety: park failed"); }
            }
        }
    }

    // -------------------------- guider failure tracking --------------------------

    private void EnsureGuiderSubscription() {
        var g = _guiders.Active;
        if (ReferenceEquals(g, _subscribedGuider)) return;
        if (_subscribedGuider != null) {
            _subscribedGuider.AppStateChanged -= OnGuiderAppState;
            _subscribedGuider.Alert -= OnGuiderAlert;
        }
        _subscribedGuider = g;
        g.AppStateChanged += OnGuiderAppState;
        g.Alert += OnGuiderAlert;
    }

    private void OnGuiderAppState(string state) {
        if (string.IsNullOrEmpty(state)) return;
        if (state.Equals("LostLock", StringComparison.OrdinalIgnoreCase)) {
            _consecutiveGuideFailures++;
        } else if (state.Equals("Guiding", StringComparison.OrdinalIgnoreCase)) {
            // Clean recovery resets the breaker.
            _consecutiveGuideFailures = 0;
        }
    }

    private void OnGuiderAlert(string _) {
        // Alerts include "star selection failed" / "no star found" style
        // messages; count them toward the breaker (reset on a Guiding state).
        _consecutiveGuideFailures++;
    }
}
