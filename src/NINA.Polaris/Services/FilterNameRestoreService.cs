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

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NINA.Polaris.Services;

/// <summary>
/// Puts the operator's filter names back into the driver after it comes up
/// with defaults.
///
/// Filter labels are the operator's data: they live on the rig
/// (<c>EquipmentProfile.FilterNames</c>) and the driver is only a cache of
/// them. INDI drivers routinely come back from a restart advertising
/// "Filter 1..N" — the wheel has no memory of its own until CONFIG_SAVE lands
/// — so something has to push the saved set back.
///
/// That restore used to live in the BROWSER, on a status tick. Which meant it
/// only happened when a browser happened to be open, on that page, at that
/// moment, having already loaded the rig list. Reported from the field twice:
/// once after a tab reload, once after a package update. This owns it on the
/// server, where the names actually live, so it works headless and for every
/// connected client at once.
///
/// Everything it reads is published ASYNCHRONOUSLY after the wheel connects,
/// so "not yet" is never treated as "no": the loop simply keeps looking. It
/// pushes once per connection episode and then goes quiet until the wheel
/// disappears and comes back.
/// </summary>
public class FilterNameRestoreService : BackgroundService {
    private readonly EquipmentManager _equipment;
    private readonly ProfileService _profiles;
    private readonly ILogger<FilterNameRestoreService> _logger;

    // Slow poll: this is a once-per-connection repair, not a control loop.
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    // Latch per connection episode, so a wheel whose names the operator
    // deliberately edits in the driver isn't fought with every 2 seconds.
    private bool _restoredThisEpisode;
    private string? _episodeDevice;

    public FilterNameRestoreService(EquipmentManager equipment,
                                    ProfileService profiles,
                                    ILogger<FilterNameRestoreService> logger) {
        _equipment = equipment;
        _profiles = profiles;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        while (!stoppingToken.IsCancellationRequested) {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) {
                _logger.LogDebug(ex, "Filter-name restore tick failed (non-fatal)");
            }
            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct) {
        var wheel = _equipment.FilterWheel;
        if (wheel == null) { ResetEpisode(null); return; }

        // A different wheel (or the same one re-selected) starts a new episode.
        if (_episodeDevice != wheel.DeviceName) ResetEpisode(wheel.DeviceName);
        if (_restoredThisEpisode) return;

        if (!wheel.IsConnected) { _restoredThisEpisode = false; return; }

        var saved = _profiles.ActiveEquipmentProfile?.FilterNames;
        var current = wheel.FilterNames;
        switch (Decide(wheel.Capabilities.SupportsEditNames, saved, current)) {
            case Decision.Wait:
                return;
            case Decision.Nothing:
                _restoredThisEpisode = true;
                return;
            case Decision.Push:
                _logger.LogInformation(
                    "Filter wheel came up with names the rig does not have saved, restoring: [{Driver}] -> [{Saved}]",
                    string.Join(", ", current!), string.Join(", ", saved!));
                await wheel.SetFilterNamesAsync(saved!, ct);
                _restoredThisEpisode = true;
                return;
        }
    }

    internal enum Decision {
        /// <summary>Not knowable yet. Ask again; never latch on this.</summary>
        Wait,
        /// <summary>Definitively nothing to do. Latch until reconnect.</summary>
        Nothing,
        /// <summary>Push the saved names into the driver.</summary>
        Push
    }

    /// <summary>
    /// The whole point of this service in one function: tell "no" apart from
    /// "not yet". Everything the caller feeds in is published asynchronously
    /// after the wheel connects, so an early snapshot answers "no" to
    /// questions whose answer becomes "yes" a second later. Two shipped bugs
    /// came from latching on those.
    /// </summary>
    internal static Decision Decide(bool supportsEditNames, string[]? saved, string[]? current) {
        // Derived live from the driver's FILTER_NAME property on INDI, which
        // does not exist in the first moments after a reconnect.
        if (!supportsEditNames) return Decision.Wait;
        // Nothing stored for this rig: no later event changes that.
        if (saved == null || saved.Length == 0) return Decision.Nothing;
        // Slots arrive as the driver publishes them; a short list is "not yet".
        if (current == null || current.Length != saved.Length) return Decision.Wait;
        for (var i = 0; i < current.Length; i++) {
            if (!string.Equals(current[i], saved[i], StringComparison.Ordinal))
                return Decision.Push;
        }
        return Decision.Nothing;
    }

    private void ResetEpisode(string? device) {
        _episodeDevice = device;
        _restoredThisEpisode = false;
    }
}
