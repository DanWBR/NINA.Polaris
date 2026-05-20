using System.Globalization;

namespace NINA.Headless.Services.Alpaca;

/// <summary>
/// Convenience wrapper for the Alpaca/ASCOM Telescope v3 interface — covers
/// the actions the rest of the app actually uses (connection, current
/// pointing, tracking, slew, park).
/// </summary>
public class AlpacaTelescope {
    private readonly AlpacaClient _client;

    public AlpacaTelescope(string host, int port, int deviceNumber = 0) {
        _client = new AlpacaClient(host, port, "telescope", deviceNumber);
    }

    public Task<bool> GetConnectedAsync(CancellationToken ct = default) =>
        SafeBool(_client.GetAsync<bool>("connected", ct));
    public Task SetConnectedAsync(bool v, CancellationToken ct = default) =>
        _client.PutAsync("connected", new() { ["Connected"] = v ? "true" : "false" }, ct);

    public Task<string?> GetNameAsync(CancellationToken ct = default) =>
        _client.GetAsync<string>("name", ct);

    // --- Pointing ---
    public Task<double> GetRightAscensionAsync(CancellationToken ct = default) =>
        SafeDouble(_client.GetAsync<double>("rightascension", ct));
    public Task<double> GetDeclinationAsync(CancellationToken ct = default) =>
        SafeDouble(_client.GetAsync<double>("declination", ct));
    public Task<double> GetAltitudeAsync(CancellationToken ct = default) =>
        SafeDouble(_client.GetAsync<double>("altitude", ct));
    public Task<double> GetAzimuthAsync(CancellationToken ct = default) =>
        SafeDouble(_client.GetAsync<double>("azimuth", ct));
    public Task<bool> GetSlewingAsync(CancellationToken ct = default) =>
        SafeBool(_client.GetAsync<bool>("slewing", ct));
    public Task<bool> GetAtParkAsync(CancellationToken ct = default) =>
        SafeBool(_client.GetAsync<bool>("atpark", ct));
    public Task<bool> GetTrackingAsync(CancellationToken ct = default) =>
        SafeBool(_client.GetAsync<bool>("tracking", ct));
    public Task SetTrackingAsync(bool v, CancellationToken ct = default) =>
        _client.PutAsync("tracking", new() { ["Tracking"] = v ? "true" : "false" }, ct);

    public Task<int> GetSideOfPierAsync(CancellationToken ct = default) =>
        SafeInt(_client.GetAsync<int>("sideofpier", ct));

    // --- Slew / sync / abort ---

    /// <summary>Slew asynchronously. RA in hours (0..24), Dec in degrees (-90..+90).</summary>
    public Task SlewToCoordinatesAsync(double raHours, double decDeg, CancellationToken ct = default) =>
        _client.PutAsync("slewtocoordinatesasync", new() {
            ["RightAscension"] = raHours.ToString(CultureInfo.InvariantCulture),
            ["Declination"] = decDeg.ToString(CultureInfo.InvariantCulture)
        }, ct);

    public Task SyncToCoordinatesAsync(double raHours, double decDeg, CancellationToken ct = default) =>
        _client.PutAsync("synctocoordinates", new() {
            ["RightAscension"] = raHours.ToString(CultureInfo.InvariantCulture),
            ["Declination"] = decDeg.ToString(CultureInfo.InvariantCulture)
        }, ct);

    public Task AbortSlewAsync(CancellationToken ct = default) =>
        _client.PutAsync("abortslew", null, ct);

    public Task ParkAsync(CancellationToken ct = default) =>
        _client.PutAsync("park", null, ct);
    public Task UnparkAsync(CancellationToken ct = default) =>
        _client.PutAsync("unpark", null, ct);

    private static async Task<bool> SafeBool(Task<bool> t) { try { return await t; } catch { return false; } }
    private static async Task<int> SafeInt(Task<int> t) { try { return await t; } catch { return 0; } }
    private static async Task<double> SafeDouble(Task<double> t) { try { return await t; } catch { return 0; } }
}
