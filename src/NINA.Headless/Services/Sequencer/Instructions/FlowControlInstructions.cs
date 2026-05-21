namespace NINA.Headless.Services.Sequencer.Instructions;

/// <summary>Sleep for a fixed duration.</summary>
public class WaitForTimeInstruction : SequenceInstruction {
    public override string Type => "WaitForTime";
    public int Seconds { get; set; } = 60;
    public override Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) => Task.Delay(TimeSpan.FromSeconds(Seconds), ct);
}

/// <summary>Block until UTC clock hits the configured wall-clock time (HH:mm[:ss] today, next day if already past).</summary>
public class WaitUntilTimeInstruction : SequenceInstruction {
    public override string Type => "WaitUntilTime";
    /// <summary>UTC time of day, e.g. "21:30" or "21:30:00".</summary>
    public string TimeOfDayUtc { get; set; } = "21:00";

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        if (!TimeSpan.TryParse(TimeOfDayUtc, out var tod))
            throw new InvalidOperationException("Bad TimeOfDayUtc: " + TimeOfDayUtc);
        var now = DateTime.UtcNow;
        var target = now.Date + tod;
        if (target <= now) target = target.AddDays(1);
        var wait = target - now;
        ctx.Logger.LogInformation("Waiting {Wait} until UTC {Target}", wait, target);
        await Task.Delay(wait, ct);
    }
}

/// <summary>
/// Block until the configured target's altitude rises above
/// <see cref="MinAltitudeDeg"/>. Uses the active profile's lat/lon.
/// </summary>
public class WaitUntilAltitudeInstruction : SequenceInstruction {
    public override string Type => "WaitUntilAltitude";
    public double RaHours { get; set; }
    public double DecDeg { get; set; }
    /// <summary>Minimum altitude in degrees the target must clear.</summary>
    public double MinAltitudeDeg { get; set; } = 20;
    /// <summary>Hard cap so we don't block forever — default 6 hours.</summary>
    public int TimeoutSeconds { get; set; } = 6 * 3600;

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        var lat = ctx.Profiles.Active.Latitude;
        var lon = ctx.Profiles.Active.Longitude;
        var deadline = DateTime.UtcNow.AddSeconds(TimeoutSeconds);

        while (DateTime.UtcNow < deadline) {
            ct.ThrowIfCancellationRequested();
            var (_, alt) = AltAzMath.RaDecToAltAz(RaHours, DecDeg, lat, lon, DateTime.UtcNow);
            if (alt >= MinAltitudeDeg) {
                ctx.Logger.LogInformation("Target at alt={Alt:0.0}° (≥ {Min}°)", alt, MinAltitudeDeg);
                return;
            }
            await Task.Delay(30_000, ct);
        }
        throw new TimeoutException($"Target didn't reach {MinAltitudeDeg}° within {TimeoutSeconds}s");
    }
}

/// <summary>Block until the Sun is below the configured altitude (negative = below horizon; -12 = nautical twilight; -18 = astronomical).</summary>
public class WaitForSunBelowHorizonInstruction : SequenceInstruction {
    public override string Type => "WaitForSunBelowHorizon";
    public double SunAltitudeDeg { get; set; } = -12; // nautical twilight
    public int TimeoutSeconds { get; set; } = 6 * 3600;

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        var lat = ctx.Profiles.Active.Latitude;
        var lon = ctx.Profiles.Active.Longitude;
        var deadline = DateTime.UtcNow.AddSeconds(TimeoutSeconds);
        while (DateTime.UtcNow < deadline) {
            ct.ThrowIfCancellationRequested();
            var sunAlt = SolarMath.SunAltitudeDeg(DateTime.UtcNow, lat, lon);
            if (sunAlt <= SunAltitudeDeg) {
                ctx.Logger.LogInformation("Sun alt = {Alt:0.0}° (≤ {Tgt}°), continuing", sunAlt, SunAltitudeDeg);
                return;
            }
            await Task.Delay(60_000, ct);
        }
        throw new TimeoutException($"Sun didn't drop to {SunAltitudeDeg}° within {TimeoutSeconds}s");
    }
}

