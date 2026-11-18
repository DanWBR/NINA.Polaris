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
/// The LIVE and AUTORUN end events belong to one night. Besides turning off
/// once they run, they are turned off on every rig when Polaris starts (a
/// restart of Polaris or a reboot of the host) and when a new observing night
/// begins, at local noon, so a park or a power-off set yesterday is never
/// waiting in tonight's session.
///
/// <para>The noon reset waits while an AUTORUN run or the LIVE capture is
/// going: a session that runs past noon keeps its end events until it is
/// over.</para>
/// </summary>
public sealed class EndEventsResetService : BackgroundService {
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

    private readonly ProfileService _profiles;
    private readonly SequenceEngine _engine;
    private readonly LiveCaptureService _capture;
    private readonly ILogger<EndEventsResetService> _logger;

    public EndEventsResetService(ProfileService profiles, SequenceEngine engine,
                                 LiveCaptureService capture, ILogger<EndEventsResetService> logger) {
        _profiles = profiles;
        _engine = engine;
        _capture = capture;
        _logger = logger;
    }

    /// <summary>The observing night a local time belongs to: the date of the
    /// evening it started, with the day turning at noon.</summary>
    internal static DateOnly NightOf(DateTime local) => DateOnly.FromDateTime(local.AddHours(-12));

    /// <summary>Turn the end events off on every rig (AUTORUN and LIVE), and
    /// in the engine's copy for the active rig. Returns how many rigs changed.</summary>
    internal static int ClearAll(ProfileService profiles, SequenceEngine engine) {
        int changed = 0;
        foreach (var rig in profiles.ListEquipmentProfiles()) {
            bool live = rig.LiveStackEndActions?.ToTeardown().Any == true;
            bool auto = rig.AutorunEndActions?.HasEndEvents == true;
            if (!live && !auto) continue;
            profiles.UpdateEquipmentProfile(rig.Id, r => {
                if (live) r.LiveStackEndActions = r.LiveStackEndActions.WithoutEndEvents();
                if (auto) r.AutorunEndActions = r.AutorunEndActions!.WithoutEndEvents();
            });
            changed++;
        }
        // The engine holds the active rig's set in memory and writes it back on
        // every schedule save: clear that copy too.
        engine.ClearEndEvents();
        return changed;
    }

    private bool Busy => _engine.State != SequenceState.Idle || _capture.IsRunning;

    private void Clear(string why) {
        try {
            int n = ClearAll(_profiles, _engine);
            if (n > 0) _logger.LogInformation("End events turned off on {N} rig(s): {Why}", n, why);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Could not turn the end events off ({Why})", why);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        Clear("Polaris started");
        var night = NightOf(DateTime.Now);
        while (!stoppingToken.IsCancellationRequested) {
            try { await Task.Delay(CheckInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
            var now = NightOf(DateTime.Now);
            if (now == night || Busy) continue;
            Clear("a new observing night");
            night = now;
        }
    }
}
