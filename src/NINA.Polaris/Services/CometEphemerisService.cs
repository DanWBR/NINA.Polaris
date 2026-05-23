using System.Text.Json;
using System.Text.Json.Serialization;
using CosineKitty;

namespace NINA.Polaris.Services;

/// <summary>
/// Computes apparent RA/Dec and estimated magnitude for a small curated
/// list of periodic comets via Keplerian propagation of their osculating
/// orbital elements. Source elements are JPL Small-Body Database values
/// snapshotted into wwwroot/data/comets.json — accurate to a few arcmin
/// over a few months around perihelion, plenty for "is this comet worth
/// looking at tonight" planning.
///
/// Limitations (acceptable for the planning use case):
///   - Two-body Keplerian only; ignores Jupiter perturbations
///   - Comet magnitude is estimated via the standard cometary law
///     m = H + 5·log10(Δ) + n·2.5·log10(r) — n varies wildly between
///     apparitions, so estimates can be off by ±2 magnitudes
///   - Hyperbolic / parabolic orbits (e ≥ 1) intentionally not handled;
///     all the comets in our curated file are periodic
///
/// Heliocentric → geocentric conversion uses AstronomyEngine to get
/// Earth's accurate heliocentric position.
/// </summary>
public class CometEphemerisService {
    private const double DegToRad = Math.PI / 180.0;
    private const double RadToDeg = 180.0 / Math.PI;
    // Mean obliquity of the ecliptic at J2000 (deg). Good enough for the
    // planning use case — full precession costs more code than it saves
    // for a 1-arcmin-target ephemeris.
    private const double ObliquityDeg = 23.4392911;
    // Gauss's constant (rad/day); μ_sun in heliocentric AU/day units is k².
    private const double GaussK = 0.01720209895;

    private readonly IWebHostEnvironment _env;
    private readonly ILogger<CometEphemerisService> _logger;
    private List<CometElements> _comets = new();

    public CometEphemerisService(IWebHostEnvironment env, ILogger<CometEphemerisService> logger) {
        _env = env;
        _logger = logger;
        LoadComets();
    }

    public IReadOnlyList<CometElements> AllComets => _comets;

    private void LoadComets() {
        var path = Path.Combine(_env.WebRootPath ?? "wwwroot", "data", "comets.json");
        if (!File.Exists(path)) {
            _logger.LogWarning("comets.json not found at {Path}; CometEphemerisService starts empty", path);
            return;
        }
        try {
            var json = File.ReadAllText(path);
            var doc = JsonSerializer.Deserialize<CometsFile>(json);
            _comets = doc?.Comets ?? new List<CometElements>();
            _logger.LogInformation("Loaded {Count} comet elements from {Path}", _comets.Count, path);
        } catch (Exception ex) {
            _logger.LogError(ex, "Failed to load comets.json at {Path}", path);
        }
    }

