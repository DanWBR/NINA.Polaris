using NINA.Image.Interfaces;

namespace NINA.Polaris.Services.Alpaca;

/// <summary>
/// Minimal wrappers around the Alpaca/ASCOM device interfaces beyond Camera
/// and Telescope. Surface is intentionally small, covers identity, the
/// connect/disconnect lifecycle, and the read-only state most users care
/// about. Add to from the existing endpoints as needs grow; the URL/payload
/// shape matches the Camera/Telescope wrappers.
/// </summary>

// ---- Focuser ----------------------------------------------------------------
/// <summary>
/// Alpaca/ASCOM Focuser v3 client exposed through <see cref="IFocuser"/> so
/// EquipmentManager, AutoFocusService and the live-stack trigger orchestrator
/// can drive an Alpaca focuser the same way they drive INDI / direct-COM
/// focusers. The legacy Get*/Set*/MoveAsync surface stays in place because
/// <see cref="NINA.Polaris.Endpoints.AlpacaEndpoints"/> still hits it for
/// the JSON probe endpoints.
/// </summary>
public class AlpacaFocuser : IFocuser {
    private readonly AlpacaClient _c;
    private string _deviceName = "Alpaca Focuser";
    private bool _absolute = true;
    private int _maxStep = 100000;
    private int _maxIncrement = 100000;

    public AlpacaFocuser(string host, int port, int n = 0) { _c = new(host, port, "focuser", n); }

