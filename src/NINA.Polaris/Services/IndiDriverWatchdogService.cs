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
    private readonly IServiceProvider _services;

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
        // FIELD6-11: spurious-disconnect reconnects, tracked separately from
        // driver restarts — they're a different remedy for a different fault and
        // must not consume each other's budget.
        public readonly List<DateTime> Reconnects = new();
        public DateTime LastReconnectAt = DateTime.MinValue;
        public bool ReconnectInFlight;
    }
    private readonly ConcurrentDictionary<string, DeviceState> _byDevice = new();

    /// <summary>Cooler state captured just BEFORE a driver restart, so it can be
    /// put back after the reconnect. Keyed by INDI device name.
    ///
    /// Needed because a restarted driver comes up at ITS defaults, and on the
    /// SV405CC the Cooler control's default is 0 (off) — so a camera that was
    /// holding 0°C came back with the TEC dead and quietly warmed up for the rest
    /// of the night. We reconnected the device and stopped there (dc645402), which
    /// fixed capture but not cooling. Field report: "faltou reativar o cooling da
    /// câmera após a reconexão."
    ///
    /// Snapshot rather than "always re-enable": if the user had the cooler OFF on
    /// purpose, a restart must not switch it on.</summary>
    private readonly ConcurrentDictionary<string, bool> _coolerWasOn = new();

    // Rolling record of what the watchdog last did, for the UI/status.
    public record ActionRecord(DateTime At, string Device, string? DriverLabel,
                               string Result);
    private readonly object _lastLock = new();
    private ActionRecord? _lastAction;
    public ActionRecord? LastAction { get { lock (_lastLock) return _lastAction; } }

    public IndiDriverWatchdogService(IndiClient indi, IndiWebManagerService indiWeb,
                                     NotificationService notify,
                                     IConfiguration config,
                                     IServiceProvider services,
                                     ILogger<IndiDriverWatchdogService> logger) {
        _indi = indi;
        _indiWeb = indiWeb;
        _notify = notify;
        _config = config;
        // Resolved lazily rather than constructor-injected: this stays a plain INDI
        // watchdog with no hard edge into the equipment graph (EquipmentManager
        // already depends on IndiClient, so taking it here would invite a cycle).
        _services = services;
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
        _indi.DeviceConnectionLost += OnDeviceConnectionLost;
        _logger.LogInformation(
            "IndiDriverWatchdog armed (enabled={Enabled}, threshold={T} in {W}s, " +
            "min {Min}s between restarts, max {Max}/{RW}s)",
            Enabled, WedgeThreshold, WedgeWindow.TotalSeconds,
            MinRestartInterval.TotalSeconds, MaxRestartsPerWindow, RestartWindow.TotalSeconds);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) {
        _indi.BlobTimeout -= OnBlobTimeout;
        _indi.DeviceConnectionLost -= OnDeviceConnectionLost;
        return Task.CompletedTask;
    }

    /// <summary>
    /// FIELD6-11: a device we connected dropped its CONNECTION on its own. This
    /// is NOT the wedge case the rest of this service handles: the driver process
    /// is alive and answering, it just let go of the hardware, so restarting the
    /// driver is the wrong (and much more disruptive) hammer — the fix is simply
    /// to connect it again, exactly what the operator was doing by hand in RIGS.
    /// Field report: the SV405CC's INDI driver does this mid-session for no
    /// visible reason; capture then silently stops for the rest of the night
    /// because nothing was watching this transition.
    /// Reuses the restart rate limiter's shape so a device that flaps can't spin:
    /// at most one reconnect per MinRestartInterval, MaxRestartsPerWindow per
    /// RestartWindow, after which we go quiet and leave it to the operator.
    /// </summary>
    private void OnDeviceConnectionLost(string device) {
        if (!Enabled || string.IsNullOrWhiteSpace(device)) return;
        // A camera Polaris drives through a vendor SDK must STAY disconnected in
        // INDI: NativeCameraIndiGuard put it that way on purpose, and reconnecting
        // it here would restore the double claim on the USB device that made the
        // ASI585 fail exposures (field session 2026-09-05).
        try {
            if (_services.GetService<NativeCameraIndiGuard>()?.IsNativelyDriven(device) == true) {
                _logger.LogInformation(
                    "INDI '{Device}' disconnected: leaving it that way, Polaris drives that camera " +
                    "through its vendor SDK", device);
                return;
            }
        } catch { /* guard unavailable: fall through to the normal policy */ }
        var st = _byDevice.GetOrAdd(device, _ => new DeviceState());
        var now = DateTime.UtcNow;
        lock (st.Gate) {
            if (st.ReconnectInFlight) return;
            if (now - st.LastReconnectAt < MinRestartInterval) {
                _logger.LogWarning(
                    "INDI '{Device}' dropped again within {Sec}s of the last reconnect — backing off",
                    device, MinRestartInterval.TotalSeconds);
                return;
            }
            st.Reconnects.RemoveAll(t => now - t > RestartWindow);
            if (st.Reconnects.Count >= MaxRestartsPerWindow) {
                _logger.LogError(
                    "INDI '{Device}' has dropped {N} times in {Win}min — giving up auto-reconnect. "
                    + "Reconnect it in RIGS; the driver or the USB link is unhealthy.",
                    device, st.Reconnects.Count, RestartWindow.TotalMinutes);
                _notify.Push("error",
                    $"{device} keeps disconnecting on its own — auto-reconnect gave up. " +
                    $"Reconnect it in RIGS; the driver or the USB link is unhealthy.", 15000);
                Record(device, null, "reconnect-gave-up");
                return;
            }
            st.ReconnectInFlight = true;
            st.LastReconnectAt = now;
            st.Reconnects.Add(now);
        }

        _ = Task.Run(async () => {
            try {
                _logger.LogWarning("INDI '{Device}': spurious disconnect — reconnecting", device);
                _notify.Push("warn",
                    $"{device} disconnected on its own — reconnecting…", 8000);
                // Let the driver settle before asking again; an immediate CONNECT
                // on a driver that just dropped tends to be ignored.
                await Task.Delay(TimeSpan.FromSeconds(2));
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await _indi.ConnectDeviceAsync(device, cts.Token);
                bool ok = _indi.GetSwitch(device, "CONNECTION", "CONNECT");
                if (ok) {
                    _logger.LogInformation("INDI '{Device}': reconnected", device);
                    _notify.Push("info", $"{device} reconnected", 6000);
                } else {
                    _logger.LogError("INDI '{Device}': reconnect issued but device still reports disconnected", device);
                }
                Record(device, null, ok ? "reconnected" : "reconnect-failed");
            } catch (Exception ex) {
                _logger.LogError(ex, "INDI '{Device}': reconnect threw", device);
                Record(device, null, "reconnect-error");
            } finally {
                var s = _byDevice.GetOrAdd(device, _ => new DeviceState());
                lock (s.Gate) s.ReconnectInFlight = false;
            }
        });
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

        // Snapshot cooler state BEFORE the driver dies — once it's gone the device
        // reports its defaults and we'd have no way to tell "was cooling" from
        // "user turned it off".
        CaptureCoolerState(device);

        var ok = await _indiWeb.RestartDriverAsync(label);
        lock (st.Gate) {
            var now = DateTime.UtcNow;
            st.LastRestartAt = now;
            st.Restarts.Add(now);
            st.Timeouts.Clear();   // give the freshly restarted driver a clean slate
        }
        if (!ok) {
            Record(device, label, "restart-failed");
            _notify.Push("error",
                $"Failed to restart INDI driver '{label}': {_indiWeb.LastError}", 12000);
            return;
        }

        // FIELD6-12: a restart is only half the remedy. indiserver tears the
        // driver down with `FIFO: Shutting down driver` + delProperty, so the
        // device VANISHES; the fresh process re-announces it CONNECTION=Off.
        // Restarting and stopping there left the camera disconnected for the
        // rest of the night, which is exactly the field report "the camera
        // disconnects for no reason and I just reconnect it in RIGS" — the
        // reconnect the operator was doing by hand is the missing half.
        // (The class comment above chose restart "instead of the futile device
        // reconnect". Correct that a reconnect alone can't fix a wedged driver;
        // wrong that it's either/or — it's restart THEN reconnect.)
        var reconnected = await TryReconnectAfterRestartAsync(device, label);
        // FIELD7-2: reconnecting is still only two thirds of it. The fresh driver
        // is at its defaults, and on the SV405CC the Cooler control defaults to OFF
        // — so a camera that had been holding 0°C came back with a dead TEC and
        // warmed up unnoticed for the rest of the session.
        if (reconnected) RestoreCoolerAfterReconnect(device);
        Record(device, label, reconnected ? "restarted+reconnected" : "restarted-reconnect-failed");
        if (reconnected) {
            _notify.Push("info",
                $"INDI driver '{label}' restarted and {device} reconnected — capture should resume.", 8000);
        } else {
            _notify.Push("warn",
                $"INDI driver '{label}' restarted, but {device} did not come back. " +
                $"Reconnect it in RIGS.", 12000);
        }
    }

    /// <summary>
    /// Wait for a just-restarted driver to re-announce the device, then connect
    /// it. The device is gone from our snapshot at this point (delProperty), so
    /// poll until its CONNECTION property exists again before writing — a CONNECT
    /// aimed at a device indiserver hasn't re-announced is silently dropped.
    /// </summary>
    /// <summary>Resolve the main or aux camera when it's the INDI device named
    /// <paramref name="device"/>. Null when the device isn't a camera we own (the
    /// watchdog also covers mounts, focusers, wheels — none of which have a cooler).</summary>
    private (NINA.Image.Interfaces.ICamera Camera, string Slot)? ResolveCamera(string device) {
        var equip = _services.GetService<EquipmentManager>();
        if (equip == null) return null;
        if (equip.Camera != null && equip.Camera.DeviceName == device)
            return (equip.Camera, CoolingRampService.Main);
        if (equip.AuxCamera != null && equip.AuxCamera.DeviceName == device)
            return (equip.AuxCamera, CoolingRampService.Aux);
        // Extra imaging cameras (slot 2+) cool on their own "imager-N" slot.
        for (int i = 2; i < 2 + equip.ExtraImagerCount; i++) {
            var cam = equip.GetImager(i);
            if (cam != null && cam.DeviceName == device)
                return (cam, $"imager-{i}");
        }
        return null;
    }

    /// <summary>Remember whether the cooler was running, before we tear the driver
    /// down. Best-effort: a camera that's already wedged may answer badly, and a
    /// bad answer must not stop the restart that's trying to rescue it.</summary>
    private void CaptureCoolerState(string device) {
        try {
            var found = ResolveCamera(device);
            if (found == null) return;
            if (!found.Value.Camera.Capabilities.SupportsCooler) return;
            _coolerWasOn[device] = found.Value.Camera.CoolerOn;
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Could not read cooler state for {Device} before restart", device);
        }
    }

    /// <summary>Put the cooler back after a restart+reconnect, if it was on.
    ///
    /// Goes through CoolingRampService rather than writing the setpoint raw, so the
    /// same °C/min rule applies here as everywhere else — and it matters more here
    /// than anywhere: the sensor may have drifted up while the driver was down, and
    /// slamming it back to 0°C is exactly the fast plunge that condenses dew.
    /// Ramping from wherever it actually is, is free, because the ramp reads the
    /// live temperature.</summary>
    private void RestoreCoolerAfterReconnect(string device) {
        try {
            if (!_coolerWasOn.TryRemove(device, out var wasOn) || !wasOn) return;

            var found = ResolveCamera(device);
            if (found == null) return;
            var (cam, slot) = found.Value;

            var profiles = _services.GetService<ProfileService>();
            var ramp = _services.GetService<CoolingRampService>();
            if (ramp == null) return;

            var rig = profiles?.ActiveEquipmentProfile;
            // Per-imager slot ("imager-N") reads that imager's own setpoint; main
            // and aux share the rig-level target.
            double? imagerTarget = null, imagerRate = null;
            if (slot.StartsWith("imager-") && int.TryParse(slot.AsSpan("imager-".Length), out var slotIdx)) {
                var im = rig?.Imagers.ElementAtOrDefault(slotIdx);
                imagerTarget = im?.CoolerTargetTemperature;
                imagerRate = im?.CoolerRampDegPerMinute;
            }
            var target = imagerTarget ?? rig?.CoolerTargetTemperature ?? -10;
            var rate = imagerRate ?? rig?.CoolerRampDegPerMinute ?? 2.0;

            _logger.LogInformation(
                "INDI watchdog: restoring cooler on {Device} → {Target:0.0}°C at {Rate:0.#}°C/min " +
                "(sensor now {Now:0.0}°C)", device, target, rate, cam.Temperature);
            ramp.Start(cam, target, rate, coolerOnFirst: true, coolerOffWhenDone: false,
                       source: "watchdog restore", slot: slot);
            _notify.Push("info",
                $"Cooler re-enabled on {device} after the driver restart (target {target:0.#}°C).", 6000);
        } catch (Exception ex) {
            // Never let this sink the restart: capture is back, which is the point.
            _logger.LogWarning(ex, "Could not restore cooler on {Device} after reconnect", device);
        }
    }

    private async Task<bool> TryReconnectAfterRestartAsync(string device, string label) {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(
            _config.GetValue("IndiWatchdog:ReconnectTimeoutSec", 30));
        try {
            // 1) Wait for the re-announce.
            while (DateTime.UtcNow < deadline) {
                if (_indi.Devices.TryGetValue(device, out var props)
                        && props.ContainsKey("CONNECTION")) break;
                await Task.Delay(500);
            }
            if (!_indi.Devices.TryGetValue(device, out _)) {
                _logger.LogError(
                    "INDI watchdog: '{Device}' never re-appeared after restarting '{Label}'", device, label);
                return false;
            }
            // 2) Honour the per-device pre-connect delay (INDIROB-3) — a driver
            //    that just started is exactly the case that delay exists for.
            await Task.Delay(1000);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _indi.ConnectDeviceAsync(device, cts.Token);
            // 3) Verify rather than assume: ConnectDeviceAsync returns once the
            //    write is out, and INDI never acks writes.
            while (DateTime.UtcNow < deadline) {
                if (_indi.GetSwitch(device, "CONNECTION", "CONNECT")) {
                    _logger.LogInformation(
                        "INDI watchdog: '{Device}' reconnected after restarting '{Label}'", device, label);
                    return true;
                }
                await Task.Delay(500);
            }
            _logger.LogError(
                "INDI watchdog: '{Device}' still reports disconnected after restarting '{Label}'", device, label);
            return false;
        } catch (Exception ex) {
            _logger.LogError(ex,
                "INDI watchdog: reconnect after restarting '{Label}' threw for '{Device}'", label, device);
            return false;
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
