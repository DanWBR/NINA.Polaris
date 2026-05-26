namespace NINA.Polaris.Services.Alpaca;

/// <summary>
/// Minimal wrappers around the Alpaca/ASCOM device interfaces beyond Camera
/// and Telescope. Surface is intentionally small, covers identity, the
/// connect/disconnect lifecycle, and the read-only state most users care
/// about. Add to from the existing endpoints as needs grow; the URL/payload
/// shape matches the Camera/Telescope wrappers.
/// </summary>

// ---- Focuser ----------------------------------------------------------------
public class AlpacaFocuser {
    private readonly AlpacaClient _c;
    public AlpacaFocuser(string host, int port, int n = 0) { _c = new(host, port, "focuser", n); }

    public Task<bool> GetConnectedAsync(CancellationToken ct = default) => Safe(_c.GetAsync<bool>("connected", ct));
    public Task SetConnectedAsync(bool v, CancellationToken ct = default) =>
        _c.PutAsync("connected", new() { ["Connected"] = v ? "true" : "false" }, ct);
    public Task<string?> GetNameAsync(CancellationToken ct = default) => _c.GetAsync<string>("name", ct);
    public Task<int> GetPositionAsync(CancellationToken ct = default) => Safe(_c.GetAsync<int>("position", ct));
    public Task<int> GetMaxStepAsync(CancellationToken ct = default) => Safe(_c.GetAsync<int>("maxstep", ct));
    public Task<bool> GetIsMovingAsync(CancellationToken ct = default) => Safe(_c.GetAsync<bool>("ismoving", ct));
    public Task<double> GetTemperatureAsync(CancellationToken ct = default) => Safe(_c.GetAsync<double>("temperature", ct));
    public Task<bool> GetAbsoluteAsync(CancellationToken ct = default) => Safe(_c.GetAsync<bool>("absolute", ct));
    public Task MoveAsync(int position, CancellationToken ct = default) =>
        _c.PutAsync("move", new() { ["Position"] = position.ToString() }, ct);
    public Task HaltAsync(CancellationToken ct = default) => _c.PutAsync("halt", null, ct);

    private static async Task<bool> Safe(Task<bool> t)     { try { return await t; }   catch { return false; } }
    private static async Task<int> Safe(Task<int> t)       { try { return await t; }   catch { return 0; } }
    private static async Task<double> Safe(Task<double> t) { try { return await t; }   catch { return 0; } }
    private static async Task<string?> Safe(Task<string?> t) { try { return await t; } catch { return null; } }
    private static async Task<List<string>?> Safe(Task<List<string>?> t) { try { return await t; } catch { return null; } }
    private static async Task<List<int>?> Safe(Task<List<int>?> t) { try { return await t; } catch { return null; } }
}

// ---- FilterWheel ------------------------------------------------------------
public class AlpacaFilterWheel {
    private readonly AlpacaClient _c;
    public AlpacaFilterWheel(string host, int port, int n = 0) { _c = new(host, port, "filterwheel", n); }

    public Task<bool> GetConnectedAsync(CancellationToken ct = default) => Safe(_c.GetAsync<bool>("connected", ct));
    public Task SetConnectedAsync(bool v, CancellationToken ct = default) =>
        _c.PutAsync("connected", new() { ["Connected"] = v ? "true" : "false" }, ct);
    public Task<string?> GetNameAsync(CancellationToken ct = default) => _c.GetAsync<string>("name", ct);
    /// <summary>Currently selected filter slot, 0-based; -1 = moving.</summary>
    public Task<int> GetPositionAsync(CancellationToken ct = default) => Safe(_c.GetAsync<int>("position", ct));
    public Task SetPositionAsync(int slot, CancellationToken ct = default) =>
        _c.PutAsync("position", new() { ["Position"] = slot.ToString() }, ct);
    public Task<List<string>?> GetNamesAsync(CancellationToken ct = default) =>
        Safe(_c.GetAsync<List<string>>("names", ct));
    public Task<List<int>?> GetFocusOffsetsAsync(CancellationToken ct = default) =>
        Safe(_c.GetAsync<List<int>>("focusoffsets", ct));

