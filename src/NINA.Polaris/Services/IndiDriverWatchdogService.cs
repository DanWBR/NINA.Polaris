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

using System.Collections.Concurrent;
using NINA.INDI.Client;
using NINA.INDI.Protocol;

namespace NINA.Polaris.Services;

/// <summary>
/// Detects a wedged INDI driver — one that stopped delivering BLOBs, the
/// classic "capture hangs then times out" symptom (e.g. indi_asi_ccd dropping
/// the CCD1 BLOB after a mid-exposure USB/reconfig event) — and restarts JUST
/// that driver through indi-web, instead of the futile device reconnect that
/// doesn't touch the stuck driver process.
///
/// Signal: <see cref="IndiClient.BlobTimeout"/>, raised by the camera adapter
/// when its per-capture deadline elapses with no BLOB. After
/// <see cref="WedgeThreshold"/> timeouts for the same device inside
/// <see cref="WedgeWindow"/>, and only while indi-web owns the server, the
/// watchdog resolves the device's driver label and POSTs
/// <c>/api/drivers/restart/&lt;label&gt;</c>. Restarts are rate-limited
/// (<see cref="MinRestartInterval"/> between attempts for a driver, capped at
/// <see cref="MaxRestartsPerWindow"/> per <see cref="RestartWindow"/>) so a
/// genuinely broken driver isn't bounced forever — after the cap it backs off
/// and tells the user to intervene.
///
/// It only ever restarts a driver (never moves hardware) and is a no-op when
/// indi-web isn't running (e.g. the user connected to an external indiserver we
/// don't manage — there we can't restart a driver, so we just surface it).
/// </summary>
public class IndiDriverWatchdogService : IHostedService {
    private readonly IndiClient _indi;
    private readonly IndiWebManagerService _indiWeb;
    private readonly NotificationService _notify;
    private readonly ILogger<IndiDriverWatchdogService> _logger;
    private readonly IConfiguration _config;

    // Tunables (overridable via IndiWatchdog:* config).
    public bool Enabled { get; private set; }
    public int WedgeThreshold { get; }
    public TimeSpan WedgeWindow { get; }
    public TimeSpan MinRestartInterval { get; }
    public int MaxRestartsPerWindow { get; }
    public TimeSpan RestartWindow { get; }

    private sealed class DeviceState {
        public readonly object Gate = new();
        public readonly List<DateTime> Timeouts = new();      // recent BLOB timeouts
        public readonly List<DateTime> Restarts = new();      // recent driver restarts
        public DateTime LastRestartAt = DateTime.MinValue;
        public bool RestartInFlight;
    }
    private readonly ConcurrentDictionary<string, DeviceState> _byDevice = new();

    // Rolling record of what the watchdog last did, for the UI/status.
    public record ActionRecord(DateTime At, string Device, string? DriverLabel,
                               string Result);
    private readonly object _lastLock = new();
    private ActionRecord? _lastAction;
    public ActionRecord? LastAction { get { lock (_lastLock) return _lastAction; } }

    public IndiDriverWatchdogService(IndiClient indi, IndiWebManagerService indiWeb,
                                     NotificationService notify,
                                     IConfiguration config,
                                     ILogger<IndiDriverWatchdogService> logger) {
        _indi = indi;
        _indiWeb = indiWeb;
        _notify = notify;
        _config = config;
        _logger = logger;

        Enabled = config.GetValue("IndiWatchdog:Enabled", true);
        WedgeThreshold = Math.Max(1, config.GetValue("IndiWatchdog:WedgeThreshold", 2));
        WedgeWindow = TimeSpan.FromSeconds(config.GetValue("IndiWatchdog:WedgeWindowSec", 300));
        MinRestartInterval = TimeSpan.FromSeconds(config.GetValue("IndiWatchdog:MinRestartIntervalSec", 60));
        MaxRestartsPerWindow = Math.Max(1, config.GetValue("IndiWatchdog:MaxRestartsPerWindow", 3));
        RestartWindow = TimeSpan.FromSeconds(config.GetValue("IndiWatchdog:RestartWindowSec", 1800));
    }

