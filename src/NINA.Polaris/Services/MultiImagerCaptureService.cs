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

using System.Linq;
using NINA.Image.Interfaces;

namespace NINA.Polaris.Services;

/// <summary>
/// STAGE2: capture+save loops for the ADDITIONAL imaging cameras (imager index
/// 2+), the N-camera generalization of <see cref="AuxCaptureService"/> (which
/// still owns the single aux slot at index 1). One independent loop per extra
/// imager runs while a main session (LIVE/AUTORUN) is active and that imager is
/// enabled + connected; each saves to its own target and joins the
/// <see cref="DitherBarrier"/> as participant "imager-N" so a dither never fires
/// mid-sub on any of them. Like the aux loop it does NOT guide, plate solve,
/// live stack, or feed the sequencer.
/// </summary>
public sealed class MultiImagerCaptureService {
    private readonly EquipmentManager _equip;
    private readonly ImageWriterService _writer;
    private readonly ProfileService _profiles;
    private readonly ActiveGuiderProvider _guiders;
    private readonly AutoFocusService _autoFocus;
    private readonly MeridianFlipService _meridian;
    private readonly DitherBarrier _barrier;
    private readonly ILogger<MultiImagerCaptureService> _logger;

    private sealed class Loop {
        public readonly CancellationTokenSource Cts = new();
        public Task? Task;
        public long FrameCount;
        public string? LastError;
    }

    private readonly object _lock = new();
    private readonly Dictionary<int, Loop> _loops = new();
    private int _sessionRefCount;

    public MultiImagerCaptureService(EquipmentManager equip, ImageWriterService writer,
        ProfileService profiles, ActiveGuiderProvider guiders, AutoFocusService autoFocus,
        MeridianFlipService meridian, DitherBarrier barrier,
        ILogger<MultiImagerCaptureService> logger) {
        _equip = equip;
        _writer = writer;
        _profiles = profiles;
        _guiders = guiders;
        _autoFocus = autoFocus;
        _meridian = meridian;
        _barrier = barrier;
        _logger = logger;
    }

    private EquipmentProfile? Rig => _profiles.ActiveEquipmentProfile;

    /// <summary>Role/barrier id for imager slot <paramref name="index"/>
    /// (index 2 → "imager-3"), matching <c>EquipmentManager.EnumerateImagers</c>.</summary>
    private static string RoleOf(int index) => $"imager-{index + 1}";

    /// <summary>A main session (LIVE/AUTORUN) started (true) or stopped (false).
    /// Reference-counted so overlapping sessions keep the loops alive until the
    /// last one ends. Mirrors <see cref="AuxCaptureService.NotifySessionActive"/>.</summary>
    public void NotifySessionActive(bool active) {
        lock (_lock) {
            if (active) _sessionRefCount++;
            else _sessionRefCount = Math.Max(0, _sessionRefCount - 1);
            Reevaluate();
        }
    }

    /// <summary>Re-check which extra-imager loops should run (call after the user
    /// selects/connects an extra imager or toggles its Enabled flag).</summary>
    public void Sync() { lock (_lock) Reevaluate(); }

    private void Reevaluate() {
        var rig = Rig;
        var extra = _equip.ExtraImagerCount;
        for (int index = 2; index < 2 + extra; index++) {
            var cfg = rig?.Imagers.ElementAtOrDefault(index);
            bool shouldRun = _sessionRefCount > 0
                && cfg?.Enabled == true
                && _equip.GetImager(index) is { IsConnected: true };
            bool running = _loops.ContainsKey(index);
            if (shouldRun && !running) StartLoop(index);
            else if (!shouldRun && running) StopLoop(index);
        }
        // Drop loops whose imager slot no longer exists.
        foreach (var idx in _loops.Keys.Where(i => i >= 2 + extra).ToList()) StopLoop(idx);
    }

    private void StartLoop(int index) {
        var loop = new Loop();
        _loops[index] = loop;
        var role = RoleOf(index);
        // Blocking barrier participant; never primary (owns the cadence only when
        // it is the slowest imaging camera).
        _barrier.Register(role, blocking: true, isPrimary: false);
        loop.Task = Task.Run(() => RunLoop(index, role, loop, loop.Cts.Token));
        _logger.LogInformation("Imager {Index} capture loop started ({Role})", index, role);
    }