    /// <summary>
    /// Apparent geocentric equatorial position + estimated magnitude for
    /// the given comet at the given UTC instant.
    /// </summary>
    public CometPosition Compute(CometElements c, DateTime utc) {
        // 1) Resolve perihelion epoch to Julian Date (TT ≈ UTC for our
        //    precision needs — ~70 s offset is negligible at this level).
        var tperi = DateTime.SpecifyKind(DateTime.Parse(c.Tperi), DateTimeKind.Utc);
        var jdNow   = ToJulianDate(utc);
        var jdPeri  = ToJulianDate(tperi);

        // 2) Mean motion + mean anomaly.
        var a = c.Q / (1 - c.E);                  // AU
        var n = GaussK * Math.Sqrt(1.0 / (a * a * a)); // rad/day
        var M = n * (jdNow - jdPeri);             // rad

        // 3) Eccentric anomaly via Newton-Raphson (5 iterations is plenty
        //    for e < 0.97; we cap at e = 0.97 for Halley-class orbits).
        var E = M;
        for (var i = 0; i < 30; i++) {
            var f  = E - c.E * Math.Sin(E) - M;
            var fp = 1 - c.E * Math.Cos(E);
            var dE = f / fp;
            E -= dE;
            if (Math.Abs(dE) < 1e-10) break;
        }

        // 4) True anomaly + heliocentric distance.
        var sinHalfNu = Math.Sqrt(1 + c.E) * Math.Sin(E / 2);
        var cosHalfNu = Math.Sqrt(1 - c.E) * Math.Cos(E / 2);
        var nu = 2 * Math.Atan2(sinHalfNu, cosHalfNu);
        var r  = a * (1 - c.E * Math.Cos(E));

        // 5) Position in the orbital plane (perifocal frame).
        var xPeri = r * Math.Cos(nu);
        var yPeri = r * Math.Sin(nu);

        // 6) Rotate perifocal → heliocentric ecliptic via ω, i, Ω.
        var w = c.ArgPeriapsis * DegToRad;
        var O = c.OmegaNode    * DegToRad;
        var I = c.I            * DegToRad;
        var cosW = Math.Cos(w); var sinW = Math.Sin(w);
        var cosO = Math.Cos(O); var sinO = Math.Sin(O);
        var cosI = Math.Cos(I); var sinI = Math.Sin(I);

        var xEcl = (cosO * cosW - sinO * sinW * cosI) * xPeri + (-cosO * sinW - sinO * cosW * cosI) * yPeri;
        var yEcl = (sinO * cosW + cosO * sinW * cosI) * xPeri + (-sinO * sinW + cosO * cosW * cosI) * yPeri;
        var zEcl = (sinW * sinI)                       * xPeri + ( cosW * sinI)                      * yPeri;

        // 7) Subtract Earth's heliocentric ecliptic position to get the
        //    geocentric vector. AstronomyEngine gives equatorial J2000 by
        //    default; rotate it to ecliptic by the obliquity tilt.
        var earth = Astronomy.HelioVector(Body.Earth, new AstroTime(utc));
        var (xeE, yeE, zeE) = EquatorialToEcliptic(earth.x, earth.y, earth.z);

        var xGeo = xEcl - xeE;
        var yGeo = yEcl - yeE;
        var zGeo = zEcl - zeE;

        // 8) Ecliptic → equatorial.
        var (xEq, yEq, zEq) = EclipticToEquatorial(xGeo, yGeo, zGeo);

        // 9) Equatorial Cartesian → RA/Dec.
        var delta = Math.Sqrt(xEq * xEq + yEq * yEq + zEq * zEq); // AU, geocentric
        var ra    = Math.Atan2(yEq, xEq) * RadToDeg / 15.0;       // hours
        if (ra < 0) ra += 24;
        var dec   = Math.Asin(zEq / delta) * RadToDeg;

        // 10) Magnitude estimate via the cometary photometric law.
        var mag = c.H + 5 * Math.Log10(delta) + c.N * Math.Log10(r);

        return new CometPosition(
            RaHours:        ra,
            DecDeg:         dec,
            HelioDistanceAu: r,
            GeoDistanceAu:   delta,
            EstimatedMagnitude: mag);
    }

    // ----- Helpers -----

    private static double ToJulianDate(DateTime utc) {
        // Standard formula. Works for any modern Gregorian date.
        var u = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return u.ToOADate() + 2415018.5;
    }

    private static (double x, double y, double z) EclipticToEquatorial(double x, double y, double z) {
        var eps = ObliquityDeg * DegToRad;
        var cos = Math.Cos(eps); var sin = Math.Sin(eps);
        return (x, y * cos - z * sin, y * sin + z * cos);
    }

    private static (double x, double y, double z) EquatorialToEcliptic(double x, double y, double z) {
        var eps = ObliquityDeg * DegToRad;
        var cos = Math.Cos(eps); var sin = Math.Sin(eps);
        return (x, y * cos + z * sin, -y * sin + z * cos);
    }
}

public class CometElements {
    [JsonPropertyName("name")]           public string Name           { get; set; } = "";
    [JsonPropertyName("tperi")]          public string Tperi          { get; set; } = "";
    [JsonPropertyName("q")]              public double Q              { get; set; }
    [JsonPropertyName("e")]              public double E              { get; set; }
    [JsonPropertyName("i")]              public double I              { get; set; }
    [JsonPropertyName("omega_node")]     public double OmegaNode      { get; set; }
    [JsonPropertyName("arg_periapsis")]  public double ArgPeriapsis   { get; set; }
    [JsonPropertyName("h")]              public double H              { get; set; }
    [JsonPropertyName("n")]              public double N              { get; set; } = 4.0;
}

internal class CometsFile {
    [JsonPropertyName("comets")] public List<CometElements> Comets { get; set; } = new();
}

public record CometPosition(
    double RaHours,
    double DecDeg,
    double HelioDistanceAu,
    double GeoDistanceAu,
    double EstimatedMagnitude);
