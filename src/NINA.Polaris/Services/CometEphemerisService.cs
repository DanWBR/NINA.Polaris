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

using System.Text.Json;
using System.Text.Json.Serialization;
using CosineKitty;

namespace NINA.Polaris.Services;

/// <summary>
/// Computes apparent RA/Dec and estimated magnitude for a small curated
/// list of periodic comets via Keplerian propagation of their osculating
/// orbital elements. Source elements are JPL Small-Body Database values
/// snapshotted into wwwroot/data/comets.json, accurate to a few arcmin
/// over a few months around perihelion, plenty for "is this comet worth
/// looking at tonight" planning.
///
/// Limitations (acceptable for the planning use case):
///   - Two-body Keplerian only; ignores Jupiter perturbations
///   - Comet magnitude is estimated via the standard cometary law
///     m = H + 5·log10(Δ) + n·2.5·log10(r), n varies wildly between
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
    // planning use case, full precession costs more code than it saves
    // for a 1-arcmin-target ephemeris.
    private const double ObliquityDeg = 23.4392911;
    // Gauss's constant (rad/day); μ_sun in heliocentric AU/day units is k².
    private const double GaussK = 0.01720209895;

    private readonly IWebHostEnvironment _env;
    private readonly ILogger<CometEphemerisService> _logger;
    private readonly string _overridePath;
    private readonly object _gate = new();
    private List<CometElements> _comets = new();

    /// <summary>Where the working set came from: "bundled" or "jpl".</summary>
    public string Source { get; private set; } = "bundled";

    /// <summary>When the downloaded set was fetched. Null while running on the
    /// bundled snapshot, which is what the UI needs to say "these elements are
    /// from the install, not from tonight".</summary>
    public DateTime? FetchedAtUtc { get; private set; }

    public CometEphemerisService(IWebHostEnvironment env, IConfiguration config,
                                 ILogger<CometEphemerisService> logger) {
        _env = env;
        _logger = logger;
        // A downloaded set cannot live beside the bundled one: wwwroot sits
        // under /opt/polaris owned by root, and a package update overwrites it.
        _overridePath = config.GetValue("Sky:CometElementsPath",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NINA.Polaris", "comets.json"))!;
        LoadComets();
    }

    public IReadOnlyList<CometElements> AllComets { get { lock (_gate) return _comets; } }

    /// <summary>Install a freshly downloaded set and persist it, so the next
    /// start uses it without another download.
    ///
    /// An empty set is refused. A refresh that returned nothing (rate limit, a
    /// changed response shape, a captive portal answering 200 with HTML) must
    /// never be allowed to leave the operator with fewer comets than the
    /// bundled file already gave them.</summary>
    public void ReplaceElements(IReadOnlyList<CometElements> fresh, string source, DateTime fetchedUtc) {
        if (fresh == null || fresh.Count == 0)
            throw new ArgumentException("Refusing to install an empty comet set.", nameof(fresh));

        var file = new CometsFile { Comets = fresh.ToList() };
        var json = JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(_overridePath)!);
        // Write then move: a crash mid-write must not leave a truncated file
        // that the next start refuses to parse.
        var tmp = _overridePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _overridePath, overwrite: true);
        File.SetLastWriteTimeUtc(_overridePath, fetchedUtc);

        lock (_gate) {
            _comets = file.Comets;
            Source = source;
            FetchedAtUtc = fetchedUtc;
        }
        _logger.LogInformation("Installed {Count} comet elements from {Source}", fresh.Count, source);
    }

    private void LoadComets() {
        // Downloaded set wins; the bundled snapshot is the floor so the SKY tab
        // is never empty on a host that has never seen the internet.
        if (TryLoadFrom(_overridePath, "jpl", File.Exists(_overridePath)
                ? File.GetLastWriteTimeUtc(_overridePath) : null)) return;

        var bundled = Path.Combine(_env.WebRootPath ?? "wwwroot", "data", "comets.json");
        if (!TryLoadFrom(bundled, "bundled", null))
            _logger.LogWarning("No comet elements found (tried {A} and {B}); "
                + "CometEphemerisService starts empty", _overridePath, bundled);
    }

    private bool TryLoadFrom(string path, string source, DateTime? fetchedUtc) {
        if (!File.Exists(path)) return false;
        try {
            var doc = JsonSerializer.Deserialize<CometsFile>(File.ReadAllText(path));
            var list = doc?.Comets;
            if (list == null || list.Count == 0) return false;
            lock (_gate) {
                _comets = list;
                Source = source;
                FetchedAtUtc = fetchedUtc;
            }
            _logger.LogInformation("Loaded {Count} comet elements from {Path} ({Source})",
                list.Count, path, source);
            return true;
        } catch (Exception ex) {
            // A corrupt override must fall through to the bundled file rather
            // than take the catalogue down with it.
            _logger.LogError(ex, "Failed to load comet elements at {Path}", path);
            return false;
        }
    }

    /// <summary>
    /// True anomaly and heliocentric distance at <paramref name="daysFromPerihelion"/>,
    /// picking the branch the eccentricity calls for.
    ///
    /// All three cases are Meeus, "Astronomical Algorithms" 2nd ed., ch. 30
    /// (elliptic), ch. 35 (parabolic, Barker's equation) and ch. 35's
    /// near-parabolic discussion (hyperbolic).
    ///
    /// Handling all three is not completeness for its own sake. The elliptic
    /// branch alone computes a = q / (1 - e), which is infinite at e = 1 and
    /// negative beyond it, so every long-period comet came out as NaN. In a
    /// live JPL set of comets within ±550 days of perihelion, 67 of 118 have
    /// e >= 0.98 — the bright, newly discovered ones people actually want to
    /// photograph are exactly the ones the elliptic-only code could not do.
    /// </summary>
    internal static (double nu, double r) SolveOrbit(double q, double e, double daysFromPerihelion) {
        const double NearParabolic = 1e-3;   // |e-1| below this: use Barker

        if (Math.Abs(e - 1.0) < NearParabolic) {
            // Barker's equation: an exact closed form, no iteration.
            var A = 1.5 * GaussK * daysFromPerihelion / Math.Sqrt(2.0 * q * q * q);
            var B = Math.Cbrt(A + Math.Sqrt(A * A + 1.0));
            var tanHalfNu = B - 1.0 / B;
            var nuP = 2.0 * Math.Atan(tanHalfNu);
            var rP = q * (1.0 + tanHalfNu * tanHalfNu);
            return (nuP, rP);
        }

        if (e > 1.0) {
            // Hyperbolic: a = q/(e-1) > 0, M = e·sinh(H) - H.
            var aH = q / (e - 1.0);
            var nH = GaussK * Math.Sqrt(1.0 / (aH * aH * aH));
            var MH = nH * daysFromPerihelion;
            // Seed from the asymptotic form; Newton then converges in a few
            // steps even far from perihelion.
            var H = Math.Asinh(MH / e);
            for (var i = 0; i < 60; i++) {
                var f = e * Math.Sinh(H) - H - MH;
                var fp = e * Math.Cosh(H) - 1.0;
                var dH = f / fp;
                H -= dH;
                if (Math.Abs(dH) < 1e-12) break;
            }
            var nuH = 2.0 * Math.Atan2(Math.Sqrt(e + 1.0) * Math.Tanh(H / 2.0),
                                       Math.Sqrt(e - 1.0));
            var rH = aH * (e * Math.Cosh(H) - 1.0);
            return (nuH, rH);
        }

        // Elliptic.
        var a = q / (1.0 - e);
        var n = GaussK * Math.Sqrt(1.0 / (a * a * a));
        var M = n * daysFromPerihelion;
        var E = M;
        for (var i = 0; i < 60; i++) {
            var f = E - e * Math.Sin(E) - M;
            var fp = 1.0 - e * Math.Cos(E);
            var dE = f / fp;
            E -= dE;
            if (Math.Abs(dE) < 1e-12) break;
        }
        var nuE = 2.0 * Math.Atan2(Math.Sqrt(1.0 + e) * Math.Sin(E / 2.0),
                                   Math.Sqrt(1.0 - e) * Math.Cos(E / 2.0));
        var rE = a * (1.0 - e * Math.Cos(E));
        return (nuE, rE);
    }

    /// <summary>
    /// Apparent geocentric equatorial position + estimated magnitude for
    /// the given comet at the given UTC instant.
    /// </summary>
    public CometPosition Compute(CometElements c, DateTime utc) {
        // 1) Resolve perihelion epoch to Julian Date (TT ≈ UTC for our
        //    precision needs, ~70 s offset is negligible at this level).
        var tperi = DateTime.SpecifyKind(DateTime.Parse(c.Tperi), DateTimeKind.Utc);
        var jdNow   = ToJulianDate(utc);
        var jdPeri  = ToJulianDate(tperi);

        // 2-4) True anomaly + heliocentric distance, by orbit class.
        var (nu, r) = SolveOrbit(c.Q, c.E, jdNow - jdPeri);

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