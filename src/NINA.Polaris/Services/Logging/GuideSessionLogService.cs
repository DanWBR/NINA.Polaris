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

namespace NINA.Polaris.Services.Logging;

/// <summary>
/// ASIAIR-style per-session guiding logs. Watches the guider lifecycle and, for
/// every guiding session, saves a dedicated file under
/// <c>{LocalAppData}/NINA.Polaris/logs/guide/</c>:
///
///  • <b>Native guider</b> → writes a PHD2-compatible Guide Log
///    (<see cref="GuideLogWriter"/>) from the guider's own step/dither/settle
///    events, so it opens in PHD2 Log Viewer just like a real PHD2 log.
///  • <b>External PHD2</b> → PHD2 already writes its own guide log in the user's
///    home (<c>~/Documents/PHD2</c>); on session end we copy that file into the
///    same folder so all session logs live together.
///
/// Opt-out via <see cref="ProfileService.Active"/><c>.SaveGuideLogs</c> (default
/// on). Subscribes to both concrete guiders (only the active one ever fires an
/// active-guiding state), and keys each session by which backend raised it.
/// </summary>
public sealed class GuideSessionLogService : BackgroundService {
    private readonly NativeGuider _native;
    private readonly PHD2Client _phd2;
    private readonly ProfileService _profiles;
    private readonly EquipmentManager _equip;
    private readonly ILogger<GuideSessionLogService> _logger;

    public static string GuideLogDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NINA.Polaris", "logs", "guide");

    private readonly object _lock = new();
    private GuideLogWriter? _writer;     // native session only
    private bool _active;
    private string? _backend;            // "native" | "phd2"
    private DateTime _begin;
    private int _frame;
    private int _guideCount;
    private bool _prevDither;
    private bool _guidingHeaderWritten;

    public GuideSessionLogService(NativeGuider native, PHD2Client phd2, ProfileService profiles,
            EquipmentManager equip, ILogger<GuideSessionLogService> logger) {
        _native = native;
        _phd2 = phd2;
        _profiles = profiles;
        _equip = equip;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) {
        try { Directory.CreateDirectory(GuideLogDir); } catch { /* best-effort */ }

        _native.AppStateChanged += s => OnState("native", _native, s);
        _native.GuideStepReceived += step => OnStep("native", step);
        _native.Settled += r => OnSettled("native", r);

        _phd2.AppStateChanged += s => OnState("phd2", _phd2, s);
        // PHD2 writes its own frame log; we only need lifecycle from it.

        // Close a session cleanly on shutdown.
        stoppingToken.Register(() => { lock (_lock) { if (_active) EndSession(); } });
        return Task.CompletedTask;
    }

    private bool Enabled => _profiles.Active?.SaveGuideLogs != false;

    private void OnState(string backend, IGuider guider, string state) {
        try {
            lock (_lock) {
                bool activeState = state is "Guiding" or "Calibrating";
                if (activeState && !_active && Enabled) {
                    StartSession(backend, guider);
                } else if (state == "Stopped" && _active && _backend == backend) {
                    EndSession();
                }
            }
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Guide session log: state handler failed");
        }
    }

    private void StartSession(string backend, IGuider guider) {
        _active = true;
        _backend = backend;
        _begin = DateTime.UtcNow;
        _frame = 0;
        _guideCount = 0;
        _prevDither = false;
        _guidingHeaderWritten = false;

        if (backend == "native") {
            var stamp = _begin.ToLocalTime().ToString("yyyy-MM-dd_HHmmss");
            var path = Path.Combine(GuideLogDir, $"PHD2_GuideLog_{stamp}.txt");
            try {
                _writer = new GuideLogWriter(path);
                _writer.WriteLine(GuideLogWriter.FormatVersionLine(_begin));
                _logger.LogInformation("Guide session log started: {Path}", path);
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Guide session log: cannot create {Path}", path);
                _writer = null;
            }
        }
        // External PHD2: nothing to open now; we copy PHD2's own log on end.
    }

