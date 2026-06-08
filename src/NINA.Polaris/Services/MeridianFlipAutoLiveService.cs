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
        // Only while a live stack is actively integrating.
        if (!_liveStack.IsRunning) return;

        var scope = _equip.Telescope;
        if (scope == null || !scope.IsConnected) return;

        var raHours = scope.RightAscension;
        if (double.IsNaN(raHours)) return;

        var minutesAfter = _meridian.Settings.MinutesAfterMeridian;
        var hoursUntilFlip = MeridianFlipService.HoursUntilFlip(
            raHours, DateTime.UtcNow, _profile.Active.Longitude, minutesAfter);

        // Re-arm the per-crossing guard once the target is well east of the
        // flip point again (a new target rising, or the next night).
        if (hoursUntilFlip > 0.25) _flippedThisCrossing = false;

        if (_flippedThisCrossing) return;
        if (_meridian.State != MeridianFlipState.Idle) return;

        // Flip is due: HA has reached/passed the flip point but the target
        // isn't absurdly far west (sanity bound, ~6 h past meridian).
        bool due = hoursUntilFlip <= 0 && hoursUntilFlip > -6.0;
        if (!due) return;

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