    private static async Task<bool> Safe(Task<bool> t)     { try { return await t; }   catch { return false; } }
    private static async Task<int> Safe(Task<int> t)       { try { return await t; }   catch { return 0; } }
    private static async Task<double> Safe(Task<double> t) { try { return await t; }   catch { return 0; } }
    private static async Task<string?> Safe(Task<string?> t) { try { return await t; } catch { return null; } }
    private static async Task<List<string>?> Safe(Task<List<string>?> t) { try { return await t; } catch { return null; } }
    private static async Task<List<int>?> Safe(Task<List<int>?> t) { try { return await t; } catch { return null; } }
}

// ---- Rotator ----------------------------------------------------------------
public class AlpacaRotator {
    private readonly AlpacaClient _c;
    public AlpacaRotator(string host, int port, int n = 0) { _c = new(host, port, "rotator", n); }

    public Task<bool> GetConnectedAsync(CancellationToken ct = default) => Safe(_c.GetAsync<bool>("connected", ct));
    public Task SetConnectedAsync(bool v, CancellationToken ct = default) =>
        _c.PutAsync("connected", new() { ["Connected"] = v ? "true" : "false" }, ct);
    public Task<string?> GetNameAsync(CancellationToken ct = default) => _c.GetAsync<string>("name", ct);
    public Task<double> GetPositionAsync(CancellationToken ct = default) => Safe(_c.GetAsync<double>("position", ct));
    public Task<double> GetTargetPositionAsync(CancellationToken ct = default) => Safe(_c.GetAsync<double>("targetposition", ct));
    public Task<bool> GetIsMovingAsync(CancellationToken ct = default) => Safe(_c.GetAsync<bool>("ismoving", ct));
    public Task<bool> GetReverseAsync(CancellationToken ct = default) => Safe(_c.GetAsync<bool>("reverse", ct));
    public Task SetReverseAsync(bool v, CancellationToken ct = default) =>
        _c.PutAsync("reverse", new() { ["Reverse"] = v ? "true" : "false" }, ct);
    public Task MoveAbsoluteAsync(double degrees, CancellationToken ct = default) =>
        _c.PutAsync("moveabsolute", new() { ["Position"] = degrees.ToString(System.Globalization.CultureInfo.InvariantCulture) }, ct);
    public Task SyncAsync(double degrees, CancellationToken ct = default) =>
        _c.PutAsync("sync", new() { ["Position"] = degrees.ToString(System.Globalization.CultureInfo.InvariantCulture) }, ct);
    public Task HaltAsync(CancellationToken ct = default) => _c.PutAsync("halt", null, ct);

    private static async Task<bool> Safe(Task<bool> t)     { try { return await t; }   catch { return false; } }
    private static async Task<int> Safe(Task<int> t)       { try { return await t; }   catch { return 0; } }
    private static async Task<double> Safe(Task<double> t) { try { return await t; }   catch { return 0; } }
    private static async Task<string?> Safe(Task<string?> t) { try { return await t; } catch { return null; } }
    private static async Task<List<string>?> Safe(Task<List<string>?> t) { try { return await t; } catch { return null; } }
    private static async Task<List<int>?> Safe(Task<List<int>?> t) { try { return await t; } catch { return null; } }
}

// ---- Dome -------------------------------------------------------------------
public class AlpacaDome {
    private readonly AlpacaClient _c;
    public AlpacaDome(string host, int port, int n = 0) { _c = new(host, port, "dome", n); }

    public Task<bool> GetConnectedAsync(CancellationToken ct = default) => Safe(_c.GetAsync<bool>("connected", ct));
    public Task SetConnectedAsync(bool v, CancellationToken ct = default) =>
        _c.PutAsync("connected", new() { ["Connected"] = v ? "true" : "false" }, ct);
    public Task<string?> GetNameAsync(CancellationToken ct = default) => _c.GetAsync<string>("name", ct);
    public Task<double> GetAzimuthAsync(CancellationToken ct = default) => Safe(_c.GetAsync<double>("azimuth", ct));
    public Task<bool> GetAtParkAsync(CancellationToken ct = default) => Safe(_c.GetAsync<bool>("atpark", ct));
    public Task<bool> GetAtHomeAsync(CancellationToken ct = default) => Safe(_c.GetAsync<bool>("athome", ct));
    public Task<bool> GetSlewingAsync(CancellationToken ct = default) => Safe(_c.GetAsync<bool>("slewing", ct));
    /// <summary>0=Open, 1=Closed, 2=Opening, 3=Closing, 4=Error.</summary>
    public Task<int> GetShutterStatusAsync(CancellationToken ct = default) => Safe(_c.GetAsync<int>("shutterstatus", ct));
    public Task<bool> GetSlavedAsync(CancellationToken ct = default) => Safe(_c.GetAsync<bool>("slaved", ct));
    public Task SetSlavedAsync(bool v, CancellationToken ct = default) =>
        _c.PutAsync("slaved", new() { ["Slaved"] = v ? "true" : "false" }, ct);

