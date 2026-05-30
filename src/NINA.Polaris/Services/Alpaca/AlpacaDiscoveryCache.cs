namespace NINA.Polaris.Services.Alpaca;

/// <summary>
/// Process-wide cache of the most recent Alpaca discovery result, sliced
/// by device type. Populated by the /api/alpaca/discover endpoint and
/// the auto-connect flow that follows it; consumed by EquipmentManager
/// when it builds the Camera/Mount/Focuser/FilterWheel "Alpaca" driver
/// option lists.
///
/// <para>Why a singleton cache rather than re-discovering on every
/// dropdown render: the dropdowns refresh whenever the user touches the
/// RIGS tab, but a full UDP-broadcast cycle takes 3s and can stall the
/// UI. The cache is filled once per Discover click and held until the
/// next click (or until the server restarts).</para>
/// </summary>
public class AlpacaDiscoveryCache {
    private readonly object _lock = new();
    private List<AlpacaDiscoveredDevice> _devices = new();
    private DateTime _updatedAt = DateTime.MinValue;

    /// <summary>Replace the cached device list. Called by the discovery
    /// endpoint after a successful scan + auto-connect pass.</summary>
    public void Replace(IEnumerable<AlpacaDiscoveredDevice> devices) {
        lock (_lock) {
            _devices = devices?.ToList() ?? new();
            _updatedAt = DateTime.UtcNow;
        }
    }

    /// <summary>All cached devices, regardless of type. Snapshot copy
    /// so callers can iterate without holding the lock.</summary>
    public IReadOnlyList<AlpacaDiscoveredDevice> All() {
        lock (_lock) { return _devices.ToList(); }
    }

    /// <summary>Devices of a specific Alpaca DeviceType (PascalCase
    /// per the Alpaca spec: "Camera", "Telescope", "Focuser",
    /// "FilterWheel", "Rotator", "CoverCalibrator",
    /// "ObservingConditions", "Dome", "SafetyMonitor", "Switch").
    /// Comparison is case-insensitive so callers can pass "camera".</summary>
    public IReadOnlyList<AlpacaDiscoveredDevice> ByType(string deviceType) {
        if (string.IsNullOrWhiteSpace(deviceType)) return Array.Empty<AlpacaDiscoveredDevice>();
        lock (_lock) {
            return _devices
                .Where(d => string.Equals(d.DeviceType, deviceType, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    /// <summary>Wall-clock of the last successful Replace. UI uses this
    /// to render a "discovered N min ago" hint.</summary>
    public DateTime UpdatedAt {
        get { lock (_lock) { return _updatedAt; } }
    }
}

/// <summary>One Alpaca-discovered device, flattened across the server
/// that hosted it. DeviceId is the canonical "host:port:deviceNumber"
/// string EquipmentManager uses when selecting an Alpaca adapter.</summary>
public record AlpacaDiscoveredDevice(
    string Host,
    int Port,
    string ServerName,
    string DeviceType,
    string DeviceName,
    int DeviceNumber,
    string UniqueId
) {
    public string DeviceId => $"{Host}:{Port}:{DeviceNumber}";
}
