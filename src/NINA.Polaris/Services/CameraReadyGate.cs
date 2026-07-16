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
/// Shared "wait until the main camera is ready before capturing" gate.
///
/// The INDI driver watchdog is global: on a wedged driver it restarts the driver,
/// reconnects the device and restores the cooler — a recovery that takes on the
/// order of tens of seconds. For that recovery to actually SAVE a capture run, the
/// run has to stay out of the way and pick back up when the camera returns. Only
/// the LIVE loop did (its private WaitForCameraReadyAsync). AUTORUN, the ADV tree
/// sequencer and PLAN each had a weaker null-check that let a disconnected camera
/// through, so during a restart they either burned their frame budget on fast
/// failures (AUTORUN skipped ~one frame every 2 s, fast-forwarding through the
/// night) or aborted the whole run (ADV/PLAN) — the latter able to trip a plan's
/// host-shutdown end action. This centralises the wait so every capture path
/// shares the one behaviour instead of reinventing a lesser one.
///
/// Depends on a <see cref="System.Func{ICamera}"/> accessor rather than
/// EquipmentManager directly, so it is unit-testable (EquipmentManager needs a
/// live IndiClient and exposes Camera with a private setter) and carries no hard
/// edge to the equipment graph.
/// </summary>
public class CameraReadyGate {
    private readonly Func<ICamera?> _camera;
    private readonly ILogger<CameraReadyGate> _logger;

    public CameraReadyGate(Func<ICamera?> camera, ILogger<CameraReadyGate> logger) {
        _camera = camera;
        _logger = logger;
    }

    /// <summary>Ready = present AND connected. A camera that is selected but
    /// disconnected (mid-restart) is NOT ready; capturing against it writes to
    /// properties that no longer exist.</summary>
    public static bool IsReady(ICamera? cam) => cam != null && cam.IsConnected;

    /// <summary>Block until the main camera is present and connected, returning it;
    /// null means <paramref name="ct"/> was cancelled while waiting (user stop /
    /// run abort). Re-resolves the camera on every poll, so a reconnect that swaps
    /// the ICamera instance hands back the live one, never a stale reference.
    ///
    /// Polls at 1 s and logs ONCE per outage (not per poll) — the per-second log
    /// flood was the exact symptom this replaces. <paramref name="context"/> tags
    /// the log line with the caller (AUTORUN / ADV / LIVE).</summary>
    public async Task<ICamera?> WaitAsync(string context, CancellationToken ct,
                                          Action<string>? onWaiting = null,
                                          Action? onReady = null) {
        var cam = _camera();
        if (IsReady(cam)) return cam;

        var state = cam == null ? "no camera selected" : "disconnected";
        _logger.LogWarning("{Context}: camera not ready ({State}); waiting for it before capturing",
            context, state);
        onWaiting?.Invoke(state);

        while (!ct.IsCancellationRequested) {
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return null; }
            cam = _camera();
            if (IsReady(cam)) {
                _logger.LogInformation("{Context}: camera ready, resuming capture", context);
                onReady?.Invoke();
                return cam;
            }
        }
        return null;
    }
}
