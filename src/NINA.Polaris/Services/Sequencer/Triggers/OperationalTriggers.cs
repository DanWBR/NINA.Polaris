namespace NINA.Polaris.Services.Sequencer.Triggers;

/// <summary>
/// Fire the existing MeridianFlipService routine when the mount approaches
/// the meridian. The service handles pausing guiding, slewing, plate-solve
/// recenter, and resuming guiding.
/// </summary>
public class MeridianFlipTrigger : SequenceTrigger {
    public override string Type => "MeridianFlip";

    /// <summary>Target right ascension in decimal hours (J2000). The trigger needs
    /// this both to time the flip and to re-center after.</summary>
    public double RaHours { get; set; }
    public double DecDeg { get; set; }

    public override Task<bool> ShouldFireAsync(SequenceContext ctx, CancellationToken ct) {
        return Task.FromResult(ctx.MeridianFlip.ShouldFlipNow(RaHours));
    }

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        await ctx.MeridianFlip.ExecuteFlipAsync(RaHours, DecDeg, ct);
    }
}

/// <summary>Dither via PHD2 every N frames captured.</summary>
public class DitherAfterNExposuresTrigger : SequenceTrigger {
    public override string Type => "DitherAfterNExposures";
    public int EveryNFrames { get; set; } = 3;
    public double Pixels { get; set; } = 5.0;
    public bool RaOnly { get; set; } = false;
    public double SettlePixels { get; set; } = 1.5;
    public int SettleTimeSeconds { get; set; } = 10;
    public int SettleTimeoutSeconds { get; set; } = 40;

    public override Task<bool> ShouldFireAsync(SequenceContext ctx, CancellationToken ct) {
        if (EveryNFrames <= 0 || !ctx.PHD2.IsConnected || !ctx.PHD2.IsGuiding) return Task.FromResult(false);
        var key = $"Dither:{Id}:last";
        var last = ctx.Scratch.TryGetValue(key, out var v) ? (int)v : 0;
        if (ctx.FramesCompleted - last >= EveryNFrames) {
            ctx.Scratch[key] = ctx.FramesCompleted;
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        await ctx.PHD2.DitherAsync(Pixels, RaOnly, SettlePixels, SettleTimeSeconds, SettleTimeoutSeconds);
    }
}

/// <summary>
/// Periodically plate-solve the current pointing; if the offset from the
/// configured target exceeds <see cref="ToleranceArcsec"/>, kick off a
/// Slew &amp; Center to correct it.
/// </summary>
public class CenterAfterDriftTrigger : SequenceTrigger {
    public override string Type => "CenterAfterDrift";
    public double RaHours { get; set; }
    public double DecDeg { get; set; }
    public int CheckEveryNFrames { get; set; } = 10;
    public double ToleranceArcsec { get; set; } = 60;

    public override Task<bool> ShouldFireAsync(SequenceContext ctx, CancellationToken ct) {
        if (CheckEveryNFrames <= 0) return Task.FromResult(false);
        var key = $"DriftCheck:{Id}:last";
        var last = ctx.Scratch.TryGetValue(key, out var v) ? (int)v : 0;
        if (ctx.FramesCompleted - last >= CheckEveryNFrames) {
            ctx.Scratch[key] = ctx.FramesCompleted;
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        var job = ctx.SlewCenter.StartJob(RaHours, DecDeg, ToleranceArcsec);
        while (true) {
            ct.ThrowIfCancellationRequested();
            var status = ctx.SlewCenter.GetJob(job.Id);
            if (status == null) throw new InvalidOperationException("Slew & Center job vanished");
            if (status.State == SlewCenterState.Centered) return;
            if (status.State == SlewCenterState.Failed) {
                ctx.Logger.LogWarning("CenterAfterDrift: re-center failed: {Err}", status.Error);
                return; // soft fail, don't abort the sequence on a flaky solve
            }
            if (status.State == SlewCenterState.Cancelled) return;
            await Task.Delay(500, ct);
        }
    }
}

/// <summary>
/// Watchdog that flips <see cref="SequenceContext.AbortRequested"/> when the
/// weather goes out of bounds or critical equipment disconnects mid-run.
/// Containers honour AbortRequested between every step.
/// </summary>
public class SafetyTrigger : SequenceTrigger {
    public override string Type => "Safety";
    public double? MaxCloudCoverPercent { get; set; } = 70;
    public double? MaxWindSpeedKph { get; set; } = 50;
    /// <summary>If true, abort when the mount reports disconnected.</summary>
    public bool RequireMountConnected { get; set; } = true;

    public override Task<bool> ShouldFireAsync(SequenceContext ctx, CancellationToken ct) {
        // We "fire" once when conditions go bad; the actual ExecuteAsync sets the abort.
        var reason = EvaluateUnsafe(ctx);
        if (reason != null) ctx.Scratch[$"Safety:{Id}:reason"] = reason;
        return Task.FromResult(reason != null);
    }

    public override Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        if (ctx.Scratch.TryGetValue($"Safety:{Id}:reason", out var r) && r is string reason) {
            ctx.AbortRequested = true;
            ctx.AbortReason = reason;
            ctx.Logger.LogError("Safety trigger fired: {Reason}", reason);
        }
        return Task.CompletedTask;
    }

    private string? EvaluateUnsafe(SequenceContext ctx) {
        if (RequireMountConnected && (ctx.Equipment.Telescope == null || !ctx.Equipment.Telescope.IsConnected))
            return "Mount disconnected";
        var w = ctx.Equipment.Weather;
        if (w == null) return null;
        if (MaxCloudCoverPercent.HasValue && w.CloudCover > MaxCloudCoverPercent.Value)
            return $"Cloud cover {w.CloudCover}% > {MaxCloudCoverPercent.Value}%";
        if (MaxWindSpeedKph.HasValue && w.WindSpeed > MaxWindSpeedKph.Value)
            return $"Wind {w.WindSpeed} km/h > {MaxWindSpeedKph.Value} km/h";
        return null;
    }
}
