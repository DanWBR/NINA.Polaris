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
/// Auto meridian flip during LIVE stacking. Polls the mount's hour angle
/// and, when LIVE stacking is running and the target has crossed the
/// meridian by <c>MinutesAfterMeridian</c>, performs the flip via
/// <see cref="MeridianFlipService"/> -- the live stacker then re-orients
/// the post-flip frames automatically (Part B alignment probe) and keeps
/// integrating.
///
/// <para>Gated on <see cref="MeridianFlipSettings.AutoFlipDuringLiveStack"/>
/// (independent of the sequencer's <c>Enabled</c> flag). A per-crossing
/// guard prevents re-flipping every poll once the target is west of the
/// flip point: the guard re-arms when the hour angle goes negative again
/// (a fresh target rising in the east, or a new night).</para>
/// </summary>
public class MeridianFlipAutoLiveService : BackgroundService {
    private readonly LiveStackingService _liveStack;
    private readonly MeridianFlipService _meridian;
    private readonly EquipmentManager _equip;
    private readonly ProfileService _profile;
    private readonly ILogger<MeridianFlipAutoLiveService> _logger;

    // Poll cadence. The flip window is minutes wide, so 20 s is plenty
    // responsive without burning CPU on a Pi.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);

    // True once we've auto-flipped for the current meridian crossing.
    // Re-armed when HA goes clearly negative again (target rising).
    private bool _flippedThisCrossing;

    // Crossing-observation gate. We only auto-flip a target we actually watched
    // cross the meridian (HA − → ≥0) WHILE this live stack was running. Without
    // this, acquiring a target that is ALREADY west of the flip point (e.g. a
    // fresh SKY GoTo to a NW target just after homing) made the service fire an
    // immediate, unwanted flip — the mount would un-flip and swing the OTA back
    // toward the tripod. A GEM slewed straight to a western target is already on
    // the correct pier side and needs no flip. Mirrors MountSafetyGuardService's
    // `_sawCrossing` gate. Reset when the live stack isn't running or HA is
    // clearly east again (new target rising / next night).
    private double? _prevHa;
    private bool _sawCrossing;

    public MeridianFlipAutoLiveService(
            LiveStackingService liveStack,
            MeridianFlipService meridian,
            EquipmentManager equip,
            ProfileService profile,
            ILogger<MeridianFlipAutoLiveService> logger) {
        _liveStack = liveStack;
        _meridian = meridian;
        _equip = equip;
        _profile = profile;
        _logger = logger;
    }

    // ----------------------- pure decision helpers (unit-tested) -----------------

    /// <summary>True when the target just crossed the meridian west-bound between
    /// two consecutive HA samples (HA − → ≥0). <paramref name="prevHa"/> is null on
    /// the first sample (no transition yet).</summary>
    public static bool CrossedMeridianWest(double? prevHa, double ha)
        => prevHa.HasValue && prevHa.Value < 0 && ha >= 0;

    /// <summary>
    /// Should the live-stacking auto-flip fire now? A flip is due only when we've
    /// actually WATCHED the target cross the meridian on this stack
    /// (<paramref name="sawCrossing"/>) — never for a target acquired already west
    /// (a fresh GoTo to a western target is on the correct pier side and needs no
    /// flip; flipping it would un-flip the mount toward the tripod). Given a
    /// crossing, it's due once HA is at/past the flip point but not absurdly far
    /// west (~6 h sanity bound).
    /// </summary>
    public static bool AutoFlipDue(bool sawCrossing, double hoursUntilFlip)
        => sawCrossing && hoursUntilFlip <= 0 && hoursUntilFlip > -6.0;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        // Small startup delay so equipment + profile are settled.
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested) {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) {
                _logger.LogDebug(ex, "Auto meridian-flip tick failed (non-fatal)");
            }
            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct) {
        if (!_meridian.Settings.AutoFlipDuringLiveStack) return;
        // Only while a live stack is actively integrating. A stack that isn't
        // running resets the crossing-observation gate so a fresh stack must
        // watch its OWN meridian crossing before it may auto-flip.
        if (!_liveStack.IsRunning) {
            _prevHa = null;
            _sawCrossing = false;
            _flippedThisCrossing = false;
            return;
        }

        var scope = _equip.Telescope;
        if (scope == null || !scope.IsConnected) return;

        var raHours = scope.RightAscension;
        if (double.IsNaN(raHours)) return;

        // Signed hour angle (−12..+12): <0 east of meridian (rising), >0 west.
        double lst = MeridianFlipService.ComputeLstHours(DateTime.UtcNow, _profile.Active.Longitude);
        double ha = lst - raHours;
        while (ha > 12) ha -= 24;
        while (ha < -12) ha += 24;

        // Target clearly east again → new approach / next night: reset the gate.
        if (ha < -0.1) {
            _sawCrossing = false;
            _flippedThisCrossing = false;
        }
        // Meridian crossing observed (HA − → ≥0) while stacking: NOW a flip may
        // become due. A target acquired already west never trips this, so no
        // spurious flip on a fresh GoTo to a western target.
        if (CrossedMeridianWest(_prevHa, ha)) {
            _sawCrossing = true;
            _flippedThisCrossing = false;
        }
        _prevHa = ha;

        var minutesAfter = _meridian.Settings.MinutesAfterMeridian;
        var hoursUntilFlip = MeridianFlipService.HoursUntilFlip(
            raHours, DateTime.UtcNow, _profile.Active.Longitude, minutesAfter);

        // Re-arm the per-crossing guard once the target is well east of the
        // flip point again (a new target rising, or the next night).
        if (hoursUntilFlip > 0.25) _flippedThisCrossing = false;

        if (_flippedThisCrossing) return;
        if (_meridian.State != MeridianFlipState.Idle) return;

        // Only fire when we actually watched this target cross the meridian on
        // this stack AND it's past the flip point — never auto-flip a target
        // acquired already west (that would un-flip toward the tripod, the field
        // near-crash report).
        if (!AutoFlipDue(_sawCrossing, hoursUntilFlip)) return;

        var dec = scope.Declination;
        _logger.LogInformation(
            "Auto meridian flip (live stacking): target past flip point " +
            "(RA={Ra:F4}h, Dec={Dec:F3}deg). Triggering flip.", raHours, dec);
        _flippedThisCrossing = true;

        var ok = await _meridian.ExecuteFlipAsync(raHours, dec, ct);
        if (ok) {
            _logger.LogInformation("Auto meridian flip complete; live stacking continues.");
        } else {
            _logger.LogWarning("Auto meridian flip did not complete: {Err}",
                _meridian.LastFlipError ?? "unknown");
        }
    }
}