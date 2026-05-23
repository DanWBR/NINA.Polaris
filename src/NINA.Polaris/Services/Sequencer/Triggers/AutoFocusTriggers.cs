namespace NINA.Polaris.Services.Sequencer.Triggers;

/// <summary>
/// Fire auto-focus when the focuser's temperature drifts by more than
/// <see cref="DeltaC"/> degrees from the last reading we focused at.
/// </summary>
public class AutoFocusOnTempChangeTrigger : SequenceTrigger {
    public override string Type => "AutoFocusOnTempChange";
    public double DeltaC { get; set; } = 1.0;

    public override Task<bool> ShouldFireAsync(SequenceContext ctx, CancellationToken ct) {
        var f = ctx.Equipment.Focuser;
        if (f == null) return Task.FromResult(false);
        var key = $"AFTemp:{Id}:last";
        var current = f.Temperature;
        if (!ctx.Scratch.TryGetValue(key, out var lastObj)) {
            ctx.Scratch[key] = current;
            return Task.FromResult(false);
        }
        var last = (double)lastObj;
        if (Math.Abs(current - last) >= DeltaC) {
            ctx.Scratch[key] = current;
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public override Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) => RunAutoFocusAsync(ctx, ct);

    internal static async Task RunAutoFocusAsync(SequenceContext ctx, CancellationToken ct) {
        ctx.AutoFocus.Start(new AutoFocusRequest());
        while (ctx.AutoFocus.State == AutoFocusState.Running) {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(500, ct);
        }
    }
}

/// <summary>
/// Fire auto-focus when the rolling average HFR rises above
/// <see cref="ThresholdMultiplier"/> × baseline HFR. Baseline is captured
/// after the first AF run of the sequence.
/// </summary>
public class AutoFocusOnHfrIncreaseTrigger : SequenceTrigger {
    public override string Type => "AutoFocusOnHfrIncrease";
    public double ThresholdMultiplier { get; set; } = 1.2;

    public override Task<bool> ShouldFireAsync(SequenceContext ctx, CancellationToken ct) {
        // Baseline = first measured HFR after the most recent AF. Current =
        // HFR of the last frame TakeExposureInstruction recorded into Scratch.
        var baseHfr = ctx.AutoFocus.LastResult?.FinalMeasuredHfr ?? 0;
        var nowHfr = ctx.Scratch.TryGetValue("Frame:LastHfr", out var v) ? (double)v : 0;
        if (baseHfr <= 0 || nowHfr <= 0) return Task.FromResult(false);
        var ratio = nowHfr / baseHfr;
        if (ratio >= ThresholdMultiplier) {
            ctx.Logger.LogInformation("HFR drift {Ratio:0.00}× baseline (baseline={Base:0.00}, now={Now:0.00}) → auto-focus",
                ratio, baseHfr, nowHfr);
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public override Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) =>
        AutoFocusOnTempChangeTrigger.RunAutoFocusAsync(ctx, ct);
}

/// <summary>Fire auto-focus every <see cref="Minutes"/> minutes of wall-clock time.</summary>
public class AutoFocusEveryNMinutesTrigger : SequenceTrigger {
    public override string Type => "AutoFocusEveryNMinutes";
    public int Minutes { get; set; } = 60;

    public override Task<bool> ShouldFireAsync(SequenceContext ctx, CancellationToken ct) {
        var key = $"AFTime:{Id}:last";
        if (!ctx.Scratch.TryGetValue(key, out var lastObj)) {
            ctx.Scratch[key] = ctx.RunStartedAt;
            return Task.FromResult(false);
        }
        var last = (DateTime)lastObj;
        if ((DateTime.UtcNow - last).TotalMinutes >= Minutes) {
            ctx.Scratch[key] = DateTime.UtcNow;
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public override Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) =>
        AutoFocusOnTempChangeTrigger.RunAutoFocusAsync(ctx, ct);
}

/// <summary>Fire auto-focus whenever the active filter changes between frames.</summary>
public class AutoFocusOnFilterChangeTrigger : SequenceTrigger {
    public override string Type => "AutoFocusOnFilterChange";

    public override Task<bool> ShouldFireAsync(SequenceContext ctx, CancellationToken ct) {
        var fw = ctx.Equipment.FilterWheel;
        if (fw == null) return Task.FromResult(false);
        var key = $"AFFilter:{Id}:last";
        var cur = fw.Position;
        if (!ctx.Scratch.TryGetValue(key, out var lastObj)) {
            ctx.Scratch[key] = cur;
            return Task.FromResult(false);
        }
        var changed = (int)lastObj != cur;
        if (changed) ctx.Scratch[key] = cur;
        return Task.FromResult(changed);
    }

    public override Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) =>
        AutoFocusOnTempChangeTrigger.RunAutoFocusAsync(ctx, ct);
}
