using NINA.Core.Enum;
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.INDI.Client;
using NINA.INDI.Protocol;

namespace NINA.INDI.Devices;

public class IndiCamera {
    private readonly IndiClient _client;
    private TaskCompletionSource<IImageData>? _exposureTcs;

    public string DeviceName { get; }
    public bool IsConnected => _client.IsConnected;

    public CameraStates State {
        get {
            var prop = _client.GetProperty(DeviceName, "CCD_EXPOSURE");
            if (prop == null) return CameraStates.NoState;
            return prop.State switch {
                IndiPropertyState.Busy => CameraStates.Exposing,
                IndiPropertyState.Ok => CameraStates.Idle,
                IndiPropertyState.Alert => CameraStates.Error,
                _ => CameraStates.Idle
            };
        }
    }

    public double Temperature => _client.GetNumber(DeviceName, "CCD_TEMPERATURE", "CCD_TEMPERATURE_VALUE");
    public int BinX => (int)_client.GetNumber(DeviceName, "CCD_BINNING", "HOR_BIN");
    public int BinY => (int)_client.GetNumber(DeviceName, "CCD_BINNING", "VER_BIN");
    public bool CoolerOn => _client.GetSwitch(DeviceName, "CCD_COOLER", "COOLER_ON");
    public double CoolerPower => _client.GetNumber(DeviceName, "CCD_COOLER_POWER", "CCD_COOLER_VALUE");
    public int MaxX => (int)_client.GetNumber(DeviceName, "CCD_INFO", "CCD_MAX_X");
    public int MaxY => (int)_client.GetNumber(DeviceName, "CCD_INFO", "CCD_MAX_Y");
    public double PixelSizeX => _client.GetNumber(DeviceName, "CCD_INFO", "CCD_PIXEL_SIZE_X");
    public double PixelSizeY => _client.GetNumber(DeviceName, "CCD_INFO", "CCD_PIXEL_SIZE_Y");
    public int BitDepth => (int)_client.GetNumber(DeviceName, "CCD_INFO", "CCD_BITSPERPIXEL");

    public IndiCamera(IndiClient client, string deviceName) {
        _client = client;
        DeviceName = deviceName;

        _client.BlobReceived += OnBlobReceived;
        _client.PropertyChanged += OnPropertyChanged;
    }

    public async Task ConnectAsync(CancellationToken ct = default) {
        await _client.SetSwitchAsync(DeviceName, "CONNECTION",
            new Dictionary<string, bool> { ["CONNECT"] = true, ["DISCONNECT"] = false }, ct);
        await _client.EnableBlobAsync(DeviceName, ct);
    }

    public async Task DisconnectAsync(CancellationToken ct = default) {
        await _client.SetSwitchAsync(DeviceName, "CONNECTION",
            new Dictionary<string, bool> { ["CONNECT"] = false, ["DISCONNECT"] = true }, ct);
    }

    public async Task SetBinningAsync(int binX, int binY, CancellationToken ct = default) {
        await _client.SetNumberAsync(DeviceName, "CCD_BINNING",
            new Dictionary<string, double> { ["HOR_BIN"] = binX, ["VER_BIN"] = binY }, ct);
    }

    public async Task SetTemperatureAsync(double temperature, CancellationToken ct = default) {
        await _client.SetNumberAsync(DeviceName, "CCD_TEMPERATURE",
            new Dictionary<string, double> { ["CCD_TEMPERATURE_VALUE"] = temperature }, ct);
    }

    public async Task SetCoolerAsync(bool on, CancellationToken ct = default) {
        await _client.SetSwitchAsync(DeviceName, "CCD_COOLER",
            new Dictionary<string, bool> { ["COOLER_ON"] = on, ["COOLER_OFF"] = !on }, ct);
    }

    public async Task<IImageData> CaptureAsync(double exposureSeconds, CancellationToken ct = default) {
        _exposureTcs = new TaskCompletionSource<IImageData>();

        using var reg = ct.Register(() => _exposureTcs.TrySetCanceled());

        await _client.SetNumberAsync(DeviceName, "CCD_EXPOSURE",
            new Dictionary<string, double> { ["CCD_EXPOSURE_VALUE"] = exposureSeconds }, ct);

        return await _exposureTcs.Task;
    }

    public async Task AbortExposureAsync(CancellationToken ct = default) {
        await _client.SetSwitchAsync(DeviceName, "CCD_ABORT_EXPOSURE",
            new Dictionary<string, bool> { ["ABORT"] = true }, ct);
        _exposureTcs?.TrySetCanceled();
    }

    private void OnBlobReceived(IndiBlobProperty blob) {
        if (blob.Device != DeviceName) return;

        foreach (var (name, element) in blob.Values) {
            if (element.Data == null || element.Data.Length == 0) continue;

            try {
                var imageData = FITSReader.Read(element.Data);

                imageData.MetaData.Camera.Name = DeviceName;
                imageData.MetaData.Camera.Temperature = Temperature;
                imageData.MetaData.Camera.BinX = (short)BinX;
                imageData.MetaData.Camera.BinY = (short)BinY;
                imageData.MetaData.Camera.PixelSizeX = PixelSizeX;
                imageData.MetaData.Camera.PixelSizeY = PixelSizeY;

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
