using System.Collections.Concurrent;
using System.Net.Sockets;
using NINA.Core.Utility;
using NINA.INDI.Protocol;

namespace NINA.INDI.Client;

public class IndiClient : IDisposable {
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private readonly IndiXmlParser _parser = new();
    private CancellationTokenSource? _cts;
    private Task? _readTask;

    public string Host { get; }
    public int Port { get; }
    public bool IsConnected => _tcp?.Connected ?? false;

    public ConcurrentDictionary<string, ConcurrentDictionary<string, IndiProperty>> Devices { get; } = new();

    public event Action<string, IndiProperty>? PropertyChanged;
    public event Action<string>? DeviceFound;
    public event Action<IndiBlobProperty>? BlobReceived;
    public event Action<string, string>? MessageReceived;
    public event Action? Disconnected;

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

        _tcp = new TcpClient();
        await _tcp.ConnectAsync(Host, Port, ct);
        _stream = _tcp.GetStream();
        _cts = new CancellationTokenSource();

        Logger.Info($"Connected to INDI server at {Host}:{Port}");

        _readTask = Task.Run(() => _parser.ParseStreamAsync(_stream, _cts.Token), _cts.Token);

        // Request all properties
        await SendAsync(IndiXmlWriter.GetProperties(), ct);
    }

    public async Task DisconnectAsync() {
        _cts?.Cancel();

        if (_readTask != null) {
            try { await _readTask; } catch { /* expected */ }
        }

        _stream?.Dispose();
        _tcp?.Dispose();
        _stream = null;
        _tcp = null;

        Logger.Info("Disconnected from INDI server");
        Disconnected?.Invoke();
    }

    public async Task SendAsync(byte[] data, CancellationToken ct = default) {
        if (_stream == null) throw new InvalidOperationException("Not connected");
        await _stream.WriteAsync(data, ct);
        await _stream.FlushAsync(ct);
    }

    public async Task EnableBlobAsync(string device, CancellationToken ct = default) {
        await SendAsync(IndiXmlWriter.EnableBLOB(device), ct);
    }

    public async Task SetNumberAsync(string device, string property, Dictionary<string, double> values,
        CancellationToken ct = default) {
        await SendAsync(IndiXmlWriter.NewNumberVector(device, property, values), ct);
    }

    public async Task SetSwitchAsync(string device, string property, Dictionary<string, bool> values,
        CancellationToken ct = default) {
        await SendAsync(IndiXmlWriter.NewSwitchVector(device, property, values), ct);
    }

    public async Task SetTextAsync(string device, string property, Dictionary<string, string> values,
        CancellationToken ct = default) {
        await SendAsync(IndiXmlWriter.NewTextVector(device, property, values), ct);
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

        if (!Devices.ContainsKey(prop.Device)) {
            DeviceFound?.Invoke(prop.Device);
        }

        deviceProps[prop.Name] = prop;
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

        if (prop is IndiBlobProperty blob) {
            BlobReceived?.Invoke(blob);
        }

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
        _cts?.Cancel();
        _stream?.Dispose();
        _tcp?.Dispose();
    }
}
