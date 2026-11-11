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

using NINA.Image.Interfaces;

namespace NINA.Polaris.Services;

/// <summary>
/// Asks once a night, when the mount connects, whether to send Polaris's date,
/// time and location to it.
///
/// <para>The question lives on the server, so every open client shows the same
/// prompt and the first answer closes it everywhere. The answer is kept with
/// the night it was given for (<see cref="NightKey"/>), so a mount that drops
/// and reconnects later that night (a USB reset, a power blip) gets the same
/// choice applied again without asking.</para>
/// </summary>
public sealed class MountSiteSyncService : BackgroundService {
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    /// <summary>Time for the driver to announce its site and clock before
    /// they are read for the prompt.</summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(3);

    public static readonly string[] Choices = { "both", "time", "location", "none" };

    private readonly EquipmentManager _equip;
    private readonly ProfileService _profiles;
    private readonly ILogger<MountSiteSyncService> _logger;
    private readonly object _gate = new();
    private bool _wasConnected;
    private int _nextId;

    public sealed record PendingPrompt(int Id, string Device, string Night,
        DateTime PolarisUtc, double OffsetHours,
        double Latitude, double Longitude, double Elevation, bool LocationConfigured,
        bool CanSetTime, bool CanSetLocation,
        double? MountLatitude, double? MountLongitude);

    /// <summary>The open question, or null.</summary>
    public PendingPrompt? Pending { get; private set; }

    public MountSiteSyncService(EquipmentManager equip, ProfileService profiles,
            ILogger<MountSiteSyncService> logger) {
        _equip = equip;
        _profiles = profiles;
        _logger = logger;
    }

    /// <summary>The observing night a moment belongs to: the local date of
    /// the evening, so 02:00 counts with the night that started the day before.
    /// The night turns over at local noon.</summary>
    internal static string NightKey(DateTime local) =>
        local.AddHours(-12).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    internal static bool SendsTime(string choice) => choice is "both" or "time";
    internal static bool SendsLocation(string choice) => choice is "both" or "location";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        while (!stoppingToken.IsCancellationRequested) {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogDebug(ex, "Mount site sync tick failed (non-fatal)"); }
            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct) {
        var scope = _equip.Telescope;
        bool connected = scope is { IsConnected: true };
        if (!connected) {
            _wasConnected = false;
            lock (_gate) Pending = null;
            return;
        }
        if (_wasConnected) return;
        _wasConnected = true;

        await Task.Delay(SettleDelay, ct);
        scope = _equip.Telescope;
        if (scope is not { IsConnected: true }) { _wasConnected = false; return; }

        var p = _profiles.Active;
        var night = NightKey(DateTime.Now);
        if (p.MountSiteSyncNight == night && p.MountSiteSyncChoice is { } choice) {
            _logger.LogInformation(
                "Mount connected again tonight: applying tonight's time and location choice \"{Choice}\"", choice);
            await ApplyAsync(scope, choice, ct);
            return;
        }

        var caps = scope.Capabilities;
        if (!caps.SupportsSetSiteTime && !caps.SupportsSetSiteLocation) return;

        var utc = DateTime.UtcNow;
        double? mountLat = null, mountLon = null;
        if (scope is NINA.INDI.Devices.IndiTelescope indi) {
            var site = indi.DriverSite;
            mountLat = site.Lat;
            mountLon = site.Lon;
        }
        lock (_gate) {
            Pending = new PendingPrompt(++_nextId, scope.DeviceName ?? "mount", night,
                utc, TimeZoneInfo.Local.GetUtcOffset(utc).TotalHours,
                p.Latitude, p.Longitude, p.Altitude, LocationConfigured(p),
                caps.SupportsSetSiteTime, caps.SupportsSetSiteLocation,
                mountLat, mountLon);
        }
        _logger.LogInformation("Mount connected: asking whether to sync its date, time and location (night {Night})", night);
    }

    private static bool LocationConfigured(UserProfile p) =>
        Math.Abs(p.Latitude) > 1e-6 || Math.Abs(p.Longitude) > 1e-6;

    public sealed record Outcome(bool Ok, string? TimeStatus, string? LocationStatus, string? Error);

    /// <summary>The operator's answer, from any client. Applies it, keeps it
    /// for the rest of the night and closes the prompt everywhere.</summary>
    public async Task<Outcome> AnswerAsync(int id, string choice, CancellationToken ct) {
        choice = (choice ?? "").Trim().ToLowerInvariant();
        if (!Choices.Contains(choice)) return new Outcome(false, null, null, "Unknown choice");
        PendingPrompt? pending;
        lock (_gate) {
            pending = Pending;
            if (pending == null || pending.Id != id)
                return new Outcome(false, null, null, "Already answered");
            Pending = null;
        }
        var p = _profiles.Active;
        p.MountSiteSyncNight = pending.Night;
        p.MountSiteSyncChoice = choice;
        _profiles.Save();
        _logger.LogInformation("Mount time and location: operator chose \"{Choice}\" for night {Night}",
            choice, pending.Night);

        var scope = _equip.Telescope;
        if (scope is not { IsConnected: true })
            return new Outcome(false, null, null, "The mount is not connected");
        var (timeStatus, locationStatus) = await ApplyAsync(scope, choice, ct);
        return new Outcome(true, timeStatus, locationStatus, null);
    }

    private async Task<(string? Time, string? Location)> ApplyAsync(ITelescope scope, string choice,
            CancellationToken ct) {
        string? timeStatus = null, locationStatus = null;
        if (SendsTime(choice)) {
            try {
                var utc = DateTime.UtcNow;
                var offset = TimeZoneInfo.Local.GetUtcOffset(utc).TotalHours;
                await scope.SetSiteTimeAsync(utc, offset, ct);
                timeStatus = "sent";
                _logger.LogInformation("Mount time set: utc={Utc:o}, offset={Offset:F2}h", utc, offset);
            } catch (NotSupportedException) {
                timeStatus = "not supported by the driver";
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                timeStatus = "failed: " + ex.Message;
                _logger.LogWarning(ex, "Mount time sync failed");
            }
        }
        if (SendsLocation(choice)) {
            var p = _profiles.Active;
            if (!LocationConfigured(p)) {
                locationStatus = "location not set in Polaris";
            } else {
                try {
                    await scope.SetSiteLocationAsync(p.Latitude, p.Longitude, p.Altitude, ct);
                    locationStatus = "sent";
                    _logger.LogInformation("Mount location set: lat={Lat:F4}, lon={Lon:F4}, elev={Elev:F0}m",
                        p.Latitude, p.Longitude, p.Altitude);
                } catch (NotSupportedException) {
                    locationStatus = "not supported by the driver";
                } catch (Exception ex) when (ex is not OperationCanceledException) {
                    locationStatus = "failed: " + ex.Message;
                    _logger.LogWarning(ex, "Mount location sync failed");
                }
            }
        }
        return (timeStatus, locationStatus);
    }
}
