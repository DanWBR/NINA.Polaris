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
/// Independent capture+save loop for the AUXILIARY camera — a second camera on
/// the same mount with different optics. It runs on its own cadence (its own
/// exposure/gain/binning) while a main imaging session (LIVE or AUTORUN) is
/// active, and only saves each frame to a separate <c>aux/</c> subtree. It does
/// NOT guide, plate solve, live stack, or feed the sequencer.
///
/// Activation is reference-counted: the LIVE loop and the sequence engine call
/// <see cref="NotifySessionActive"/> on start/stop. The loop runs only while a
/// session is active AND the rig has <c>AuxEnabled</c> AND the aux camera is
/// connected. Like the guide camera, it deliberately uses a SEPARATE capture
/// gate (<see cref="AuxCameraCaptureGate"/>) so it never contends with the main
/// imaging camera; it pauses while the mount is busy (dither/settle/AF/flip/
/// slew) so trailed subs aren't archived.
/// </summary>
public sealed class AuxCaptureService {
    private readonly EquipmentManager _equip;
    private readonly ImageWriterService _writer;
    private readonly ProfileService _profiles;
    private readonly ActiveGuiderProvider _guiders;
    private readonly AutoFocusService _autoFocus;
    private readonly MeridianFlipService _meridian;
    private readonly DitherBarrier _barrier;
    private readonly ILogger<AuxCaptureService> _logger;

    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private int _sessionRefCount;

    public bool IsRunning { get; private set; }
    public long FrameCount { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>True when aux capture is enabled but no image output folder is
    /// configured, so every frame would be silently dropped (mirrors the LIVE
    /// warning). Surfaced on the status payload.</summary>
    public bool NoOutputDir =>
        Rig?.AuxEnabled == true && !_writer.HasOutputDir;

    public AuxCaptureService(EquipmentManager equip, ImageWriterService writer,
        ProfileService profiles, ActiveGuiderProvider guiders, AutoFocusService autoFocus,
        MeridianFlipService meridian, DitherBarrier barrier, ILogger<AuxCaptureService> logger) {
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

    /// <summary>A main session (LIVE / AUTORUN) started (true) or stopped (false).
    /// Reference-counted so overlapping sessions keep the aux loop alive until
    /// the last one ends.</summary>
    public void NotifySessionActive(bool active) {
        lock (_lock) {
            if (active) _sessionRefCount++;
            else _sessionRefCount = Math.Max(0, _sessionRefCount - 1);
            Reevaluate();
        }
    }

    /// <summary>Re-check whether the loop should be running (call after the user
    /// toggles AuxEnabled or connects/disconnects the aux camera).</summary>
    public void Sync() { lock (_lock) Reevaluate(); }

    private void Reevaluate() {
        bool shouldRun = _sessionRefCount > 0
            && Rig?.AuxEnabled == true
            && _equip.AuxCamera != null
            && _equip.AuxCamera.IsConnected;
        if (shouldRun && !IsRunning) StartLoop();
        else if (!shouldRun && IsRunning) StopLoop();
    }

    private void StartLoop() {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        FrameCount = 0;
        LastError = null;
        IsRunning = true;
        // Blocking barrier participant (the mount must not dither mid aux-sub),
        // never primary so it only owns the cadence when it is the slowest cam.
        _barrier.Register("aux", blocking: true, isPrimary: false);
        _loopTask = Task.Run(() => RunLoop(ct));
        _logger.LogInformation("Aux capture loop started");
    }

    private void StopLoop() {
        IsRunning = false;
        try { _cts?.Cancel(); } catch { }
        _barrier.Deregister("aux");
        _cts = null;
        _loopTask = null;
        _logger.LogInformation("Aux capture loop stopped after {Count} frames", FrameCount);
    }

    private async Task RunLoop(CancellationToken ct) {
        var cam = _equip.AuxCamera;
        if (cam == null) return;
        try {
            // Apply binning once up front (best effort).
            int bin = Math.Clamp(Rig?.AuxBinning ?? 1, 1, 4);
            try { await cam.SetBinningAsync(bin, bin, ct); } catch { /* best effort */ }

            while (!ct.IsCancellationRequested) {
                // Pause while the mount is moving — same OTA on the same mount,
                // so a dither/settle/AF/flip/slew trails the aux frame too.
                while (!ct.IsCancellationRequested && MountBusy()) {
                    try { await Task.Delay(250, ct); } catch { return; }
                }
                if (ct.IsCancellationRequested) break;

                // Synchronized-dither barrier: park here if a dither round is in
                // flight so the mount never moves while this sub is exposing.
                await _barrier.BeforeSubAsync("aux", ct);
                if (ct.IsCancellationRequested) break;

                double expSec = Math.Max(0.05, (Rig?.AuxExposureMs ?? 5000) / 1000.0);
                int? gain = Rig?.AuxGain is int g && g > 0 ? g : null;
                IImageData? image;
                try {
                    var opts = new CaptureOptions(Gain: gain, BinX: bin, BinY: bin, ImageType: "LIGHT");
                    image = await AuxCameraCaptureGate.RunAsync(
                        () => cam.CaptureAsync(expSec, opts, ct), ct,
                        acquireTimeout: TimeSpan.FromSeconds(expSec + 60));
                } catch (OperationCanceledException) {
                    break;
                } catch (Exception ex) {
                    _logger.LogWarning(ex, "Aux capture failed; backing off 2s");
                    LastError = ex.Message;
                    try { await Task.Delay(2000, ct); } catch { break; }
                    continue;
                }

                if (image?.Data != null) {
                    try {
                        var path = _writer.SaveImage(image, imageType: "AUX",
                            gain: gain ?? 0,
                            focalLengthMmOverride: Rig?.AuxFocalLengthMm);
                        if (path != null) FrameCount++;
                    } catch (Exception ex) {
                        _logger.LogWarning(ex, "Aux frame save failed");
                        LastError = ex.Message;
                    }
                }

                // Sub finished: report to the barrier. When the aux is the
                // slowest camera it owns the dither cadence and this call runs
                // (and awaits) the synchronized dither round; otherwise it is a
                // cheap no-op.
                try { await _barrier.AfterSubAsync("aux", expSec, ct); }
                catch (OperationCanceledException) { break; }
            }
        } catch (OperationCanceledException) {
            /* normal stop */
        } catch (Exception ex) {
            _logger.LogError(ex, "Aux capture loop crashed");
            LastError = ex.Message;
        } finally {
            lock (_lock) { if (_cts == null) IsRunning = false; }
        }
    }

    /// <summary>True while the mount is moving or about to move, so the aux frame
    /// would be trailed: a dither/settle, an auto-focus run, a meridian flip, or
    /// an active slew.</summary>
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
