namespace NINA.Polaris.Services.Sequencer.Instructions;

public class OpenShutterInstruction : SequenceInstruction {
    public override string Type => "OpenShutter";
    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        var d = ctx.Equipment.Dome ?? throw new InvalidOperationException("No dome connected");
        await d.OpenShutterAsync(ct);
    }
}

public class CloseShutterInstruction : SequenceInstruction {
    public override string Type => "CloseShutter";
    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        var d = ctx.Equipment.Dome ?? throw new InvalidOperationException("No dome connected");
        await d.CloseShutterAsync(ct);
    }
}

public class ParkDomeInstruction : SequenceInstruction {
    public override string Type => "ParkDome";
    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        var d = ctx.Equipment.Dome ?? throw new InvalidOperationException("No dome connected");
        await d.ParkAsync(ct);
    }
}

public class SlewDomeToAzimuthInstruction : SequenceInstruction {
    public override string Type => "SlewDomeToAzimuth";
    public double Azimuth { get; set; }
    public override IReadOnlyList<string> Validate() =>
        (Azimuth < 0 || Azimuth >= 360) ? new[] { $"Azimuth out of range: {Azimuth}" } : Array.Empty<string>();
    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        var d = ctx.Equipment.Dome ?? throw new InvalidOperationException("No dome connected");
        await d.SlewToAzimuthAsync(Azimuth, ct);
    }
}

/// <summary>
/// Sync the dome aperture to the mount's current azimuth (one-shot — for
/// continuous slaving, leave it to the dome's native slave mode if available).
/// Computes az from the mount's current RA/Dec via a simple altaz transform.
/// </summary>
public class SyncDomeToScopeInstruction : SequenceInstruction {
    public override string Type => "SyncDomeToScope";

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        var mount = ctx.Equipment.Telescope ?? throw new InvalidOperationException("No telescope connected");
        var dome  = ctx.Equipment.Dome      ?? throw new InvalidOperationException("No dome connected");

        var lat = ctx.Profiles.Active.Latitude;
        var lon = ctx.Profiles.Active.Longitude;
        var (az, _) = AltAzMath.RaDecToAltAz(mount.RightAscension, mount.Declination, lat, lon, DateTime.UtcNow);
        await dome.SlewToAzimuthAsync(az, ct);
    }
}

internal static class AltAzMath {
    /// <summary>
    /// Compact RA/Dec → Alt/Az transform. Returns degrees. Accurate to ~0.1°
    /// — fine for slaving a dome aperture; not a substitute for plate solving.
    /// </summary>
    public static (double azDeg, double altDeg) RaDecToAltAz(double raHours, double decDeg, double latDeg, double lonDeg, DateTime utc) {
        // Local sidereal time (Meeus 12.4 simplified)
        var jd = utc.ToOADate() + 2415018.5;
        var t = (jd - 2451545.0) / 36525.0;
        var gmst = 280.46061837 + 360.98564736629 * (jd - 2451545.0)
                 + 0.000387933 * t * t - t * t * t / 38710000;
        var lst = (gmst + lonDeg) % 360;
        if (lst < 0) lst += 360;

        var ha = lst - raHours * 15;
        if (ha < -180) ha += 360;
        if (ha > 180) ha -= 360;

        var haRad = ha * Math.PI / 180;
        var decRad = decDeg * Math.PI / 180;
        var latRad = latDeg * Math.PI / 180;

        var sinAlt = Math.Sin(decRad) * Math.Sin(latRad) + Math.Cos(decRad) * Math.Cos(latRad) * Math.Cos(haRad);
        var altRad = Math.Asin(sinAlt);
        var cosAz = (Math.Sin(decRad) - Math.Sin(altRad) * Math.Sin(latRad))
                  / (Math.Cos(altRad) * Math.Cos(latRad));
        cosAz = Math.Clamp(cosAz, -1, 1);
        var azRad = Math.Acos(cosAz);
        var az = azRad * 180 / Math.PI;
        if (Math.Sin(haRad) > 0) az = 360 - az;
        return (az, altRad * 180 / Math.PI);
    }
}
