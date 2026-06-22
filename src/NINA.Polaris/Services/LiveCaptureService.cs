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
/// Opt-in server-owned LIVE capture loop (UserProfile.LiveServerLoopEnabled).
/// The server becomes the orchestrator of the LIVE session: it drives every
/// exposure and keeps going even if the browser disconnects. The client's
/// WASM live-stacking is then pure compute offload — it consumes the relayed
/// frames but no longer controls the cadence.
///
/// Frame path is identical to the client-driven <c>POST /api/camera/capture</c>
/// loop: when the LIVE stacker is running the frame is fed to it
/// (<see cref="LiveStackingService.AddFrameAsync"/>, which itself relays the
/// stacked/raw result + fires FrameIntegrated → triggers / save-to-disk),
/// otherwise the raw frame is relayed as <see cref="FrameKind.Live"/> so the
/// LIVE canvas + client stacker still update. Each exposure is wrapped in
/// <see cref="CaptureProgressService"/> so the unified "Xs of Ys" countdown
/// is server-driven here too.
///
/// Between frames the loop pauses while the guider is dithering/settling or
/// an auto-focus run is active, so the trigger work owns the camera and a
/// dither lands cleanly between subs (parity with the client loop's
/// "dither only after the current capture" behaviour).
/// </summary>
public sealed class LiveCaptureService {
    private readonly EquipmentManager _equip;
    private readonly LiveStackingService _liveStack;
    private readonly ImageRelayService _relay;
    private readonly CaptureProgressService _captureProgress;
    private readonly ActiveGuiderProvider _guiders;
    private readonly AutoFocusService _autoFocus;
    private readonly ILogger<LiveCaptureService> _logger;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private readonly object _lock = new();

    public bool IsRunning { get; private set; }
    public double ExposureSeconds { get; private set; }
    public int Gain { get; private set; }
    public int BinX { get; private set; }
    public long FrameCount { get; private set; }
    public string? LastError { get; private set; }

    public LiveCaptureService(EquipmentManager equip, LiveStackingService liveStack,
        ImageRelayService relay, CaptureProgressService captureProgress,
        ActiveGuiderProvider guiders, AutoFocusService autoFocus,
        ILogger<LiveCaptureService> logger) {
        _equip = equip;
        _liveStack = liveStack;
        _relay = relay;
        _captureProgress = captureProgress;
        _guiders = guiders;
        _autoFocus = autoFocus;
        _logger = logger;
    }

    /// <summary>Start the server-side LIVE loop. No-op (returns false) if a
    /// loop is already running or no camera is connected.</summary>
    public bool Start(double exposureSeconds, int gain, int binning) {
        lock (_lock) {
            if (IsRunning) return false;
            if (_equip.Camera == null || !_equip.Camera.IsConnected) {
                LastError = "No camera connected";
                return false;
            }
            ExposureSeconds = exposureSeconds > 0 ? exposureSeconds : 1.0;
            Gain = gain;
            BinX = binning > 0 ? binning : 1;
            FrameCount = 0;
            LastError = null;
            IsRunning = true;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _loopTask = Task.Run(() => RunLoop(ct));
            _logger.LogInformation(
                "Server LIVE loop started: exp={Exp}s gain={Gain} bin={Bin}",
                ExposureSeconds, Gain, BinX);
            return true;
        }
    }

    /// <summary>Stop the loop. Safe to call when idle.</summary>
    public void Stop() {
        CancellationTokenSource? cts;
        lock (_lock) {
            if (!IsRunning) return;
            IsRunning = false;
            cts = _cts;
            _cts = null;
        }
        try { cts?.Cancel(); } catch { }
        _logger.LogInformation("Server LIVE loop stopped");
    }

    private async Task RunLoop(CancellationToken ct) {
        try {
            var cam = _equip.Camera;
            if (cam == null) return;
            if (BinX > 0) {
                try { await cam.SetBinningAsync(BinX, BinX); } catch { /* best effort */ }
            }

            while (!ct.IsCancellationRequested) {
                // Pause between frames while a dither/settle or an auto-focus
                // run owns the camera, so subs aren't trailed and the trigger
                // work completes before the next exposure.
                while (!ct.IsCancellationRequested && ShouldPause()) {
                    try { await Task.Delay(250, ct); } catch { return; }
                }
                if (ct.IsCancellationRequested) break;

                IImageData image;
                try {
                    var opts = new CaptureOptions(
                        Gain: Gain > 0 ? Gain : (int?)null,
                        BinX: BinX, BinY: BinX,
                        ImageType: "LIGHT");
                    using (_captureProgress.Begin("live", ExposureSeconds))
                        image = await CameraCaptureGate.RunAsync(
                            () => cam.CaptureAsync(ExposureSeconds, opts, ct), ct);
                } catch (OperationCanceledException) {
                    break;
                } catch (Exception ex) {
                    _logger.LogWarning(ex, "Server LIVE frame capture failed; backing off 1s");
                    LastError = ex.Message;
                    try { await Task.Delay(1000, ct); } catch { break; }
                    continue;
                }

                try {
                    if (_liveStack.IsRunning) {
                        if (ExposureSeconds > 0) _liveStack.AverageExposureSec = ExposureSeconds;
                        await _liveStack.AddFrameAsync(image, ct);
                    } else {
                        await _relay.RelayImageAsync(image, FrameKind.Live, ct);
                    }
                    FrameCount++;
                } catch (OperationCanceledException) {
                    break;
                } catch (Exception ex) {
                    _logger.LogWarning(ex, "Server LIVE frame integrate/relay failed");
                    LastError = ex.Message;
                }

                // Honour the live-stack duration cap so the server loop ends
                // when the session's max duration is reached.
                if (_liveStack.IsRunning && _liveStack.DurationCapReached) {
                    _logger.LogInformation("Server LIVE loop ending: stack duration cap reached");
                    break;
                }
            }
        } finally {
            lock (_lock) { IsRunning = false; }
        }
    }

    /// <summary>True while the camera should NOT start a new LIVE sub: a
    /// dither/settle is in progress or an auto-focus run is active.</summary>
    private bool ShouldPause() {
        try {
            var g = _guiders.Active;
            if (g != null && (g.IsDithering || g.IsSettling)) return true;
        } catch { /* guider not ready */ }
        try {
            if (!_autoFocus.State.ToString().Equals("Idle", StringComparison.OrdinalIgnoreCase))
                return true;
        } catch { /* autofocus not ready */ }
        return false;
    }
}