    /// <summary>Parse a "host:port:deviceNumber" descriptor (matches the
    /// shape <see cref="AlpacaDiscovery"/> hands back to the equipment
    /// picker) into a constructed wrapper. Defaults the device number to
    /// 0 when the third segment is missing.</summary>
    public static AlpacaFocuser FromDeviceId(string deviceId) {
        var parts = (deviceId ?? "").Split(':');
        if (parts.Length < 2)
            throw new ArgumentException($"Alpaca device id '{deviceId}' must be host:port[:deviceNumber].",
                nameof(deviceId));
        var host = parts[0];
        var port = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        var dev = parts.Length >= 3 && int.TryParse(parts[2],
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
        return new AlpacaFocuser(host, port, dev);
    }

    // ---- legacy direct surface (kept for existing JSON endpoints) ----
    public Task<bool> GetConnectedAsync(CancellationToken ct = default) => Safe(_c.GetAsync<bool>("connected", ct));
    public Task SetConnectedAsync(bool v, CancellationToken ct = default) =>
        _c.PutAsync("connected", new() { ["Connected"] = v ? "true" : "false" }, ct);
    public Task<string?> GetNameAsync(CancellationToken ct = default) => _c.GetAsync<string>("name", ct);
    public Task<int> GetPositionAsync(CancellationToken ct = default) => Safe(_c.GetAsync<int>("position", ct));
    public Task<int> GetMaxStepAsync(CancellationToken ct = default) => Safe(_c.GetAsync<int>("maxstep", ct));
    public Task<int> GetMaxIncrementAsync(CancellationToken ct = default) => Safe(_c.GetAsync<int>("maxincrement", ct));
    public Task<double> GetStepSizeAsync(CancellationToken ct = default) => Safe(_c.GetAsync<double>("stepsize", ct));
    public Task<bool> GetIsMovingAsync(CancellationToken ct = default) => Safe(_c.GetAsync<bool>("ismoving", ct));
    public Task<double> GetTemperatureAsync(CancellationToken ct = default) => Safe(_c.GetAsync<double>("temperature", ct));
    public Task<bool> GetAbsoluteAsync(CancellationToken ct = default) => Safe(_c.GetAsync<bool>("absolute", ct));
    public Task<bool> GetTempCompAsync(CancellationToken ct = default) => Safe(_c.GetAsync<bool>("tempcomp", ct));
    public Task<bool> GetTempCompAvailableAsync(CancellationToken ct = default) => Safe(_c.GetAsync<bool>("tempcompavailable", ct));
    public Task SetTempCompAsync(bool v, CancellationToken ct = default) =>
        _c.PutAsync("tempcomp", new() { ["TempComp"] = v ? "true" : "false" }, ct);
    public Task MoveAsync(int position, CancellationToken ct = default) =>
        _c.PutAsync("move", new() { ["Position"] = position.ToString() }, ct);
    public Task HaltAsync(CancellationToken ct = default) => _c.PutAsync("halt", null, ct);

    // ---- IFocuser surface ----
    public string DeviceName => _deviceName;
    public bool IsConnected   => GetConnectedAsync().GetAwaiter().GetResult();
    public int Position       => GetPositionAsync().GetAwaiter().GetResult();
    public int MaxPosition    => _maxStep;
    /// <summary>Onboard probe in degrees C; NaN when the focuser doesn't
    /// publish a reading. The Alpaca <c>temperature</c> endpoint errors
    /// out on driverless focusers, which our <c>Safe</c> wrapper coerces
    /// to 0 -- so we re-issue the unsafe call here and translate failure
    /// to NaN as the IFocuser contract requires.</summary>
    public double Temperature {
        get {
            try { return _c.GetAsync<double>("temperature").GetAwaiter().GetResult(); }
            catch { return double.NaN; }
        }
    }
    public bool IsMoving => GetIsMovingAsync().GetAwaiter().GetResult();

    public async Task ConnectAsync(CancellationToken ct = default) {
        await SetConnectedAsync(true, ct);
        // Cache the slow-changing capability values so MaxPosition /
        // DeviceName reads don't issue an HTTP request per access.
        try { _deviceName = (await GetNameAsync(ct)) ?? "Alpaca Focuser"; } catch { }
        _absolute     = await GetAbsoluteAsync(ct);
        _maxStep      = await GetMaxStepAsync(ct);
        _maxIncrement = await GetMaxIncrementAsync(ct);
        if (_maxStep <= 0) _maxStep = 100000;
        if (_maxIncrement <= 0) _maxIncrement = _maxStep;
    }

    public Task DisconnectAsync(CancellationToken ct = default) => SetConnectedAsync(false, ct);

    public async Task MoveAbsoluteAsync(int position, CancellationToken ct = default) {
        var clamped = Math.Clamp(position, 0, _maxStep);
        await MoveAsync(clamped, ct);
        await PollUntilSettledAsync(ct);
    }

    public async Task MoveRelativeAsync(int steps, CancellationToken ct = default) {
        if (_absolute) {
            // Absolute drivers take a target step. Read the current
            // position fresh -- relying on a cached value can race with
            // a concurrent move issued through the JSON endpoints.
            var cur = await GetPositionAsync(ct);
            var target = Math.Clamp(cur + steps, 0, _maxStep);
            await MoveAsync(target, ct);
        } else {
            // Relative-only drivers (rare) accept signed deltas.
            // ASCOM doesn't define a Move that takes a delta directly,
            // so we still use the Position parameter as a delta -- the
            // Alpaca server forwards it to the driver verbatim.
            await MoveAsync(steps, ct);
        }
        await PollUntilSettledAsync(ct);
    }

    public Task AbortAsync(CancellationToken ct = default) => HaltAsync(ct);

    /// <summary>Poll <c>ismoving</c> at 250 ms cadence until the focuser
    /// reports it has settled. On cancellation we issue a Halt to stop
    /// the motor before propagating the OCE -- callers that abort an
    /// auto-focus sweep should not be left with a focuser still slewing
    /// in the background.</summary>
    private async Task PollUntilSettledAsync(CancellationToken ct) {
        try {
            while (!ct.IsCancellationRequested) {
                await Task.Delay(250, ct);
                if (!await GetIsMovingAsync(ct)) return;
            }
        } catch (OperationCanceledException) {
            try { await HaltAsync(CancellationToken.None); } catch { }
            throw;
        }
    }

    private static async Task<bool> Safe(Task<bool> t)     { try { return await t; }   catch { return false; } }
    private static async Task<int> Safe(Task<int> t)       { try { return await t; }   catch { return 0; } }
    private static async Task<double> Safe(Task<double> t) { try { return await t; }   catch { return 0; } }
    private static async Task<string?> Safe(Task<string?> t) { try { return await t; } catch { return null; } }
    private static async Task<List<string>?> Safe(Task<List<string>?> t) { try { return await t; } catch { return null; } }
    private static async Task<List<int>?> Safe(Task<List<int>?> t) { try { return await t; } catch { return null; } }
}

// ---- FilterWheel ------------------------------------------------------------
/// <summary>
/// Alpaca/ASCOM FilterWheel v2 client exposed through <see cref="IFilterWheel"/>.
/// ASCOM's <c>position</c> returns -1 while the wheel is settling; we
/// surface that as <see cref="IsMoving"/> = true and translate -1 to the
/// last known stable slot for the <see cref="Position"/> getter so the
/// sequencer never sees a transient negative value.
/// </summary>
public class AlpacaFilterWheel : IFilterWheel {
    private readonly AlpacaClient _c;
    private string _deviceName = "Alpaca Filter Wheel";
    private string[] _names = Array.Empty<string>();
    private int _lastPosition;