    private void WriteGuidingHeaderIfNeeded() {
        if (_guidingHeaderWritten || _writer == null) return;
        _guidingHeaderWritten = true;
        var rig = _profiles.ActiveEquipmentProfile;
        var cam = _equip.GuideCamera;
        string camName = cam?.DeviceName ?? "Guide camera";
        _writer.WriteLine(GuideLogWriter.FormatGuidingBeginsHeader(
            DateTime.UtcNow, rig?.Name ?? "Polaris", camName,
            _native.ExposureMs, _native.PixelScale, 0, 0));
    }

    private void OnStep(string backend, GuideStep step) {
        try {
            lock (_lock) {
                if (!_active || _backend != backend || _writer == null) return;
                // Only log while actually guiding (skip pre-guide loop frames).
                if (!_native.IsGuiding) return;
                WriteGuidingHeaderIfNeeded();

                // Dither rising edge → INFO marker so PHD2 Log Viewer hatches it.
                if (step.Dither && !_prevDither) {
                    _writer.WriteLine(GuideLogWriter.FormatDitherInfo(
                        step.RaPixels, step.DecPixels, 0, 0));
                }
                _prevDither = step.Dither;

                _frame++;
                _guideCount++;
                double t = (step.Timestamp.ToUniversalTime() - _begin).TotalSeconds;
                if (t < 0) t = 0;
                _writer.WriteLine(GuideLogWriter.FormatFrameRow(
                    _frame, t, step.RaPixels, step.DecPixels,
                    step.RaDuration, step.RaDirection, step.DecDuration, step.DecDirection,
                    step.Mass, step.SNR));
            }
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Guide session log: step handler failed");
        }
    }

    private void OnSettled(string backend, SettleResult r) {
        lock (_lock) {
            if (!_active || _backend != backend || _writer == null) return;
            string msg = r.Status == 0 ? "Settling complete" : (r.Error ?? "Settling failed");
            _writer.WriteLine(GuideLogWriter.FormatSettlingInfo(msg));
        }
    }

    private void EndSession() {
        try {
            if (_backend == "native" && _writer != null) {
                double dur = (DateTime.UtcNow - _begin).TotalSeconds;
                _writer.WriteLine(GuideLogWriter.FormatGuidingEnds(DateTime.UtcNow));
                _writer.WriteLine(GuideLogWriter.FormatSummary(0, _guideCount, dur));
                _writer.Dispose();
                _logger.LogInformation("Guide session log closed: {Path}", _writer.Path);
            } else if (_backend == "phd2") {
                CopyExternalPhd2Log();
            }
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Guide session log: end failed");
        } finally {
            _writer = null;
            _active = false;
            _backend = null;
        }
    }

    /// <summary>Copy PHD2's own guide log for this session out of the user's home
    /// into the Polaris guide-log folder so every session's log lives together.
    /// PHD2 keeps one guide log per app run and appends across sessions, so we
    /// copy the newest one touched since this session began.</summary>
    private void CopyExternalPhd2Log() {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dirs = new[] {
            _profiles.Active?.Phd2GuideLogDir,
            Path.Combine(home, "Documents", "PHD2"),
            Path.Combine(home, "PHD2"),
        };
        foreach (var dir in dirs) {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
            try {
                var newest = new DirectoryInfo(dir)
                    .EnumerateFiles("PHD2_GuideLog_*.txt")
                    .Where(f => f.LastWriteTimeUtc >= _begin.AddMinutes(-2))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();
                if (newest == null) continue;
                var dest = Path.Combine(GuideLogDir, newest.Name);
                File.Copy(newest.FullName, dest, overwrite: true);
                _logger.LogInformation("Copied external PHD2 guide log {Src} -> {Dest}",
                    newest.FullName, dest);
                return;
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Guide session log: PHD2 copy from {Dir} failed", dir);
            }
        }
        _logger.LogInformation(
            "External PHD2 guide log not found in ~/Documents/PHD2 or ~/PHD2 " +
            "(set a custom path in Settings if PHD2 logs elsewhere).");
    }
}
