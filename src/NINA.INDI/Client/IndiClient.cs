using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NINA.Core.Utility;
using NINA.INDI.Protocol;

namespace NINA.INDI.Client;

public class IndiClient : IDisposable {
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private readonly IndiXmlParser _parser = new();
    private CancellationTokenSource? _cts;
    private Task? _readTask;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private int _reconnectAttempt;
    private bool _autoReconnect;

    /// <summary>Diagnostic logger for write tracing. Defaults to
    /// NullLogger so existing call sites that don't pass one keep
    /// working unchanged. When set (NINA.Polaris wires it through),
    /// every Set*Async logs the exact XML being sent — DBGLOG-2
    /// mirrors that into the LOG panel, so the operator can see
    /// whether an INDI write actually went out and (by comparing with
    /// the device's PropertyChanged event right after) whether the
    /// driver acted on it.
    /// Named DiagLogger to avoid colliding with the existing
    /// <c>NINA.Core.Utility.Logger</c> static class that IndiClient
    /// already uses for connect/disconnect tracing.</summary>
    public ILogger DiagLogger { get; set; } = NullLogger.Instance;

    public string Host { get; }
    public int Port { get; }
    public bool IsConnected => _tcp?.Connected == true && _stream != null;

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan SendTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan ReconnectBaseDelay { get; set; } = TimeSpan.FromSeconds(2);
    public int MaxReconnectAttempts { get; set; } = 10;

    public ConcurrentDictionary<string, ConcurrentDictionary<string, IndiProperty>> Devices { get; } = new();

    public event Action<string, IndiProperty>? PropertyChanged;
    public event Action<string>? DeviceFound;
    public event Action<IndiBlobProperty>? BlobReceived;
    public event Action<string, string>? MessageReceived;
    public event Action? Disconnected;
    public event Action? Reconnected;
    public event Action<int>? ReconnectAttempt;

    public IndiClient(string host = "localhost", int port = 7624) {
        Host = host;
        Port = port;

        _parser.PropertyDefined += OnPropertyDefined;
        _parser.PropertyUpdated += OnPropertyUpdated;
        _parser.PropertyDeleted += OnPropertyDeleted;
        _parser.MessageReceived += (dev, msg) => MessageReceived?.Invoke(dev, msg);
    }

    public async Task ConnectAsync(CancellationToken ct = default) {
        if (IsConnected) return;

        _tcp = new TcpClient {
            SendTimeout = (int)SendTimeout.TotalMilliseconds,
            ReceiveTimeout = 0, // read loop manages its own cancellation
            NoDelay = true
        };
        _tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(ConnectTimeout);

        try {
            await _tcp.ConnectAsync(Host, Port, connectCts.Token);
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
            _tcp.Dispose();
            _tcp = null;
            throw new TimeoutException($"Connection to {Host}:{Port} timed out after {ConnectTimeout.TotalSeconds}s");
        }

        _stream = _tcp.GetStream();
        _cts = new CancellationTokenSource();
        _reconnectAttempt = 0;
        _autoReconnect = true;

        Logger.Info($"Connected to INDI server at {Host}:{Port}");

        _readTask = Task.Run(() => ReadLoopAsync(_cts.Token), _cts.Token);

        await SendAsync(IndiXmlWriter.GetProperties(), ct);
    }

    private async Task ReadLoopAsync(CancellationToken ct) {
        try {
            await _parser.ParseStreamAsync(_stream!, ct);
        } catch (Exception ex) when (!ct.IsCancellationRequested) {
            Logger.Warning($"INDI read loop lost connection: {ex.Message}");
        }

        if (!ct.IsCancellationRequested && _autoReconnect) {
            _ = Task.Run(() => AutoReconnectAsync());
        }
    }