    public AlpacaFilterWheel(string host, int port, int n = 0) { _c = new(host, port, "filterwheel", n); }

    /// <summary>See <see cref="AlpacaFocuser.FromDeviceId"/>.</summary>
    public static AlpacaFilterWheel FromDeviceId(string deviceId) {
        var parts = (deviceId ?? "").Split(':');
        if (parts.Length < 2)
            throw new ArgumentException($"Alpaca device id '{deviceId}' must be host:port[:deviceNumber].",
                nameof(deviceId));
        var host = parts[0];
        var port = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        var dev = parts.Length >= 3 && int.TryParse(parts[2],
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
        return new AlpacaFilterWheel(host, port, dev);
    }

    // ---- legacy direct surface (kept for existing JSON endpoints) ----
    public Task<bool> GetConnectedAsync(CancellationToken ct = default) => Safe(_c.GetAsync<bool>("connected", ct));
    public Task SetConnectedAsync(bool v, CancellationToken ct = default) =>
        _c.PutAsync("connected", new() { ["Connected"] = v ? "true" : "false" }, ct);
    public Task<string?> GetNameAsync(CancellationToken ct = default) => _c.GetAsync<string>("name", ct);
    /// <summary>Currently selected filter slot, 0-based; -1 = moving.</summary>
    public Task<int> GetPositionAsync(CancellationToken ct = default) => Safe(_c.GetAsync<int>("position", ct));
    public Task<List<string>?> GetNamesAsync(CancellationToken ct = default) =>
        Safe(_c.GetAsync<List<string>>("names", ct));
    public Task<List<int>?> GetFocusOffsetsAsync(CancellationToken ct = default) =>
        Safe(_c.GetAsync<List<int>>("focusoffsets", ct));

    // ---- IFilterWheel surface ----
    public string DeviceName => _deviceName;
    public bool IsConnected  => GetConnectedAsync().GetAwaiter().GetResult();

    public int Position {
        get {
            var raw = GetPositionAsync().GetAwaiter().GetResult();
            if (raw < 0) return _lastPosition;
            _lastPosition = raw;
            return raw;
        }
    }

    public bool IsMoving => GetPositionAsync().GetAwaiter().GetResult() < 0;

    public string[] FilterNames => _names;
    public int FilterCount      => _names.Length;
    public string CurrentFilterName {
        get {
            var p = Position;
            return (p >= 0 && p < _names.Length) ? _names[p] : "";
        }
    }

    public async Task ConnectAsync(CancellationToken ct = default) {
        await SetConnectedAsync(true, ct);
        try { _deviceName = (await GetNameAsync(ct)) ?? "Alpaca Filter Wheel"; } catch { }
        var names = await GetNamesAsync(ct);
        _names = names?.ToArray() ?? Array.Empty<string>();
        var pos = await GetPositionAsync(ct);
        _lastPosition = pos < 0 ? 0 : pos;
    }

    public Task DisconnectAsync(CancellationToken ct = default) => SetConnectedAsync(false, ct);

    /// <summary>Switch the wheel to <paramref name="position"/>, then
    /// poll until <c>position</c> reports the target slot (i.e. no
    /// longer the -1 "moving" sentinel and equal to what we asked for).
    /// A 30s upper bound matches every other backend's settle budget
    /// and protects against an Alpaca server that loses the request.</summary>
    public async Task SetPositionAsync(int position, CancellationToken ct = default) {
        var slot = _names.Length > 0
            ? Math.Clamp(position, 0, _names.Length - 1)
            : Math.Max(0, position);
        await _c.PutAsync("position",
            new() { ["Position"] = slot.ToString(System.Globalization.CultureInfo.InvariantCulture) }, ct);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline) {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(250, ct);
            var cur = await GetPositionAsync(ct);
            if (cur != -1 && cur == slot) {
                _lastPosition = cur;
                return;
            }
        }
        throw new TimeoutException($"Alpaca filter wheel did not reach slot {slot} within 30s.");
    }

    public Task SetFilterByNameAsync(string filterName, CancellationToken ct = default) {
        if (string.IsNullOrEmpty(filterName)) return Task.CompletedTask;
        var idx = Array.FindIndex(_names, n =>
            string.Equals(n, filterName, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
            throw new ArgumentException(
                $"Filter '{filterName}' not found in wheel (have: {string.Join(", ", _names)}).",
                nameof(filterName));
        return SetPositionAsync(idx, ct);
    }

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