    private void StopLoop(int index) {
        if (!_loops.TryGetValue(index, out var loop)) return;
        try { loop.Cts.Cancel(); } catch { }
        _barrier.Deregister(RoleOf(index));
        _loops.Remove(index);
        _logger.LogInformation("Imager {Index} capture loop stopped after {Count} frames", index, loop.FrameCount);
    }

    private async Task RunLoop(int index, string role, Loop loop, CancellationToken ct) {
        var cam = _equip.GetImager(index);
        if (cam == null) return;
        var cfg = Rig?.Imagers.ElementAtOrDefault(index);
        try {
            int bin = Math.Clamp(cfg?.Binning ?? 1, 1, 4);
            try { await cam.SetBinningAsync(bin, bin, ct); } catch { /* best effort */ }

            while (!ct.IsCancellationRequested) {
                // Pause while the mount is moving — same mount, so a dither/settle/
                // AF/flip/slew trails this frame too.
                while (!ct.IsCancellationRequested && MountBusy()) {
                    try { await Task.Delay(250, ct); } catch { return; }
                }
                if (ct.IsCancellationRequested) break;

                // Park here if a synchronized dither round is in flight.
                await _barrier.BeforeSubAsync(role, ct);
                if (ct.IsCancellationRequested) break;

                // Re-read config each frame so exposure/gain edits take effect.
                cfg = Rig?.Imagers.ElementAtOrDefault(index);
                double expSec = Math.Max(0.05, (cfg?.ExposureMs ?? 5000) / 1000.0);
                int? gain = cfg?.Gain is int g && g > 0 ? g : null;

                IImageData? image;
                try {
                    var opts = new CaptureOptions(Gain: gain, BinX: bin, BinY: bin, ImageType: "LIGHT");
                    image = await cam.CaptureAsync(expSec, opts, ct);
                } catch (OperationCanceledException) {
                    break;
                } catch (Exception ex) {
                    _logger.LogWarning(ex, "Imager {Index} capture failed; backing off 2s", index);
                    loop.LastError = ex.Message;
                    try { await Task.Delay(2000, ct); } catch { break; }
                    continue;
                }

                if (image?.Data != null) {
                    try {
                        var path = _writer.SaveImage(image, targetName: role, imageType: "AUX",
                            gain: gain ?? 0, focalLengthMmOverride: cfg?.FocalLengthMm);
                        if (path != null) loop.FrameCount++;
                    } catch (Exception ex) {
                        _logger.LogWarning(ex, "Imager {Index} frame save failed", index);
                        loop.LastError = ex.Message;
                    }
                }

                // Sub finished: report to the barrier. When this is the slowest of
                // the active imaging cameras it runs the synchronized dither round.
                try { await _barrier.AfterSubAsync(role, expSec, ct); }
                catch (OperationCanceledException) { break; }
            }
        } catch (OperationCanceledException) {
            /* normal stop */
        } catch (Exception ex) {
            _logger.LogError(ex, "Imager {Index} capture loop crashed", index);
            loop.LastError = ex.Message;
        }
    }

    /// <summary>Per-loop status for the WS payload: index, frame count, last error.</summary>
    public IReadOnlyList<(int Index, string Role, long FrameCount, string? LastError)> Snapshot() {
        lock (_lock) {
            return _loops.OrderBy(kv => kv.Key)
                .Select(kv => (kv.Key, RoleOf(kv.Key), kv.Value.FrameCount, kv.Value.LastError))
                .ToList();
        }
    }

    /// <summary>True while the mount is moving or about to move (dither/settle,
    /// auto-focus, meridian flip, active slew), so the frame would be trailed.
    /// Mirrors <see cref="AuxCaptureService"/>'s check.</summary>
    private bool MountBusy() {
        try {
            var g = _guiders.Active;
            if (g != null && (g.IsDithering || g.IsSettling)) return true;
        } catch { }
        try {
            if (!_autoFocus.State.ToString().Equals("Idle", StringComparison.OrdinalIgnoreCase))
                return true;
        } catch { }
        try {
            if (_meridian.State != MeridianFlipState.Idle) return true;
        } catch { }
        try {
            if (_equip.Telescope?.IsSlewing == true) return true;
        } catch { }
        return false;
    }
}
