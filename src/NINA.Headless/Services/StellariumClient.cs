using System.Globalization;
using System.Text.Json;

namespace NINA.Headless.Services;

/// <summary>
/// Thin HTTP client for the Stellarium Remote Control plugin.
/// Reference: <a href="https://stellarium.org/doc/head/remoteControlApi.html">
/// Stellarium Remote Control HTTP API</a>.
///
/// We use two endpoints:
///   GET /api/objects/info?format=json   →  currently-selected object
///   GET /api/main/view                  →  current Alt/Az view direction
///
/// The selected-object response includes <c>raJ2000</c> and <c>decJ2000</c>
/// (degrees) plus <c>name</c>, <c>type</c>, <c>vmag</c> when available, which
/// is exactly what our Sky Explorer expects to drop into <c>skyTarget</c>.
/// </summary>
public class StellariumClient {
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly ILogger<StellariumClient> _logger;

    public StellariumClient(ILogger<StellariumClient> logger) {
        _logger = logger;
    }

    public async Task<StellariumTarget?> GetSelectedObjectAsync(string host, int port, CancellationToken ct = default) {
        var url = $"http://{host}:{port}/api/objects/info?format=json";
        try {
            using var resp = await Http.GetAsync(url, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) {
                // Stellarium returns 404 when nothing is selected
                return null;
            }
            resp.EnsureSuccessStatusCode();
            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return ParseObject(doc.RootElement);
        } catch (HttpRequestException ex) {
            _logger.LogDebug(ex, "Stellarium GET failed for {Host}:{Port}", host, port);
            throw new InvalidOperationException(
                $"Couldn't reach Stellarium at {host}:{port}. Is the Remote Control plugin enabled and listening?");
        } catch (TaskCanceledException) {
            throw new TimeoutException($"Stellarium at {host}:{port} did not respond in 5s");
        }
    }

    public async Task<StellariumView?> GetViewAsync(string host, int port, CancellationToken ct = default) {
        var url = $"http://{host}:{port}/api/main/view";
        try {
            using var resp = await Http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;
            return new StellariumView {
                AltDeg = root.TryGetProperty("alt", out var a) ? ParseRadiansToDeg(a) : 0,
                AzDeg = root.TryGetProperty("az", out var az) ? ParseRadiansToDeg(az) : 0,
                Fov = root.TryGetProperty("fov", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetDouble() : 0
            };
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Stellarium view query failed");
            return null;
        }
    }

    private static StellariumTarget? ParseObject(JsonElement root) {
        // raJ2000 / decJ2000 are in *radians* in the Stellarium API
        // (despite the field name being just "raJ2000"). Convert to hours/deg.
        if (!root.TryGetProperty("raJ2000", out var raEl) ||
            !root.TryGetProperty("decJ2000", out var decEl)) {
            return null;
        }

        double raRad = raEl.GetDouble();
        double decRad = decEl.GetDouble();

        double raDeg = raRad * 180.0 / Math.PI;
        if (raDeg < 0) raDeg += 360;
        double decDeg = decRad * 180.0 / Math.PI;

        return new StellariumTarget {
            Name = TryStr(root, "name") ?? TryStr(root, "localized-name") ?? "Stellarium selection",
            Type = TryStr(root, "type"),
            Magnitude = TryDouble(root, "vmag"),
            RaHours = raDeg / 15.0,
            DecDeg = decDeg,
            AltDeg = TryAlt(root),
            AzDeg = TryAz(root),
            Designations = TryStr(root, "designations")
        };
    }

    private static double? TryAlt(JsonElement root) {
        if (root.TryGetProperty("altitude", out var v) && v.ValueKind == JsonValueKind.Number)
            return v.GetDouble();
        return null;
    }

    private static double? TryAz(JsonElement root) {
        if (root.TryGetProperty("azimuth", out var v) && v.ValueKind == JsonValueKind.Number)
            return v.GetDouble();
        return null;
    }

    private static string? TryStr(JsonElement obj, string prop) {
        if (!obj.TryGetProperty(prop, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    private static double? TryDouble(JsonElement obj, string prop) {
        if (!obj.TryGetProperty(prop, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number) return el.GetDouble();
        if (el.ValueKind == JsonValueKind.String &&
            double.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) return d;
        return null;
    }

    private static double ParseRadiansToDeg(JsonElement el) {
        if (el.ValueKind == JsonValueKind.Number) return el.GetDouble() * 180.0 / Math.PI;
        return 0;
    }
}

public class StellariumTarget {
    public string Name { get; set; } = "";
    public string? Type { get; set; }
    public double? Magnitude { get; set; }
    public double RaHours { get; set; }
    public double DecDeg { get; set; }
    public double? AltDeg { get; set; }
    public double? AzDeg { get; set; }
    public string? Designations { get; set; }
}

public class StellariumView {
    public double AltDeg { get; set; }
    public double AzDeg { get; set; }
    public double Fov { get; set; }
}