    private async Task AutoReconnectAsync() {
        Logger.Info("INDI auto-reconnect started");
        Disconnected?.Invoke();

        CleanupConnection();

        while (_autoReconnect && _reconnectAttempt < MaxReconnectAttempts) {
            _reconnectAttempt++;
            var delay = TimeSpan.FromSeconds(
                Math.Min(ReconnectBaseDelay.TotalSeconds * Math.Pow(2, _reconnectAttempt - 1), 60));

            Logger.Info($"INDI reconnect attempt {_reconnectAttempt}/{MaxReconnectAttempts} in {delay.TotalSeconds:F0}s");
            ReconnectAttempt?.Invoke(_reconnectAttempt);

            await Task.Delay(delay);

            if (!_autoReconnect) break;

            try {
                await ConnectAsync();
                Logger.Info("INDI reconnected successfully");
                Reconnected?.Invoke();
                return;
            } catch (Exception ex) {
                Logger.Warning($"INDI reconnect attempt {_reconnectAttempt} failed: {ex.Message}");
                CleanupConnection();
            }
        }

        if (_reconnectAttempt >= MaxReconnectAttempts) {
            Logger.Error($"INDI gave up reconnecting after {MaxReconnectAttempts} attempts");
        }
    }

    private void CleanupConnection() {
        try { _stream?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        _stream = null;
        _tcp = null;
    }

    public async Task DisconnectAsync() {
        _autoReconnect = false;
        _cts?.Cancel();

        if (_readTask != null) {
            try { await _readTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        }

        CleanupConnection();
        Logger.Info("Disconnected from INDI server");
        Disconnected?.Invoke();
    }

    public async Task SendAsync(byte[] data, CancellationToken ct = default) {
        if (_stream == null) throw new InvalidOperationException("Not connected to INDI server");

        if (!await _sendLock.WaitAsync(SendTimeout, ct)) {
            throw new TimeoutException("INDI send lock timed out, previous send still in progress");
        }

        try {
            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            sendCts.CancelAfter(SendTimeout);
            await _stream.WriteAsync(data, sendCts.Token);
            await _stream.FlushAsync(sendCts.Token);
        } finally {
            _sendLock.Release();
        }
    }

    public async Task EnableBlobAsync(string device, CancellationToken ct = default) {
        await SendAsync(IndiXmlWriter.EnableBLOB(device), ct);
    }

    public async Task SetNumberAsync(string device, string property, Dictionary<string, double> values,
        CancellationToken ct = default) {
        LogIndiWrite("newNumberVector", device, property,
            string.Join(", ", values.Select(kv => kv.Key + "=" + kv.Value)));
        await SendAsync(IndiXmlWriter.NewNumberVector(device, property, values), ct);
    }

    public async Task SetSwitchAsync(string device, string property, Dictionary<string, bool> values,
        CancellationToken ct = default) {
        LogIndiWrite("newSwitchVector", device, property,
            string.Join(", ", values.Select(kv => kv.Key + "=" + (kv.Value ? "On" : "Off"))));
        await SendAsync(IndiXmlWriter.NewSwitchVector(device, property, values), ct);
    }

    public async Task SetTextAsync(string device, string property, Dictionary<string, string> values,
        CancellationToken ct = default) {
        LogIndiWrite("newTextVector", device, property,
            string.Join(", ", values.Select(kv => kv.Key + "=\"" + kv.Value + "\"")));
        await SendAsync(IndiXmlWriter.NewTextVector(device, property, values), ct);
    }

    /// <summary>Shared logging path so every INDI write surfaces in the
    /// LOG panel with a uniform shape. Emits the property + element
    /// values AND a warning when the target property doesn't exist on
    /// the device's snapshot — the most common reason a write is
    /// silently dropped is that the driver doesn't advertise the
    /// property name we picked (e.g. <c>GEOGRAPHIC_COORD</c> vs. some
    /// driver-specific alias). INDI itself never replies to writes,
    /// so this warning is the only way to spot the mismatch without
    /// sniffing the wire.</summary>
    private void LogIndiWrite(string kind, string device, string property, string elementsLog) {
        var exists = Devices.TryGetValue(device, out var props) && props.ContainsKey(property);
        if (exists) {
            DiagLogger.LogInformation("INDI {Kind} → device='{Device}' property='{Property}' [{Elements}]",
                kind, device, property, elementsLog);
        } else {
            // Build a hint of which properties DO exist on this device so
            // the user can find the right name. Truncate the list — chatty
            // devices have 50+ properties.
            var hint = props == null
                ? "(device not announced)"
                : string.Join(", ", props.Keys.OrderBy(k => k).Take(20))
                  + (props.Count > 20 ? $", … ({props.Count - 20} more)" : "");
            DiagLogger.LogWarning(
                "INDI {Kind} → device='{Device}' property='{Property}' [{Elements}] -- " +
                "WARNING: property NOT in device snapshot; driver will silently drop the write. " +
                "Available properties on this device: {Hint}",
                kind, device, property, elementsLog, hint);
        }
    }

    public IndiProperty? GetProperty(string device, string name) {
        if (Devices.TryGetValue(device, out var props) && props.TryGetValue(name, out var prop))
            return prop;
        return null;
    }

    public double GetNumber(string device, string property, string element) {
        if (GetProperty(device, property) is IndiNumberProperty np &&
            np.Values.TryGetValue(element, out var elem))
            return elem.Value;
        return double.NaN;
    }

    public bool GetSwitch(string device, string property, string element) {
        if (GetProperty(device, property) is IndiSwitchProperty sp &&
            sp.Values.TryGetValue(element, out var val))
            return val;
        return false;
    }

    public IEnumerable<string> GetDeviceNames() => Devices.Keys;

    private void OnPropertyDefined(IndiProperty prop) {
        var deviceProps = Devices.GetOrAdd(prop.Device, _ => new());
        bool isNew = !Devices.ContainsKey(prop.Device);

        deviceProps[prop.Name] = prop;

        if (isNew) DeviceFound?.Invoke(prop.Device);
        PropertyChanged?.Invoke(prop.Device, prop);

        if (prop is IndiBlobProperty)
            Logger.Debug($"INDI BLOB property defined: {prop.Device}.{prop.Name}");
    }

    private void OnPropertyUpdated(IndiProperty prop) {
        var deviceProps = Devices.GetOrAdd(prop.Device, _ => new());

        if (deviceProps.TryGetValue(prop.Name, out var existing)) {
            MergeProperty(existing, prop);
            existing.State = prop.State;
            existing.Timestamp = prop.Timestamp;
        } else {
            deviceProps[prop.Name] = prop;
        }

        if (prop is IndiBlobProperty blob)
            BlobReceived?.Invoke(blob);

        PropertyChanged?.Invoke(prop.Device, prop);
    }

    private void OnPropertyDeleted(string name) {
        foreach (var device in Devices.Values) {
            device.TryRemove(name, out _);
        }
    }

    private static void MergeProperty(IndiProperty existing, IndiProperty update) {
        switch (existing) {
            case IndiNumberProperty enp when update is IndiNumberProperty unp:
                foreach (var (k, v) in unp.Values) enp.Values[k] = v;
                break;
            case IndiTextProperty etp when update is IndiTextProperty utp:
                foreach (var (k, v) in utp.Values) etp.Values[k] = v;
                break;
            case IndiSwitchProperty esp when update is IndiSwitchProperty usp:
                foreach (var (k, v) in usp.Values) esp.Values[k] = v;
                break;
            case IndiLightProperty elp when update is IndiLightProperty ulp:
                foreach (var (k, v) in ulp.Values) elp.Values[k] = v;
                break;
            case IndiBlobProperty ebp when update is IndiBlobProperty ubp:
                foreach (var (k, v) in ubp.Values) ebp.Values[k] = v;
                break;
        }
    }

    public void Dispose() {
        _autoReconnect = false;
        _cts?.Cancel();
        CleanupConnection();
        _sendLock.Dispose();
    }
}
