using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.INDI.Client;
using NINA.INDI.Protocol;

namespace NINA.INDI.Devices;

public class IndiGuider {
    private readonly IndiClient _client;
    private TaskCompletionSource<IImageData>? _exposureTcs;

    public string DeviceName { get; }
    public bool IsConnected => _client.IsConnected;

    public bool IsGuiding {
        get {
            var ns = _client.GetProperty(DeviceName, "TELESCOPE_TIMED_GUIDE_NS");
            var we = _client.GetProperty(DeviceName, "TELESCOPE_TIMED_GUIDE_WE");
            return ns?.State == IndiPropertyState.Busy || we?.State == IndiPropertyState.Busy;
        }
    }

    public double GuideRateRA => _client.GetNumber(DeviceName, "GUIDE_RATE", "GUIDE_RATE_WE");
    public double GuideRateDec => _client.GetNumber(DeviceName, "GUIDE_RATE", "GUIDE_RATE_NS");

    public double LastPulseRA {
        get {
            var west = _client.GetNumber(DeviceName, "TELESCOPE_TIMED_GUIDE_WE", "TIMED_GUIDE_W");
            var east = _client.GetNumber(DeviceName, "TELESCOPE_TIMED_GUIDE_WE", "TIMED_GUIDE_E");
            if (!double.IsNaN(west) && west > 0) return -west;
            if (!double.IsNaN(east) && east > 0) return east;
            return 0;
        }
    }

    public double LastPulseDec {
        get {
            var north = _client.GetNumber(DeviceName, "TELESCOPE_TIMED_GUIDE_NS", "TIMED_GUIDE_N");
            var south = _client.GetNumber(DeviceName, "TELESCOPE_TIMED_GUIDE_NS", "TIMED_GUIDE_S");
            if (!double.IsNaN(north) && north > 0) return north;
            if (!double.IsNaN(south) && south > 0) return -south;
            return 0;
        }
    }

    public IndiGuider(IndiClient client, string deviceName) {
        _client = client;
        DeviceName = deviceName;

        _client.BlobReceived += OnBlobReceived;
        _client.PropertyChanged += OnPropertyChanged;
    }

    public async Task ConnectAsync(CancellationToken ct = default) {
        await _client.ConnectDeviceAsync(DeviceName, ct);
        await _client.EnableBlobAsync(DeviceName, ct);
    }

    public Task DisconnectAsync(CancellationToken ct = default)
        => _client.DisconnectDeviceAsync(DeviceName, ct);

    public enum GuideDirection {
        North,
        South,
        East,
        West
    }

    public async Task GuideAsync(GuideDirection direction, double durationMs, CancellationToken ct = default) {
        switch (direction) {
            case GuideDirection.North:
                await _client.SetNumberAsync(DeviceName, "TELESCOPE_TIMED_GUIDE_NS",
                    new Dictionary<string, double> { ["TIMED_GUIDE_N"] = durationMs, ["TIMED_GUIDE_S"] = 0 }, ct);
                break;
            case GuideDirection.South:
                await _client.SetNumberAsync(DeviceName, "TELESCOPE_TIMED_GUIDE_NS",
                    new Dictionary<string, double> { ["TIMED_GUIDE_N"] = 0, ["TIMED_GUIDE_S"] = durationMs }, ct);
                break;
            case GuideDirection.West:
                await _client.SetNumberAsync(DeviceName, "TELESCOPE_TIMED_GUIDE_WE",
                    new Dictionary<string, double> { ["TIMED_GUIDE_W"] = durationMs, ["TIMED_GUIDE_E"] = 0 }, ct);
                break;
            case GuideDirection.East:
                await _client.SetNumberAsync(DeviceName, "TELESCOPE_TIMED_GUIDE_WE",
                    new Dictionary<string, double> { ["TIMED_GUIDE_W"] = 0, ["TIMED_GUIDE_E"] = durationMs }, ct);
                break;
        }
    }

    public async Task<IImageData> StartExposureAsync(double durationSeconds, CancellationToken ct = default) {
        _exposureTcs = new TaskCompletionSource<IImageData>();

        using var reg = ct.Register(() => _exposureTcs.TrySetCanceled());

        await _client.SetNumberAsync(DeviceName, "CCD_EXPOSURE",
            new Dictionary<string, double> { ["CCD_EXPOSURE_VALUE"] = durationSeconds }, ct);

        return await _exposureTcs.Task;
    }

    public IndiPropertyState GetStatus() {
        var ns = _client.GetProperty(DeviceName, "TELESCOPE_TIMED_GUIDE_NS");
        var we = _client.GetProperty(DeviceName, "TELESCOPE_TIMED_GUIDE_WE");

        if (ns?.State == IndiPropertyState.Alert || we?.State == IndiPropertyState.Alert)
            return IndiPropertyState.Alert;
        if (ns?.State == IndiPropertyState.Busy || we?.State == IndiPropertyState.Busy)
            return IndiPropertyState.Busy;
        if (ns?.State == IndiPropertyState.Ok || we?.State == IndiPropertyState.Ok)
            return IndiPropertyState.Ok;
        return IndiPropertyState.Idle;
    }

    private void OnBlobReceived(IndiBlobProperty blob) {
        if (blob.Device != DeviceName) return;

        foreach (var (name, element) in blob.Values) {
            if (element.Data == null || element.Data.Length == 0) continue;

            try {
                var imageData = FITSReader.Read(element.Data);
                imageData.MetaData.Camera.Name = DeviceName;
                _exposureTcs?.TrySetResult(imageData);
            } catch (Exception ex) {
                _exposureTcs?.TrySetException(ex);
            }
        }
    }

    private void OnPropertyChanged(string device, IndiProperty prop) {
        if (device != DeviceName) return;
        // Could raise events for UI updates here
    }
}
