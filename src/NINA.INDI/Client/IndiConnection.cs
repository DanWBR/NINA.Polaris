using System.Collections.Concurrent;
using NINA.Core.Utility;
using NINA.INDI.Protocol;

namespace NINA.INDI.Client;

public enum ConnectionState {
    Disconnected,
    Connecting,
    Connected,
    Reconnecting
}

/// <summary>
/// High-level connection manager that wraps <see cref="IndiClient"/> with
/// lifecycle state tracking, device discovery, type inference, and health
/// monitoring.
/// </summary>
public class IndiConnection : IDisposable {
    private readonly IndiClient _client;
    private readonly ConcurrentDictionary<string, IndiDeviceInfo> _devices = new();
    private readonly object _stateLock = new();
    private ConnectionState _state = ConnectionState.Disconnected;
    private Timer? _healthTimer;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    /// <summary>
    /// Interval between health-check pings sent via <c>getProperties</c>.
    /// </summary>
    public TimeSpan HealthCheckInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// After this duration without a property update a device is considered lost.
    /// </summary>
    public TimeSpan DeviceLostTimeout { get; set; } = TimeSpan.FromMinutes(2);

    public ConnectionState State {
        get { lock (_stateLock) return _state; }
        private set {
            bool changed;
            lock (_stateLock) {
                changed = _state != value;
                _state = value;
            }
            if (changed) {
                Logger.Info($"IndiConnection state -> {value}");
                StateChanged?.Invoke(value);
            }
        }
    }

    /// <summary>The underlying INDI client.</summary>
    public IndiClient Client => _client;

    /// <summary>Currently known devices.</summary>
    public IReadOnlyDictionary<string, IndiDeviceInfo> Devices => _devices;

    public event Action<ConnectionState>? StateChanged;
    public event Action<IndiDeviceInfo>? DeviceDiscovered;
    public event Action<string>? DeviceLost;

    public IndiConnection(IndiClient client) {
        _client = client ?? throw new ArgumentNullException(nameof(client));

        _client.DeviceFound += OnDeviceFound;
        _client.PropertyChanged += OnPropertyChanged;
        _client.Disconnected += OnDisconnected;
        _client.Reconnected += OnReconnected;
        _client.ReconnectAttempt += OnReconnectAttempt;
    }

    /// <summary>
    /// Connects to the INDI server and starts health monitoring.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default) {
        if (State == ConnectionState.Connected || State == ConnectionState.Connecting) return;

