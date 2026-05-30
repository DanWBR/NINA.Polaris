using NINA.INDI.Client;

namespace NINA.INDI.Devices;

public class IndiFilterWheel : NINA.Image.Interfaces.IFilterWheel {
    private readonly IndiClient _client;

    public string DeviceName { get; }
    /// <summary>
    /// True only when the INDI client is up AND the device's per-device
    /// CONNECTION switch is in the CONNECT state. See
    /// <see cref="IndiCamera.IsConnected"/> for the rationale.
    /// </summary>
    public bool IsConnected
        => _client.IsConnected
           && _client.GetSwitch(DeviceName, "CONNECTION", "CONNECT");

    public int Position {
        get => (int)_client.GetNumber(DeviceName, "FILTER_SLOT", "FILTER_SLOT_VALUE");
    }

    public bool IsMoving {
        get {
            var prop = _client.GetProperty(DeviceName, "FILTER_SLOT");
            return prop?.State == Protocol.IndiPropertyState.Busy;
        }
    }

    public string[] FilterNames {
        get {
            var prop = _client.GetProperty(DeviceName, "FILTER_NAME");
            if (prop is Protocol.IndiTextProperty textProp && textProp.Values.Count > 0) {
                return textProp.Values
                    .OrderBy(kv => kv.Key)
                    .Select(kv => kv.Value)
                    .ToArray();
            }
            return [];
        }
    }

    public int FilterCount {
        get {
            var names = FilterNames;
            return names.Length > 0 ? names.Length : 0;
        }
    }

    public string CurrentFilterName {
        get {
            var pos = Position;
            var names = FilterNames;
            if (pos >= 1 && pos <= names.Length)
                return names[pos - 1];
            return $"Filter {pos}";
        }
    }

    public IndiFilterWheel(IndiClient client, string deviceName) {
        _client = client;
        DeviceName = deviceName;
    }

    public async Task ConnectAsync(CancellationToken ct = default) {
        await _client.SetSwitchAsync(DeviceName, "CONNECTION",
            new Dictionary<string, bool> { ["CONNECT"] = true, ["DISCONNECT"] = false }, ct);
    }

    public async Task DisconnectAsync(CancellationToken ct = default) {
        await _client.SetSwitchAsync(DeviceName, "CONNECTION",
            new Dictionary<string, bool> { ["CONNECT"] = false, ["DISCONNECT"] = true }, ct);
    }

    public async Task SetPositionAsync(int position, CancellationToken ct = default) {
        await _client.SetNumberAsync(DeviceName, "FILTER_SLOT",
            new Dictionary<string, double> { ["FILTER_SLOT_VALUE"] = position }, ct);
    }

    public async Task SetFilterByNameAsync(string filterName, CancellationToken ct = default) {
        var names = FilterNames;
        for (int i = 0; i < names.Length; i++) {
            if (names[i].Equals(filterName, StringComparison.OrdinalIgnoreCase)) {
                await SetPositionAsync(i + 1, ct);
                return;
            }
        }
        throw new InvalidOperationException($"Filter '{filterName}' not found. Available: {string.Join(", ", names)}");
    }
}
