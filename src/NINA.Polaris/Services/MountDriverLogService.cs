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

namespace NINA.Polaris.Services;

/// <summary>
/// Keeps the INDI mount driver's own debug log on disk.
///
/// <para>An AM5 dropped its plate-solve sync in the middle of a session and
/// nothing in Polaris's log could say whether the mount firmware or the
/// driver did it: Polaris sees positions, not the serial replies behind them.
/// The driver's debug log has every command and reply, so it is switched on
/// for the connected INDI mount, written to a file only (sending it to the
/// client as well would flood Polaris's log with a line per poll), and kept
/// for <see cref="KeepDays"/> days under ~/.indi/logs.</para>
/// </summary>
public sealed class MountDriverLogService : IHostedService {
    internal const int KeepDays = 7;

    private readonly IndiClient _indi;
    private readonly EquipmentManager _equip;
    private readonly ILogger<MountDriverLogService> _logger;
    private readonly bool _enabled;

    public MountDriverLogService(IndiClient indi, EquipmentManager equip, IConfiguration config,
            ILogger<MountDriverLogService> logger) {
        _indi = indi;
        _equip = equip;
        _logger = logger;
        _enabled = config.GetValue("MountDriverLog:Enabled", true);
    }

    public Task StartAsync(CancellationToken cancellationToken) {
        if (!_enabled) return Task.CompletedTask;
        _indi.DeviceConfigLoaded += OnDeviceConfigLoaded;
        _ = Task.Run(SweepOldLogs, cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) {
        _indi.DeviceConfigLoaded -= OnDeviceConfigLoaded;
        return Task.CompletedTask;
    }

    private void OnDeviceConfigLoaded(string device) {
        if (_equip.Telescope is not IndiTelescope scope
                || !string.Equals(scope.DeviceName, device, StringComparison.Ordinal)) return;
        _ = Task.Run(async () => {
            try { await EnableFileLogAsync(device); }
            catch (Exception ex) { _logger.LogDebug(ex, "Could not enable the mount driver log for {Device}", device); }
        });
    }

    private async Task EnableFileLogAsync(string device) {
        await _indi.SetSwitchAsync(device, "DEBUG",
            new Dictionary<string, bool> { ["ENABLE"] = true, ["DISABLE"] = false });
        // The logging properties are only defined once debugging is on.
        for (int i = 0; i < 10 && _indi.GetProperty(device, "LOG_OUTPUT") == null; i++)
            await Task.Delay(500);
        if (_indi.GetProperty(device, "LOGGING_LEVEL") != null)
            await _indi.SetSwitchAsync(device, "LOGGING_LEVEL", new Dictionary<string, bool> {
                ["LOG_ERROR"] = false, ["LOG_WARNING"] = false, ["LOG_SESSION"] = false, ["LOG_DEBUG"] = true });
        if (_indi.GetProperty(device, "LOG_OUTPUT") == null) {
            _logger.LogInformation("Mount driver {Device} has no LOG_OUTPUT property; its debug log stays off", device);
            return;
        }
        await _indi.SetSwitchAsync(device, "LOG_OUTPUT",
            new Dictionary<string, bool> { ["CLIENT_DEBUG"] = false, ["FILE_DEBUG"] = true });
        _logger.LogInformation("Mount driver {Device}: debug log written to {Dir}, kept {Days} days",
            device, LogRoot(), KeepDays);
        SweepOldLogs();
    }

    private static string LogRoot()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".indi", "logs");

    private void SweepOldLogs() {
        try {
            var root = LogRoot();
            if (!Directory.Exists(root)) return;
            foreach (var dir in Directory.EnumerateDirectories(root)) {
                if (!IsExpired(Path.GetFileName(dir), DateTime.Today, KeepDays)) continue;
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        } catch (Exception ex) {
            _logger.LogDebug(ex, "INDI log sweep failed");
        }
    }

    /// <summary>INDI names each day's folder yyyy-MM-dd. Anything else is
    /// left alone.</summary>
    internal static bool IsExpired(string folderName, DateTime today, int keepDays)
        => DateTime.TryParseExact(folderName, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
               System.Globalization.DateTimeStyles.None, out var day)
           && day < today.AddDays(-keepDays);
}