    public Task OpenShutterAsync(CancellationToken ct = default) => _c.PutAsync("openshutter", null, ct);
    public Task CloseShutterAsync(CancellationToken ct = default) => _c.PutAsync("closeshutter", null, ct);
    public Task ParkAsync(CancellationToken ct = default) => _c.PutAsync("park", null, ct);
    public Task FindHomeAsync(CancellationToken ct = default) => _c.PutAsync("findhome", null, ct);
    public Task AbortSlewAsync(CancellationToken ct = default) => _c.PutAsync("abortslew", null, ct);
    public Task SlewToAzimuthAsync(double az, CancellationToken ct = default) =>
        _c.PutAsync("slewtoazimuth", new() { ["Azimuth"] = az.ToString(System.Globalization.CultureInfo.InvariantCulture) }, ct);

    private static async Task<bool> Safe(Task<bool> t)     { try { return await t; }   catch { return false; } }
    private static async Task<int> Safe(Task<int> t)       { try { return await t; }   catch { return 0; } }
    private static async Task<double> Safe(Task<double> t) { try { return await t; }   catch { return 0; } }
    private static async Task<string?> Safe(Task<string?> t) { try { return await t; } catch { return null; } }
    private static async Task<List<string>?> Safe(Task<List<string>?> t) { try { return await t; } catch { return null; } }
    private static async Task<List<int>?> Safe(Task<List<int>?> t) { try { return await t; } catch { return null; } }
}

// ---- CoverCalibrator (flat panel) -------------------------------------------
// ASCOM uses CoverCalibrator for flat panels; "CalibratorState" + "CoverState"
// + brightness controls. Old separate "Switch" / "CoverCalibrator" splits exist
// in some drivers; this targets the modern combined interface.
public class AlpacaCoverCalibrator {
    private readonly AlpacaClient _c;
    public AlpacaCoverCalibrator(string host, int port, int n = 0) { _c = new(host, port, "covercalibrator", n); }

    public Task<bool> GetConnectedAsync(CancellationToken ct = default) => Safe(_c.GetAsync<bool>("connected", ct));
    public Task SetConnectedAsync(bool v, CancellationToken ct = default) =>
        _c.PutAsync("connected", new() { ["Connected"] = v ? "true" : "false" }, ct);
    public Task<string?> GetNameAsync(CancellationToken ct = default) => _c.GetAsync<string>("name", ct);
    /// <summary>0=NotPresent, 1=Off, 2=NotReady, 3=Ready, 4=Unknown, 5=Error.</summary>
    public Task<int> GetCalibratorStateAsync(CancellationToken ct = default) => Safe(_c.GetAsync<int>("calibratorstate", ct));
    /// <summary>0=NotPresent, 1=Closed, 2=Moving, 3=Open, 4=Unknown, 5=Error.</summary>
    public Task<int> GetCoverStateAsync(CancellationToken ct = default) => Safe(_c.GetAsync<int>("coverstate", ct));
    public Task<int> GetBrightnessAsync(CancellationToken ct = default) => Safe(_c.GetAsync<int>("brightness", ct));
    public Task<int> GetMaxBrightnessAsync(CancellationToken ct = default) => Safe(_c.GetAsync<int>("maxbrightness", ct));