/// <summary>Block until the Moon either rises above a target alt (Above) or drops below it (Below).</summary>
public class WaitForMoonInstruction : SequenceInstruction {
    public override string Type => "WaitForMoon";
    /// <summary>Compare mode: "Above" (wait until alt ≥ AltitudeDeg) or "Below".</summary>
    public string Mode { get; set; } = "Below";
    public double AltitudeDeg { get; set; } = 0;
    public int TimeoutSeconds { get; set; } = 6 * 3600;

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        var lat = ctx.Profiles.Active.Latitude;
        var lon = ctx.Profiles.Active.Longitude;
        var deadline = DateTime.UtcNow.AddSeconds(TimeoutSeconds);
        while (DateTime.UtcNow < deadline) {
            ct.ThrowIfCancellationRequested();
            var moonAlt = SolarMath.MoonAltitudeDeg(DateTime.UtcNow, lat, lon);
            var hit = Mode.Equals("Above", StringComparison.OrdinalIgnoreCase)
                ? moonAlt >= AltitudeDeg
                : moonAlt <= AltitudeDeg;
            if (hit) {
                ctx.Logger.LogInformation("Moon alt = {Alt:0.0}° hit ({Mode} {Tgt}°)", moonAlt, Mode, AltitudeDeg);
                return;
            }
            await Task.Delay(120_000, ct);
        }
        throw new TimeoutException($"Moon did not satisfy {Mode} {AltitudeDeg}° within {TimeoutSeconds}s");
    }
}

internal static class SolarMath {
    /// <summary>Approximate solar altitude (Meeus low-precision; ±1' is fine for scheduling).</summary>
    public static double SunAltitudeDeg(DateTime utc, double latDeg, double lonDeg) {
        var jd = utc.ToOADate() + 2415018.5;
        var n = jd - 2451545.0;
        var L = (280.460 + 0.9856474 * n) % 360; if (L < 0) L += 360;
        var g = ((357.528 + 0.9856003 * n) % 360) * Math.PI / 180;
        var lambda = (L + 1.915 * Math.Sin(g) + 0.020 * Math.Sin(2 * g)) * Math.PI / 180;
        var eps = 23.439 * Math.PI / 180;
        var ra = Math.Atan2(Math.Cos(eps) * Math.Sin(lambda), Math.Cos(lambda)); // radians
        var dec = Math.Asin(Math.Sin(eps) * Math.Sin(lambda));
        var raHours = (ra * 180 / Math.PI) / 15;
        if (raHours < 0) raHours += 24;
        var decDeg = dec * 180 / Math.PI;
        var (_, alt) = AltAzMath.RaDecToAltAz(raHours, decDeg, latDeg, lonDeg, utc);
        return alt;
    }

    /// <summary>Very-low-precision lunar altitude — adequate for "is the Moon up tonight" branching.</summary>
    public static double MoonAltitudeDeg(DateTime utc, double latDeg, double lonDeg) {
        var jd = utc.ToOADate() + 2415018.5;
        var d = jd - 2451545.0;
        var Llong = (218.316 + 13.176396 * d) * Math.PI / 180;
        var M = (134.963 + 13.064993 * d) * Math.PI / 180;
        var F = (93.272 + 13.229350 * d) * Math.PI / 180;
        var lon = Llong + 6.289 * Math.PI / 180 * Math.Sin(M);
        var lat = 5.128 * Math.PI / 180 * Math.Sin(F);
        var eps = 23.439 * Math.PI / 180;
        var ra = Math.Atan2(Math.Sin(lon) * Math.Cos(eps) - Math.Tan(lat) * Math.Sin(eps), Math.Cos(lon));
        var dec = Math.Asin(Math.Sin(lat) * Math.Cos(eps) + Math.Cos(lat) * Math.Sin(eps) * Math.Sin(lon));
        var raHours = (ra * 180 / Math.PI) / 15;
        if (raHours < 0) raHours += 24;
        var (_, alt) = AltAzMath.RaDecToAltAz(raHours, dec * 180 / Math.PI, latDeg, lonDeg, utc);
        return alt;
    }
}
