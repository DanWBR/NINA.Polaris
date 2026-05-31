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
                // FIX: previously did OrderBy(kv.Key) (lexicographic),
                // which on wheels with >9 slots ordered the names
                // FILTER_SLOT_NAME_1, _10, _2, _3, ..., _9 -- garbage
                // alignment with the slot dropdown. Sort by the
                // trailing integer instead. Falls back to lexicographic
                // for any element whose name doesn't end in digits, so
                // a non-conformant driver doesn't crash the read.
                return textProp.Values
                    .OrderBy(kv => ExtractIndex(kv.Key))
                    .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => kv.Value)
                    .ToArray();
            }
            return [];
        }
    }

    /// <summary>Parse the trailing integer from an INDI filter-name
    /// element id like <c>FILTER_SLOT_NAME_1</c> / <c>FILTER_SLOT_NAME_10</c>.
    /// Returns <see cref="int.MaxValue"/> for anything that doesn't
    /// end in digits, so non-conformant elements sink to the end of
    /// the list instead of corrupting the ordering of the conformant
    /// ones.</summary>
    private static int ExtractIndex(string elementId) {
        int i = elementId.Length - 1;
        while (i >= 0 && char.IsDigit(elementId[i])) i--;
        if (i == elementId.Length - 1) return int.MaxValue;
        var tail = elementId.AsSpan(i + 1);
        return int.TryParse(tail, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? n : int.MaxValue;
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

    /// <summary>Capabilities advertisement. INDI wheels with a
    /// FILTER_NAME text vector accept name pushes; we probe the live
    /// property so wheels without it correctly report SupportsEditNames=false.</summary>
    public NINA.Image.Interfaces.FilterWheelCapabilities Capabilities
        => new(SupportsEditNames:
            _client.GetProperty(DeviceName, "FILTER_NAME") is Protocol.IndiTextProperty);

    /// <summary>Push a new filter-name set into the driver via INDI
    /// standard <c>FILTER_NAME</c>. The driver persists these in its
    /// own config so subsequent reconnects keep them. Element ids
    /// must match what the driver already advertises (typically
    /// <c>FILTER_SLOT_NAME_1</c>..<c>_N</c> sorted by trailing index);
    /// we map <paramref name="names"/>[0] to the lowest-indexed
    /// element, [1] to the next, and so on.</summary>
    public async Task SetFilterNamesAsync(string[] names, CancellationToken ct = default) {
        if (names == null) throw new ArgumentNullException(nameof(names));
        var prop = _client.GetProperty(DeviceName, "FILTER_NAME") as Protocol.IndiTextProperty;
        if (prop == null) {
            throw new NotSupportedException(
                $"Filter wheel '{DeviceName}' does not expose FILTER_NAME -- driver doesn't support name push.");
        }
        // Preserve the driver's actual element ordering by using the
        // same numeric sort as the FilterNames getter -- that ensures
        // names[0] lands on slot 1, names[1] on slot 2, etc., even
        // when the driver advertises the elements in a non-numeric
        // dictionary order.
        var orderedKeys = prop.Values
            .OrderBy(kv => ExtractIndex(kv.Key))
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Key)
            .ToArray();
        if (names.Length != orderedKeys.Length) {
            throw new ArgumentException(
                $"Expected {orderedKeys.Length} filter names (driver advertises {orderedKeys.Length} slots), got {names.Length}.",
                nameof(names));
        }
        var payload = new Dictionary<string, string>(orderedKeys.Length);
        for (int i = 0; i < orderedKeys.Length; i++) {
            payload[orderedKeys[i]] = names[i] ?? "";
        }
        await _client.SetTextAsync(DeviceName, "FILTER_NAME", payload, ct);
    }
}