    public Task StartAsync(CancellationToken cancellationToken) {
        _indi.BlobTimeout += OnBlobTimeout;
        _logger.LogInformation(
            "IndiDriverWatchdog armed (enabled={Enabled}, threshold={T} in {W}s, " +
            "min {Min}s between restarts, max {Max}/{RW}s)",
            Enabled, WedgeThreshold, WedgeWindow.TotalSeconds,
            MinRestartInterval.TotalSeconds, MaxRestartsPerWindow, RestartWindow.TotalSeconds);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) {
        _indi.BlobTimeout -= OnBlobTimeout;
        return Task.CompletedTask;
    }

    public void SetEnabled(bool enabled) => Enabled = enabled;

    /// <summary>Snapshot for the RIGS UI / status endpoint.</summary>
    public object Status() => new {
        enabled = Enabled,
        indiWebRunning = _indiWeb.Running,
        wedgeThreshold = WedgeThreshold,
        wedgeWindowSec = (int)WedgeWindow.TotalSeconds,
        minRestartIntervalSec = (int)MinRestartInterval.TotalSeconds,
        maxRestartsPerWindow = MaxRestartsPerWindow,
        restartWindowSec = (int)RestartWindow.TotalSeconds,
        lastAction = LastAction,
    };

    private void OnBlobTimeout(string device) {
        if (string.IsNullOrWhiteSpace(device)) return;
        // Fire-and-forget: the event is raised from the capture timer thread and
        // must not block; the restart itself is an async HTTP call.
        _ = Task.Run(() => HandleWedgeAsync(device));
    }

    private async Task HandleWedgeAsync(string device) {
        try {
            if (!Enabled) return;

            var st = _byDevice.GetOrAdd(device, _ => new DeviceState());
            bool shouldRestart;
            lock (st.Gate) {
                var now = DateTime.UtcNow;
                Prune(st.Timeouts, now - WedgeWindow);
                st.Timeouts.Add(now);
                if (st.Timeouts.Count < WedgeThreshold) {
                    _logger.LogInformation(
                        "INDI watchdog: {Device} BLOB timeout {N}/{T} within {W}s",
                        device, st.Timeouts.Count, WedgeThreshold, (int)WedgeWindow.TotalSeconds);
                    return;
                }
                if (st.RestartInFlight) return;

                // Rate-limits: min gap since last restart + max restarts per window.
                Prune(st.Restarts, now - RestartWindow);
                if (now - st.LastRestartAt < MinRestartInterval) {
                    _logger.LogDebug("INDI watchdog: {Device} within min restart interval, skipping", device);
                    return;
                }
                if (st.Restarts.Count >= MaxRestartsPerWindow) {
                    Record(device, null, "capped");
                    _notify.Push("error",
                        $"INDI driver for {device} keeps wedging — restarted " +
                        $"{st.Restarts.Count}× in {(int)RestartWindow.TotalMinutes} min. " +
                        $"Manual intervention needed (check cabling / power / USB).", 12000);
                    _logger.LogWarning(
                        "INDI watchdog: {Device} hit restart cap ({N}/{RW}min) — backing off",
                        device, st.Restarts.Count, (int)RestartWindow.TotalMinutes);
                    return;
                }
                shouldRestart = true;
                st.RestartInFlight = true;
            }

            if (!shouldRestart) return;
            try {
                await AttemptRestartAsync(device, st);
            } finally {
                lock (st.Gate) st.RestartInFlight = false;
            }
        } catch (Exception ex) {
            _logger.LogWarning(ex, "INDI watchdog failed handling wedge for {Device}", device);
        }
    }