    public Task OpenCoverAsync(CancellationToken ct = default) => _c.PutAsync("opencover", null, ct);
    public Task CloseCoverAsync(CancellationToken ct = default) => _c.PutAsync("closecover", null, ct);
    public Task HaltCoverAsync(CancellationToken ct = default) => _c.PutAsync("haltcover", null, ct);
    public Task CalibratorOnAsync(int brightness, CancellationToken ct = default) =>
        _c.PutAsync("calibratoron", new() { ["Brightness"] = brightness.ToString() }, ct);
    public Task CalibratorOffAsync(CancellationToken ct = default) => _c.PutAsync("calibratoroff", null, ct);

    private static async Task<bool> Safe(Task<bool> t)     { try { return await t; }   catch { return false; } }
    private static async Task<int> Safe(Task<int> t)       { try { return await t; }   catch { return 0; } }
    private static async Task<double> Safe(Task<double> t) { try { return await t; }   catch { return 0; } }
    private static async Task<string?> Safe(Task<string?> t) { try { return await t; } catch { return null; } }
    private static async Task<List<string>?> Safe(Task<List<string>?> t) { try { return await t; } catch { return null; } }
    private static async Task<List<int>?> Safe(Task<List<int>?> t) { try { return await t; } catch { return null; } }
}

// ---- ObservingConditions (weather) ------------------------------------------
public class AlpacaObservingConditions {
    private readonly AlpacaClient _c;
    public AlpacaObservingConditions(string host, int port, int n = 0) { _c = new(host, port, "observingconditions", n); }

    public Task<bool> GetConnectedAsync(CancellationToken ct = default) => Safe(_c.GetAsync<bool>("connected", ct));
    public Task SetConnectedAsync(bool v, CancellationToken ct = default) =>
        _c.PutAsync("connected", new() { ["Connected"] = v ? "true" : "false" }, ct);
    public Task<string?> GetNameAsync(CancellationToken ct = default) => _c.GetAsync<string>("name", ct);

    public Task<double> GetCloudCoverAsync(CancellationToken ct = default) => Safe(_c.GetAsync<double>("cloudcover", ct));
    public Task<double> GetDewPointAsync(CancellationToken ct = default) => Safe(_c.GetAsync<double>("dewpoint", ct));
    public Task<double> GetHumidityAsync(CancellationToken ct = default) => Safe(_c.GetAsync<double>("humidity", ct));
    public Task<double> GetPressureAsync(CancellationToken ct = default) => Safe(_c.GetAsync<double>("pressure", ct));
    public Task<double> GetRainRateAsync(CancellationToken ct = default) => Safe(_c.GetAsync<double>("rainrate", ct));
    public Task<double> GetSkyBrightnessAsync(CancellationToken ct = default) => Safe(_c.GetAsync<double>("skybrightness", ct));
    public Task<double> GetSkyQualityAsync(CancellationToken ct = default) => Safe(_c.GetAsync<double>("skyquality", ct));
    public Task<double> GetSkyTemperatureAsync(CancellationToken ct = default) => Safe(_c.GetAsync<double>("skytemperature", ct));
    public Task<double> GetTemperatureAsync(CancellationToken ct = default) => Safe(_c.GetAsync<double>("temperature", ct));
    public Task<double> GetWindDirectionAsync(CancellationToken ct = default) => Safe(_c.GetAsync<double>("winddirection", ct));
    public Task<double> GetWindGustAsync(CancellationToken ct = default) => Safe(_c.GetAsync<double>("windgust", ct));
    public Task<double> GetWindSpeedAsync(CancellationToken ct = default) => Safe(_c.GetAsync<double>("windspeed", ct));
    public Task RefreshAsync(CancellationToken ct = default) => _c.PutAsync("refresh", null, ct);

    private static async Task<bool> Safe(Task<bool> t)     { try { return await t; }   catch { return false; } }
    private static async Task<int> Safe(Task<int> t)       { try { return await t; }   catch { return 0; } }
    private static async Task<double> Safe(Task<double> t) { try { return await t; }   catch { return 0; } }
    private static async Task<string?> Safe(Task<string?> t) { try { return await t; } catch { return null; } }
    private static async Task<List<string>?> Safe(Task<List<string>?> t) { try { return await t; } catch { return null; } }
    private static async Task<List<int>?> Safe(Task<List<int>?> t) { try { return await t; } catch { return null; } }
}
