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

using Microsoft.Extensions.Logging;

namespace NINA.Polaris.Services.Sequencer.Triggers;

/// <summary>
/// Re-acquire guiding if it drops mid-session. Once guiding has been seen
/// active, this fires whenever PHD2 is connected but no longer guiding
/// (lost lock / stopped) — and is NOT calibrating/paused — and restarts it.
/// Mirrors NINA desktop's "Restore Guiding". No-op until the first time
/// guiding actually starts, so it never fights a sequence that hasn't begun
/// guiding yet.
/// </summary>
public class RestoreGuidingTrigger : SequenceTrigger {
    public override string Type => "RestoreGuiding";
    public double SettlePixels { get; set; } = 1.5;
    public int SettleTimeSeconds { get; set; } = 10;
    public int SettleTimeoutSeconds { get; set; } = 60;
    public bool Recalibrate { get; set; } = false;

    public override Task<bool> ShouldFireAsync(SequenceContext ctx, CancellationToken ct) {
        if (!ctx.Guider.IsConnected) return Task.FromResult(false);

        var seenKey = $"RestoreGuiding:{Id}:wasGuiding";
        if (ctx.Guider.IsGuiding) {
            ctx.Scratch[seenKey] = true;   // latch: we've been guiding this run
            return Task.FromResult(false);
        }
        // Don't interrupt an in-progress (re)acquisition.
        if (ctx.Guider.IsCalibrating || ctx.Guider.IsPaused) return Task.FromResult(false);

        var wasGuiding = ctx.Scratch.TryGetValue(seenKey, out var v) && v is true;
        return Task.FromResult(wasGuiding);
    }

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        ctx.Logger.LogWarning("Guiding dropped (guider state '{State}'); restoring guiding.", ctx.Guider.AppState);
        await ctx.Guider.StartGuidingAsync(SettlePixels, SettleTimeSeconds, SettleTimeoutSeconds, Recalibrate);
    }
}

/// <summary>
/// Reconnect the main camera if it drops mid-run (the headless equivalent of
/// NINA desktop's reconnect-on-download-failure resilience). Fires when a
/// camera is selected but reports disconnected, throttled to one attempt per
/// <see cref="MinIntervalSeconds"/> so a genuinely dead camera doesn't spin.
/// </summary>
public class ReconnectCameraTrigger : SequenceTrigger {
    public override string Type => "ReconnectCamera";
    public int MinIntervalSeconds { get; set; } = 15;

    public override Task<bool> ShouldFireAsync(SequenceContext ctx, CancellationToken ct) {
        var cam = ctx.Equipment.Camera;
        if (cam == null || cam.IsConnected) return Task.FromResult(false);

        var key = $"ReconnectCamera:{Id}:lastAttemptUtc";
        if (ctx.Scratch.TryGetValue(key, out var v) && v is DateTime last
                && (DateTime.UtcNow - last).TotalSeconds < Math.Max(1, MinIntervalSeconds)) {
            return Task.FromResult(false);
        }
        ctx.Scratch[key] = DateTime.UtcNow;
        return Task.FromResult(true);
    }

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        var cam = ctx.Equipment.Camera;
        if (cam == null) return;
        ctx.Logger.LogWarning("Main camera '{Name}' disconnected mid-run; attempting reconnect.", cam.DeviceName);
        try {
            await cam.ConnectAsync(ct);
            ctx.Logger.LogInformation("Camera reconnect {Result}.", cam.IsConnected ? "succeeded" : "did not take");
        } catch (Exception ex) {
            // Soft-fail: a failed reconnect shouldn't crash the run; we'll try
            // again on the next step once the throttle window elapses.
            ctx.Logger.LogWarning(ex, "Camera reconnect attempt failed");
        }
    }
}
