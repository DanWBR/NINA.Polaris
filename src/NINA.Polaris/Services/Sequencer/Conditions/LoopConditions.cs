using NINA.Polaris.Services.Sequencer.Instructions;

namespace NINA.Polaris.Services.Sequencer.Conditions;

/// <summary>Keep looping until UTC time-of-day. Past-time means "tomorrow".</summary>
public class LoopUntilTimeCondition : SequenceCondition {
    public override string Type => "LoopUntilTime";
    public string TimeOfDayUtc { get; set; } = "06:00";

    public override Task<bool> StillTrueAsync(SequenceContext ctx, CancellationToken ct) {
        if (!TimeSpan.TryParse(TimeOfDayUtc, out var tod)) return Task.FromResult(false);
        var now = DateTime.UtcNow;
        var target = now.Date + tod;
        if (target <= ctx.RunStartedAt) target = target.AddDays(1);
        return Task.FromResult(now < target);
    }
}

/// <summary>Keep looping while the target stays above <see cref="MinAltitudeDeg"/>.</summary>
public class LoopUntilAltitudeCondition : SequenceCondition {
    public override string Type => "LoopUntilAltitude";
    public double RaHours { get; set; }
    public double DecDeg { get; set; }
    public double MinAltitudeDeg { get; set; } = 30;

    public override Task<bool> StillTrueAsync(SequenceContext ctx, CancellationToken ct) {
        var (_, alt) = AltAzMath.RaDecToAltAz(RaHours, DecDeg,
            ctx.Profiles.Active.Latitude, ctx.Profiles.Active.Longitude, DateTime.UtcNow);
        return Task.FromResult(alt >= MinAltitudeDeg);
    }
}

/// <summary>
/// Loop for N total exposures (compares <see cref="SequenceContext.FramesCompleted"/>
/// against the iteration counter stored in <see cref="SequenceContext.Scratch"/>).
/// </summary>
public class LoopForNExposuresCondition : SequenceCondition {
    public override string Type => "LoopForNExposures";
    public int TargetCount { get; set; } = 10;

    public override Task<bool> StillTrueAsync(SequenceContext ctx, CancellationToken ct) {
        var key = $"LoopForN:{Id}:start";
        if (!ctx.Scratch.TryGetValue(key, out var startObj)) {
            ctx.Scratch[key] = ctx.FramesCompleted;
            startObj = ctx.FramesCompleted;
        }
        var start = (int)startObj;
        return Task.FromResult((ctx.FramesCompleted - start) < TargetCount);
    }
}

/// <summary>Loop for a fixed wall-clock duration starting from first evaluation.</summary>
public class LoopForDurationCondition : SequenceCondition {
    public override string Type => "LoopForDuration";
    public int Seconds { get; set; } = 3600;

    public override Task<bool> StillTrueAsync(SequenceContext ctx, CancellationToken ct) {
        var key = $"LoopDur:{Id}:start";
        if (!ctx.Scratch.TryGetValue(key, out var startObj)) {
            ctx.Scratch[key] = DateTime.UtcNow;
            startObj = DateTime.UtcNow;
        }
        var start = (DateTime)startObj;
        return Task.FromResult((DateTime.UtcNow - start).TotalSeconds < Seconds);
    }
}

/// <summary>Loop until the Moon drops below the horizon (or any configured altitude).</summary>
public class LoopUntilMoonSetsCondition : SequenceCondition {
    public override string Type => "LoopUntilMoonSets";
    public double MoonAltitudeDeg { get; set; } = 0;

    public override Task<bool> StillTrueAsync(SequenceContext ctx, CancellationToken ct) {
        var alt = SolarMath.MoonAltitudeDeg(DateTime.UtcNow,
            ctx.Profiles.Active.Latitude, ctx.Profiles.Active.Longitude);
        return Task.FromResult(alt > MoonAltitudeDeg);
    }
}

/// <summary>
/// Loop while every safety check holds. Today: cloud cover and wind speed
/// from the weather device. Returns true (= keep looping) only when the
/// weather is within bounds OR no weather device is connected.
/// </summary>
public class LoopWhileSafeCondition : SequenceCondition {
    public override string Type => "LoopWhileSafe";
    /// <summary>Max cloud cover percentage allowed; null = ignore.</summary>
    public double? MaxCloudCoverPercent { get; set; } = 50;
    /// <summary>Max wind speed in km/h allowed; null = ignore.</summary>
    public double? MaxWindSpeedKph { get; set; } = 40;

    public override Task<bool> StillTrueAsync(SequenceContext ctx, CancellationToken ct) {
        var w = ctx.Equipment.Weather;
        if (w == null) return Task.FromResult(true);
        if (MaxCloudCoverPercent.HasValue && w.CloudCover > MaxCloudCoverPercent.Value) {
            ctx.Logger.LogWarning("LoopWhileSafe: cloud cover {Cover}% > {Max}%", w.CloudCover, MaxCloudCoverPercent.Value);
            return Task.FromResult(false);
        }
        if (MaxWindSpeedKph.HasValue && w.WindSpeed > MaxWindSpeedKph.Value) {
            ctx.Logger.LogWarning("LoopWhileSafe: wind {Wind} km/h > {Max} km/h", w.WindSpeed, MaxWindSpeedKph.Value);
            return Task.FromResult(false);
        }
        return Task.FromResult(true);
    }
}
