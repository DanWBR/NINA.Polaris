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

using NINA.INDI.Client;
using NINA.INDI.Devices;
using NINA.INDI.Protocol;

namespace NINA.Polaris.Services;

/// <summary>
/// Gives an INDI filter wheel a name for every slot it reports.
///
/// <para>When the driver publishes fewer filter names than the wheel has
/// slots, the filter list is short and the missing slots cannot be picked.
/// The driver sizes that list from its saved config, once, when its process
/// starts (see <see cref="IndiFilterWheel.WriteNamesForAllSlots"/>). So this
/// writes a name per slot into the config, restarts only that driver through
/// the INDI Web Manager and reconnects the wheel. Once per wheel and slot
/// count, and never while the wheel is turning.</para>
/// </summary>
public sealed class FilterWheelNameFillService : BackgroundService {
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private readonly EquipmentManager _equip;
    private readonly IndiClient _indi;
    private readonly IndiWebManagerService _indiWeb;
    private readonly NotificationService _notify;
    private readonly ILogger<FilterWheelNameFillService> _logger;
    private readonly HashSet<string> _tried = new(StringComparer.Ordinal);

    public FilterWheelNameFillService(EquipmentManager equip, IndiClient indi,
            IndiWebManagerService indiWeb, NotificationService notify,
            ILogger<FilterWheelNameFillService> logger) {
        _equip = equip;
        _indi = indi;
        _indiWeb = indiWeb;
        _notify = notify;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        while (!stoppingToken.IsCancellationRequested) {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogDebug(ex, "Filter wheel name fill tick failed (non-fatal)"); }
            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct) {
        var fw = _equip.FilterWheel;
        if (fw is not { IsConnected: true } || fw.IsMoving) return;
        if ((fw as EffectiveFilterWheel)?.Inner is not IndiFilterWheel wheel) return;
        var (min, max) = wheel.SlotRange;
        var verdict = FilterWheelHealth.Judge(wheel.Position, min, max, wheel.FilterNames.Length);
        if (!verdict.NamesShortOfSlots) return;

        var key = wheel.DeviceName + "#" + max;
        if (!_tried.Add(key)) return;

        int before = wheel.FilterNames.Length;
        if (wheel.WriteNamesForAllSlots() == 0) {
            _logger.LogInformation(
                "Filter wheel '{Wheel}': {Names} names for {Slots} slots, but the driver's config is not on this machine",
                wheel.DeviceName, before, max);
            return;
        }
        _logger.LogWarning(
            "Filter wheel '{Wheel}': the driver published {Names} names for {Slots} slots. "
            + "Wrote a name per slot into its saved config; restarting the driver to load it",
            wheel.DeviceName, before, max);

        if (!_indiWeb.Running) {
            _notify.Push("warn",
                $"The filter wheel has {max} slots but its driver lists {before}. Polaris saved a name for every "
                + "slot; restart the filter wheel driver to see them all.", 15000);
            return;
        }
        string? driverName = null, driverExec = null;
        if (_indi.GetProperty(wheel.DeviceName, "DRIVER_INFO") is IndiTextProperty di) {
            di.Values.TryGetValue("DRIVER_NAME", out driverName);
            di.Values.TryGetValue("DRIVER_EXEC", out driverExec);
        }
        var label = IndiDriverWatchdogService.PickDriverLabel(wheel.DeviceName, driverName, driverExec,
            await _indiWeb.GetRunningDriversAsync(ct));
        if (string.IsNullOrWhiteSpace(label) || !await _indiWeb.RestartDriverAsync(label, ct)) {
            _notify.Push("warn",
                $"The filter wheel has {max} slots but its driver lists {before}. Polaris saved a name for every "
                + "slot; restart the filter wheel driver to see them all.", 15000);
            return;
        }

        // Back on the server, then connect it again and wait for the names.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline && _indi.GetProperty(wheel.DeviceName, "CONNECTION") == null)
            await Task.Delay(250, ct);
        await Task.Delay(1000, ct);
        try { await _equip.FilterWheel!.ConnectAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            _logger.LogWarning(ex, "Filter wheel '{Wheel}': reconnect after the driver restart failed", wheel.DeviceName);
        }
        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && wheel.FilterNames.Length < max)
            await Task.Delay(250, ct);

        int after = wheel.FilterNames.Length;
        if (after >= max) {
            _logger.LogInformation("Filter wheel '{Wheel}': the driver now lists all {Slots} slots", wheel.DeviceName, max);
            _notify.Push("info",
                $"The filter wheel now shows all {max} slots. New slots are named \"Filter N\"; rename them in the wheel's filter names.",
                12000);
        } else {
            _logger.LogWarning("Filter wheel '{Wheel}': still {Names} names for {Slots} slots after the driver restart",
                wheel.DeviceName, after, max);
        }
    }
}
