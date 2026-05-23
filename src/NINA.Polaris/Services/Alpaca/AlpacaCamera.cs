namespace NINA.Polaris.Services.Alpaca;

/// <summary>
/// Convenience wrapper exposing the most-used Camera-interface actions from
/// the Alpaca/ASCOM Camera v3 spec. Designed to be easy to add to from the UI
/// without writing the URL routing by hand.
/// </summary>
public class AlpacaCamera {
    private readonly AlpacaClient _client;

    public AlpacaCamera(string host, int port, int deviceNumber = 0) {
        _client = new AlpacaClient(host, port, "camera", deviceNumber);
    }

    // ---- Identity / common ----
    public Task<bool> GetConnectedAsync(CancellationToken ct = default) =>
        BoolDefault(_client.GetAsync<bool>("connected", ct));
    public Task SetConnectedAsync(bool value, CancellationToken ct = default) =>
        _client.PutAsync("connected", new() { ["Connected"] = value ? "true" : "false" }, ct);
    public Task<string?> GetNameAsync(CancellationToken ct = default) =>
        _client.GetAsync<string>("name", ct);
    public Task<string?> GetDescriptionAsync(CancellationToken ct = default) =>
        _client.GetAsync<string>("description", ct);

    // ---- Sensor metadata ----
    public Task<int> GetCameraXSizeAsync(CancellationToken ct = default) =>
        IntDefault(_client.GetAsync<int>("cameraxsize", ct));
    public Task<int> GetCameraYSizeAsync(CancellationToken ct = default) =>
        IntDefault(_client.GetAsync<int>("cameraysize", ct));
    public Task<double> GetPixelSizeXAsync(CancellationToken ct = default) =>
        DoubleDefault(_client.GetAsync<double>("pixelsizex", ct));
    public Task<double> GetPixelSizeYAsync(CancellationToken ct = default) =>
        DoubleDefault(_client.GetAsync<double>("pixelsizey", ct));
    public Task<int> GetMaxBinXAsync(CancellationToken ct = default) =>
        IntDefault(_client.GetAsync<int>("maxbinx", ct));

    // ---- Cooler ----
    public Task<bool> GetCoolerOnAsync(CancellationToken ct = default) =>
        BoolDefault(_client.GetAsync<bool>("cooleron", ct));
    public Task SetCoolerOnAsync(bool on, CancellationToken ct = default) =>
        _client.PutAsync("cooleron", new() { ["CoolerOn"] = on ? "true" : "false" }, ct);
    public Task<double> GetCcdTemperatureAsync(CancellationToken ct = default) =>
        DoubleDefault(_client.GetAsync<double>("ccdtemperature", ct));
    public Task<double> GetSetCcdTemperatureAsync(CancellationToken ct = default) =>
        DoubleDefault(_client.GetAsync<double>("setccdtemperature", ct));
    public Task SetSetCcdTemperatureAsync(double t, CancellationToken ct = default) =>
        _client.PutAsync("setccdtemperature",
            new() { ["SetCCDTemperature"] = t.ToString(System.Globalization.CultureInfo.InvariantCulture) }, ct);

    // ---- Exposure ----
    public Task<int> GetBinXAsync(CancellationToken ct = default) =>
        IntDefault(_client.GetAsync<int>("binx", ct));
    public Task SetBinXAsync(int v, CancellationToken ct = default) =>
        _client.PutAsync("binx", new() { ["BinX"] = v.ToString() }, ct);
    public Task SetBinYAsync(int v, CancellationToken ct = default) =>
        _client.PutAsync("biny", new() { ["BinY"] = v.ToString() }, ct);
    public Task<int> GetGainAsync(CancellationToken ct = default) =>
        IntDefault(_client.GetAsync<int>("gain", ct));
    public Task SetGainAsync(int g, CancellationToken ct = default) =>
        _client.PutAsync("gain", new() { ["Gain"] = g.ToString() }, ct);

    public Task StartExposureAsync(double durationSeconds, bool isLight, CancellationToken ct = default) =>
        _client.PutAsync("startexposure", new() {
            ["Duration"] = durationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Light"] = isLight ? "true" : "false"
        }, ct);

    public Task AbortExposureAsync(CancellationToken ct = default) =>
        _client.PutAsync("abortexposure", null, ct);

    public Task<int> GetCameraStateAsync(CancellationToken ct = default) =>
        IntDefault(_client.GetAsync<int>("camerastate", ct));
    public Task<bool> GetImageReadyAsync(CancellationToken ct = default) =>
        BoolDefault(_client.GetAsync<bool>("imageready", ct));

    /// <summary>Returns the raw ImageArray JSON (a 2-D int array — large!).</summary>
    public Task<System.Text.Json.JsonElement?> GetImageArrayRawAsync(CancellationToken ct = default) =>
        _client.GetRawAsync("imagearray", ct);

    // ---- helpers to default null → sensible value ----
    private static async Task<bool> BoolDefault(Task<bool> task) { try { return await task; } catch { return false; } }
    private static async Task<int> IntDefault(Task<int> task) { try { return await task; } catch { return 0; } }
    private static async Task<double> DoubleDefault(Task<double> task) { try { return await task; } catch { return 0; } }
}