    private async Task AttemptRestartAsync(string device, DeviceState st) {
        // We can only restart a driver when indi-web owns the indiserver. If the
        // user connected to an external server we don't manage, surface it and
        // stop — a device reconnect wouldn't fix a stuck driver anyway.
        if (!_indiWeb.Running) {
            Record(device, null, "indi-web-not-running");
            _notify.Push("warn",
                $"INDI camera {device} stopped delivering frames (driver wedged). " +
                $"Polaris can auto-restart the driver only when the embedded INDI Web " +
                $"Manager runs it — restart the driver manually on your INDI host.", 12000);
            _logger.LogWarning("INDI watchdog: {Device} wedged but indi-web not running", device);
            return;
        }

        var label = await ResolveDriverLabelAsync(device);
        if (string.IsNullOrWhiteSpace(label)) {
            Record(device, null, "label-unresolved");
            _notify.Push("warn",
                $"INDI camera {device} wedged; couldn't map it to an indi-web driver " +
                $"to restart. Restart it from the INDI Web Manager tab.", 12000);
            _logger.LogWarning("INDI watchdog: could not resolve driver label for {Device}", device);
            return;
        }

        _notify.Push("info",
            $"INDI driver '{label}' ({device}) stopped delivering frames — restarting it…", 8000);
        _logger.LogWarning(
            "INDI watchdog: restarting wedged driver '{Label}' for device {Device}", label, device);

        var ok = await _indiWeb.RestartDriverAsync(label);
        lock (st.Gate) {
            var now = DateTime.UtcNow;
            st.LastRestartAt = now;
            st.Restarts.Add(now);
            st.Timeouts.Clear();   // give the freshly restarted driver a clean slate
        }
        Record(device, label, ok ? "restarted" : "restart-failed");
        if (ok) {
            _notify.Push("info",
                $"INDI driver '{label}' restarted. Reconnect the camera if the next " +
                $"capture doesn't recover on its own.", 8000);
        } else {
            _notify.Push("error",
                $"Failed to restart INDI driver '{label}': {_indiWeb.LastError}", 12000);
        }
    }

    /// <summary>
    /// Map an INDI device name to the driver label indi-web restarts. Preferred
    /// source is the device's standard <c>DRIVER_INFO.DRIVER_NAME</c> (which
    /// equals the indi-web label for essentially every stock driver). Falls back
    /// to matching the device name against indi-web's running driver labels, then
    /// to the sole running driver when there's only one.
    /// </summary>
    private async Task<string?> ResolveDriverLabelAsync(string device) {
        // 1) DRIVER_INFO.DRIVER_NAME straight off the device.
        try {
            if (_indi.GetProperty(device, "DRIVER_INFO") is IndiTextProperty di &&
                di.Values.TryGetValue("DRIVER_NAME", out var dn) &&
                !string.IsNullOrWhiteSpace(dn)) {
                var running0 = await _indiWeb.GetRunningDriverLabelsAsync();
                // Prefer an exact running-label match; otherwise trust DRIVER_NAME.
                var exact = running0.FirstOrDefault(l =>
                    string.Equals(l, dn, StringComparison.OrdinalIgnoreCase));
                return exact ?? dn.Trim();
            }
        } catch (Exception ex) {
            _logger.LogDebug(ex, "INDI watchdog: DRIVER_INFO read failed for {Device}", device);
        }

        // 2) Match the device name against running driver labels (a label is
        //    typically a prefix of the device name, e.g. "ZWO CCD" ⊂
        //    "ZWO CCD ASI2600MC Pro").
        var running = await _indiWeb.GetRunningDriverLabelsAsync();
        var byPrefix = running.FirstOrDefault(l =>
            device.StartsWith(l, StringComparison.OrdinalIgnoreCase) ||
            device.Contains(l, StringComparison.OrdinalIgnoreCase));
        if (byPrefix != null) return byPrefix;

        // 3) Single running driver → it must be the one.
        return running.Count == 1 ? running[0] : null;
    }

    private static void Prune(List<DateTime> stamps, DateTime cutoff)
        => stamps.RemoveAll(t => t < cutoff);

    private void Record(string device, string? label, string result) {
        lock (_lastLock) _lastAction = new ActionRecord(DateTime.UtcNow, device, label, result);
    }
}