        State = ConnectionState.Connecting;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try {
            await _client.ConnectAsync(ct);
            State = ConnectionState.Connected;
            StartHealthMonitor();
        } catch {
            State = ConnectionState.Disconnected;
            throw;
        }
    }

    /// <summary>
    /// Disconnects from the INDI server and stops monitoring.
    /// </summary>
    public async Task DisconnectAsync() {
        StopHealthMonitor();
        await _client.DisconnectAsync();
        _devices.Clear();
        State = ConnectionState.Disconnected;
    }

    // ── IndiClient event handlers ──────────────────────────────────────

    private void OnDeviceFound(string deviceName) {
        var info = _devices.GetOrAdd(deviceName, _ => new IndiDeviceInfo {
            Name = deviceName,
            InferredType = IndiDeviceType.Unknown,
            LastSeen = DateTime.UtcNow
        });

        // Re-infer now that the device appeared.
        RefreshDeviceInfo(deviceName, info);

        Logger.Info($"INDI device discovered: {info.Name} (type: {info.InferredType})");
        DeviceDiscovered?.Invoke(info);
    }

    private void OnPropertyChanged(string deviceName, IndiProperty property) {
        var info = _devices.GetOrAdd(deviceName, _ => new IndiDeviceInfo {
            Name = deviceName,
            InferredType = IndiDeviceType.Unknown,
            LastSeen = DateTime.UtcNow
        });

        info.LastSeen = DateTime.UtcNow;
        RefreshDeviceInfo(deviceName, info);
    }

    private void OnDisconnected() {
        if (State != ConnectionState.Disconnected) {
            // The client's auto-reconnect will fire ReconnectAttempt next.
            State = ConnectionState.Reconnecting;
            StopHealthMonitor();
        }
    }

    private void OnReconnected() {
        State = ConnectionState.Connected;
        StartHealthMonitor();
    }

    private void OnReconnectAttempt(int attempt) {
        if (State != ConnectionState.Reconnecting) {
            State = ConnectionState.Reconnecting;
        }
        Logger.Debug($"IndiConnection observed reconnect attempt {attempt}");
    }

    // ── Device type inference ──────────────────────────────────────────

    private void RefreshDeviceInfo(string deviceName, IndiDeviceInfo info) {
        if (!_client.Devices.TryGetValue(deviceName, out var props)) return;

        info.PropertyCount = props.Count;
        info.InferredType = InferDeviceType(props);
    }

    /// <summary>
    /// Determines the most likely device type based on which standard INDI
    /// property names are present.
    /// </summary>
    private static IndiDeviceType InferDeviceType(ConcurrentDictionary<string, IndiProperty> props) {
        bool has(string name) => props.ContainsKey(name);

        // Check more specific types first to avoid ambiguous matches.

        if (has("ABS_DOME_POSITION") || has("DOME_SHUTTER"))
            return IndiDeviceType.Dome;

        if (has("ABS_ROTATOR_ANGLE"))
            return IndiDeviceType.Rotator;

        if (has("WEATHER_STATUS") || has("WEATHER_PARAMETERS"))
            return IndiDeviceType.Weather;

        if (has("FLAT_LIGHT_CONTROL"))
            return IndiDeviceType.FlatDevice;

        if (has("FILTER_SLOT"))
            return IndiDeviceType.FilterWheel;

        if (has("ABS_FOCUS_POSITION"))
            return IndiDeviceType.Focuser;

        if (has("EQUATORIAL_EOD_COORD"))
            return IndiDeviceType.Telescope;

        // A device with CCD_EXPOSURE is a camera unless it also has the
        // guider-specific timed-guide property.
        if (has("CCD_EXPOSURE")) {
            return has("TELESCOPE_TIMED_GUIDE_NS")
                ? IndiDeviceType.Guider
                : IndiDeviceType.Camera;
        }

        if (has("TELESCOPE_TIMED_GUIDE_NS"))
            return IndiDeviceType.Guider;

        return IndiDeviceType.Unknown;
    }

    // ── Health monitoring ──────────────────────────────────────────────

    private void StartHealthMonitor() {
        StopHealthMonitor();
        _healthTimer = new Timer(_ => HealthCheckCallback(), null, HealthCheckInterval, HealthCheckInterval);
    }

    private void StopHealthMonitor() {
        _healthTimer?.Dispose();
        _healthTimer = null;
    }

    private async void HealthCheckCallback() {
        if (_disposed || State != ConnectionState.Connected) return;

        try {
            // Lightweight ping: ask the server for its property list.
            await _client.SendAsync(IndiXmlWriter.GetProperties(), _cts?.Token ?? CancellationToken.None);
        } catch (Exception ex) {
            Logger.Warning($"IndiConnection health-check failed: {ex.Message}");
        }

        // Prune devices that haven't been seen within the timeout.
        DateTime cutoff = DateTime.UtcNow - DeviceLostTimeout;
        foreach (var (name, info) in _devices) {
            if (info.LastSeen < cutoff && _devices.TryRemove(name, out _)) {
                Logger.Info($"INDI device lost (timeout): {name}");
                DeviceLost?.Invoke(name);
            }
        }
    }

    // ── Disposal ───────────────────────────────────────────────────────

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;

        StopHealthMonitor();
        _cts?.Cancel();
        _cts?.Dispose();

        _client.DeviceFound -= OnDeviceFound;
        _client.PropertyChanged -= OnPropertyChanged;
        _client.Disconnected -= OnDisconnected;
        _client.Reconnected -= OnReconnected;
        _client.ReconnectAttempt -= OnReconnectAttempt;

        Logger.Info("IndiConnection disposed");
    }
}

public enum IndiDeviceType {
    Unknown,
    Camera,
    Telescope,
    Focuser,
    FilterWheel,
    Guider,
    Dome,
    Rotator,
    Weather,
    FlatDevice
}

/// <summary>
/// Runtime metadata about a discovered INDI device.
/// </summary>
public class IndiDeviceInfo {
    public string Name { get; init; } = string.Empty;
    public IndiDeviceType InferredType { get; set; } = IndiDeviceType.Unknown;
    public int PropertyCount { get; set; }
    public DateTime LastSeen { get; set; }
}
