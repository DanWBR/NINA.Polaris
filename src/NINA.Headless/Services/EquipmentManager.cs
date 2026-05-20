using NINA.INDI.Client;
using NINA.INDI.Devices;

namespace NINA.Headless.Services;

public class EquipmentManager : IDisposable {
    private readonly IndiClient _indiClient;
    private readonly ILogger<EquipmentManager> _logger;

    public IndiCamera? Camera { get; private set; }
    public IndiTelescope? Telescope { get; private set; }
    public IndiFocuser? Focuser { get; private set; }
    public IndiFilterWheel? FilterWheel { get; private set; }

    public EquipmentManager(IndiClient indiClient, ILogger<EquipmentManager> logger) {
        _indiClient = indiClient;
        _logger = logger;
        _indiClient.DeviceFound += OnDeviceFound;
    }

    public IEnumerable<string> GetDeviceNames() => _indiClient.GetDeviceNames();

    public IndiCamera SelectCamera(string deviceName) {
        Camera = new IndiCamera(_indiClient, deviceName);
        _logger.LogInformation("Camera selected: {Name}", deviceName);
        return Camera;
    }

    public IndiTelescope SelectTelescope(string deviceName) {
        Telescope = new IndiTelescope(_indiClient, deviceName);
        _logger.LogInformation("Telescope selected: {Name}", deviceName);
        return Telescope;
    }

    public IndiFocuser SelectFocuser(string deviceName) {
        Focuser = new IndiFocuser(_indiClient, deviceName);
        _logger.LogInformation("Focuser selected: {Name}", deviceName);
        return Focuser;
    }

    public IndiFilterWheel SelectFilterWheel(string deviceName) {
        FilterWheel = new IndiFilterWheel(_indiClient, deviceName);
        _logger.LogInformation("Filter wheel selected: {Name}", deviceName);
        return FilterWheel;
    }

    public Dictionary<string, object> GetEquipmentStatus() {
        var status = new Dictionary<string, object>();

        status["indi"] = new {
            connected = _indiClient.IsConnected,
            host = _indiClient.Host,
            port = _indiClient.Port,
            deviceCount = _indiClient.Devices.Count
        };

        if (Camera != null) {
            status["camera"] = new {
                name = Camera.DeviceName,
                connected = Camera.IsConnected,
                state = Camera.State.ToString(),
                temperature = Safe(Camera.Temperature),
                coolerOn = Camera.CoolerOn,
                binX = Camera.BinX,
                binY = Camera.BinY
            };
        }

        if (Telescope != null) {
            status["telescope"] = new {
                name = Telescope.DeviceName,
                connected = Telescope.IsConnected,
                ra = Safe(Telescope.RightAscension),
                dec = Safe(Telescope.Declination),
                alt = Safe(Telescope.Altitude),
                az = Safe(Telescope.Azimuth),
                tracking = Telescope.IsTracking,
                slewing = Telescope.IsSlewing,
                parked = Telescope.IsParked,
                pierSide = Telescope.SideOfPier.ToString()
            };
        }

        if (Focuser != null) {
            status["focuser"] = new {
                name = Focuser.DeviceName,
                position = Focuser.Position,
                temperature = Safe(Focuser.Temperature),
                maxPosition = Focuser.MaxPosition,
                moving = Focuser.IsMoving
            };
        }

        if (FilterWheel != null) {
            status["filterWheel"] = new {
                name = FilterWheel.DeviceName,
                position = FilterWheel.Position,
                currentFilter = FilterWheel.CurrentFilterName,
                filters = FilterWheel.FilterNames,
                moving = FilterWheel.IsMoving
            };
        }

        return status;
    }

    static double? Safe(double v) => double.IsNaN(v) || double.IsInfinity(v) ? null : v;

    private void OnDeviceFound(string deviceName) {
        _logger.LogInformation("INDI device discovered: {Name}", deviceName);
    }

    public void Dispose() {
        _indiClient.DeviceFound -= OnDeviceFound;
    }
}
