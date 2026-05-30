using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NINA.Core.Enum;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;

namespace NINA.Ascom.Com;

/// <summary>
/// ASCOM Platform Camera (ICameraV3) adapter exposed through Polaris's
/// <see cref="ICamera"/> contract. Late-binds to the driver via the
/// ProgID + <see cref="Type.GetTypeFromProgID(string)"/> so no
/// compile-time reference to the ASCOM Platform assemblies is
/// required — Polaris ships without any ASCOM bits and starts fine on
/// machines that have never installed the Platform.
///
/// <para>Every COM interaction is funnelled through an
/// <see cref="AscomComStaDispatcher"/> pinned to a single STA thread
/// for this driver instance. See the dispatcher docstring for the
/// rationale.</para>
///
/// <para>Supported subset of ICameraV3: connect/disconnect, sensor
/// metadata, cooler controls, binning, gain (numeric range only —
/// named-gain string lists are not exposed), single-frame capture
/// via <c>StartExposure</c> + <c>ImageReady</c> poll + <c>ImageArray</c>,
/// abort, subframe / ROI. Out of scope for v1: <c>FastReadout</c>,
/// <c>ReadoutMode</c> selection, <c>PulseGuide</c>, <c>BayerOffsetX/Y</c>
/// (debayer offsets), <c>SensorType</c> (already exposed via the
/// driver name in the rig metadata).</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AscomComCamera : ICamera, IDisposable {
    private readonly string _progId;
    private readonly AscomComStaDispatcher _disp;
    private dynamic? _driver;
    // Cached metadata, only meaningful after Connect. Reading these
    // properties off the driver again on every status broadcast is
    // wasteful (they don't change at runtime) and risks blocking the
    // status loop if the driver decides to do I/O on a property get.
    private string _deviceName = "ASCOM Camera";
    private int _maxX, _maxY;
    private double _pixelSizeX, _pixelSizeY;
    private int _bitDepth = 16;
    private bool _canCool, _canBin, _canAbort, _canRoi, _hasGain;
    private int _gainMin, _gainMax;

    public AscomComCamera(string progId) {
        _progId = progId ?? throw new ArgumentNullException(nameof(progId));
        _disp = new AscomComStaDispatcher($"ASCOM-Camera-{progId}");
    }

    public string DeviceName => _deviceName;
    public bool IsConnected => _driver != null
        && _disp.Invoke<bool>(() => SafeGet<bool>(() => _driver!.Connected)).Result;

    public CameraStates State {
        get {
            if (_driver == null) return CameraStates.Idle;
            // ICameraV3.CameraState: 0=Idle, 1=Waiting, 2=Exposing,
            // 3=Reading, 4=Download, 5=Error. Map to Polaris's
            // CameraStates which mirrors that enum exactly.
            var v = _disp.Invoke<int>(() => SafeGet<int>(() => (int)_driver!.CameraState, 0))
                .GetAwaiter().GetResult();
            return v switch {
                0 => CameraStates.Idle,
                1 => CameraStates.Waiting,
                2 => CameraStates.Exposing,
                3 => CameraStates.Reading,
                4 => CameraStates.Download,
                _ => CameraStates.Error
            };
        }
    }

    public double Temperature => _driver == null
        ? double.NaN
        : _disp.Invoke<double>(() => SafeGet<double>(() => (double)_driver!.CCDTemperature, double.NaN))
            .GetAwaiter().GetResult();

    public bool CoolerOn => _driver != null && _canCool
        && _disp.Invoke<bool>(() => SafeGet<bool>(() => (bool)_driver!.CoolerOn))
            .GetAwaiter().GetResult();

    public double CoolerPower => _driver != null && _canCool
        ? _disp.Invoke<double>(() => SafeGet<double>(() => (double)_driver!.CoolerPower))
            .GetAwaiter().GetResult()
        : 0;

    public int BinX => _driver == null ? 1
        : _disp.Invoke<int>(() => SafeGet<int>(() => (int)(short)_driver!.BinX, 1))
            .GetAwaiter().GetResult();
    public int BinY => _driver == null ? 1
        : _disp.Invoke<int>(() => SafeGet<int>(() => (int)(short)_driver!.BinY, 1))
            .GetAwaiter().GetResult();

    public int BitDepth => _bitDepth;
    public int MaxX => _maxX;
    public int MaxY => _maxY;
    public double PixelSizeX => _pixelSizeX;
    public double PixelSizeY => _pixelSizeY;

    public int Gain => _driver == null || !_hasGain ? 0
        : _disp.Invoke<int>(() => SafeGet<int>(() => (int)(short)_driver!.Gain))
            .GetAwaiter().GetResult();

    public IReadOnlyList<int> IsoOptions => Array.Empty<int>();
    public int SelectedIso => 0;

    public CameraCapabilities Capabilities => new(
        SupportsCooler: _canCool,
        SupportsBinning: _canBin,
        SupportsRoi: _canRoi,
        SupportsIso: false,
        SupportsBulb: false);

    // ---- Lifecycle ----

    public Task ConnectAsync(CancellationToken ct = default) => _disp.Invoke(() => {
        // Late-bound activation. GetTypeFromProgID returns null when
        // the ProgID isn't registered (driver uninstalled OR wrong
        // bitness). Activator.CreateInstance throws COMException with
        // a clear HRESULT when the COM server can't be launched —
        // surface that to the caller verbatim so the toast shows the
        // real reason.
        var t = Type.GetTypeFromProgID(_progId)
            ?? throw new InvalidOperationException(
                $"ASCOM driver '{_progId}' is not registered. " +
                "Install or re-register the driver via the ASCOM Platform.");
        _driver = Activator.CreateInstance(t)
            ?? throw new InvalidOperationException(
                $"ASCOM driver '{_progId}' failed to instantiate.");
        _driver!.Connected = true;
        try { _deviceName = (string)_driver.Name; } catch { _deviceName = _progId; }
        // Cache the immutable sensor / capability metadata so the
        // status loop never round-trips into the driver for these.
        _maxX = SafeGet<int>(() => (int)_driver.CameraXSize);
        _maxY = SafeGet<int>(() => (int)_driver.CameraYSize);
        _pixelSizeX = SafeGet<double>(() => (double)_driver.PixelSizeX);
        _pixelSizeY = SafeGet<double>(() => (double)_driver.PixelSizeY);
        var maxAdu = SafeGet<long>(() => (long)(int)_driver.MaxADU, 65535);
        _bitDepth = maxAdu switch {
            > 65535 => 32, > 16383 => 16, > 4095 => 14, > 1023 => 12, _ => 10
        };
        _canCool = SafeGet<bool>(() => (bool)_driver.CanSetCCDTemperature);
        var maxBin = SafeGet<int>(() => (int)(short)_driver.MaxBinX, 1);
        _canBin = maxBin > 1;
        _canAbort = SafeGet<bool>(() => (bool)_driver.CanAbortExposure);
        // Subframe always available per ICameraV3 spec.
        _canRoi = true;
        // Some drivers throw PropertyNotImplementedException on Gain
        // when they don't support it (mono CCDs); some return 0. The
        // safer probe is the GainMin / GainMax pair: if both succeed
        // we expose gain to the UI.
        _hasGain = false;
        try {
            _gainMin = (int)(short)_driver.GainMin;
            _gainMax = (int)(short)_driver.GainMax;
            _hasGain = _gainMax > _gainMin;
        } catch { _hasGain = false; }
    });

    public Task DisconnectAsync(CancellationToken ct = default) => _disp.Invoke(() => {
        if (_driver == null) return;
        try { _driver.Connected = false; } catch { /* driver already gone */ }
        try { Marshal.FinalReleaseComObject(_driver); } catch { }
        _driver = null;
    });

    // ---- Capture ----

    public Task<IImageData> CaptureAsync(double exposureSeconds, CaptureOptions? opts = null,
                                          CancellationToken ct = default)
        => _disp.Invoke<IImageData>(() => {
            if (_driver == null)
                throw new InvalidOperationException("ASCOM camera not connected.");

            // Per-capture overrides. Binning first because some
            // drivers reset StartX/NumX when binning changes.
            if (opts != null) {
                if (opts.BinX is int bx && bx >= 1 && _canBin) {
                    SafeSet(() => _driver!.BinX = (short)bx);
                    SafeSet(() => _driver!.BinY = (short)(opts.BinY ?? bx));
                }
                if (opts.Gain is int g && _hasGain) {
                    SafeSet(() => _driver!.Gain = (short)Math.Clamp(g, _gainMin, _gainMax));
                }
            }

            var isLight = !string.Equals(opts?.ImageType, "DARK", StringComparison.OrdinalIgnoreCase)
                       && !string.Equals(opts?.ImageType, "BIAS", StringComparison.OrdinalIgnoreCase);
            _driver!.StartExposure(exposureSeconds, isLight);

            // Poll until ImageReady. Most drivers flip the flag
            // within ~100 ms of the exposure ending, the 50 ms tick
            // is responsive without burning CPU. Abort on
            // cancellation by calling AbortExposure if the driver
            // supports it (otherwise the next StartExposure will
            // overwrite the buffer anyway).
            var deadline = DateTime.UtcNow.AddSeconds(exposureSeconds + 120);
            while (true) {
                if (ct.IsCancellationRequested) {
                    if (_canAbort) { try { _driver.AbortExposure(); } catch { } }
                    ct.ThrowIfCancellationRequested();
                }
                if (SafeGet<bool>(() => (bool)_driver.ImageReady)) break;
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException(
                        $"ASCOM ImageReady didn't go true within {exposureSeconds + 120} s.");
                Thread.Sleep(50);
            }

            // ICameraV3.ImageArray returns an object that is a 2-D
            // SAFEARRAY of int (mono) or 3-D of int (colour). We
            // only support mono in this adapter for v1, colour
            // sensors return a 3-D array we'd have to debayer
            // ourselves, the existing IndiCamera path already does
            // that and most users with colour sensors are on Alpaca
            // / native drivers anyway.
            var raw = _driver.ImageArray;
            var arr = (Array)raw;
            int width = arr.GetLength(0);
            int height = arr.GetLength(1);
            var px = new ushort[width * height];
            // Row-major write so Polaris's downstream pipeline
            // (StarDetector, ImageWriter, etc.) reads consecutive
            // pixels in the order they expect.
            for (int y = 0; y < height; y++) {
                for (int x = 0; x < width; x++) {
                    var v = Convert.ToInt32(arr.GetValue(x, y));
                    if (v < 0) v = 0;
                    if (v > 65535) v = 65535;
                    px[y * width + x] = (ushort)v;
                }
            }
            try { Marshal.ReleaseComObject(raw); } catch { }

            var props = new ImageProperties {
                Width = width,
                Height = height,
                BitDepth = _bitDepth
            };
            var meta = new ImageMetaData {
                Camera = new ImageMetaData.CameraInfo {
                    Name = _deviceName,
                    PixelSizeX = _pixelSizeX,
                    PixelSizeY = _pixelSizeY,
                    Gain = _hasGain ? Gain : 0
                },
                Exposure = new ImageMetaData.ExposureInfo {
                    ExposureTime = exposureSeconds,
                    Filter = opts?.Filter,
                    ImageType = opts?.ImageType ?? "LIGHT"
                },
                Target = string.IsNullOrEmpty(opts?.TargetName)
                    ? new ImageMetaData.TargetInfo()
                    : new ImageMetaData.TargetInfo { Name = opts.TargetName }
            };
            return (IImageData)new BaseImageData(px, props, meta);
        });

    public Task SetBinningAsync(int binX, int binY, CancellationToken ct = default)
        => _disp.Invoke(() => {
            if (!_canBin || _driver == null) return;
            SafeSet(() => _driver!.BinX = (short)Math.Max(1, binX));
            SafeSet(() => _driver!.BinY = (short)Math.Max(1, binY));
        });

    public Task SetTemperatureAsync(double temperature, CancellationToken ct = default)
        => _disp.Invoke(() => {
            if (!_canCool || _driver == null) return;
            SafeSet(() => _driver!.SetCCDTemperature = temperature);
            SafeSet(() => _driver!.CoolerOn = true);
        });

    public Task SetCoolerAsync(bool on, CancellationToken ct = default)
        => _disp.Invoke(() => {
            if (!_canCool || _driver == null) return;
            SafeSet(() => _driver!.CoolerOn = on);
        });

    public Task SetIsoAsync(int iso, CancellationToken ct = default) => Task.CompletedTask;

    public Task AbortExposureAsync(CancellationToken ct = default) => _disp.Invoke(() => {
        if (!_canAbort || _driver == null) return;
        try { _driver.AbortExposure(); } catch { /* state-dependent */ }
    });

    public Task SetSubframeAsync(int x, int y, int width, int height,
                                  CancellationToken ct = default) => _disp.Invoke(() => {
        if (_driver == null) return;
        // w=0 OR h=0 = clear ROI per the ICamera contract.
        if (width <= 0 || height <= 0) {
            SafeSet(() => _driver!.StartX = 0);
            SafeSet(() => _driver!.StartY = 0);
            SafeSet(() => _driver!.NumX = _maxX);
            SafeSet(() => _driver!.NumY = _maxY);
            return;
        }
        SafeSet(() => _driver!.StartX = x);
        SafeSet(() => _driver!.StartY = y);
        SafeSet(() => _driver!.NumX = width);
        SafeSet(() => _driver!.NumY = height);
    });

    public void Dispose() {
        try { DisconnectAsync().GetAwaiter().GetResult(); } catch { }
        _disp.Dispose();
    }

    // ---- COM-safe helpers ----
    //
    // ASCOM drivers throw PropertyNotImplementedException + various
    // COMException variants when a property they advertise as optional
    // is read on a model that doesn't support it (e.g. CoolerPower on
    // a cooled camera with no power-readout sensor). We swallow those
    // and return a fallback rather than tear down the whole adapter.
    private static T SafeGet<T>(Func<T> read, T fallback = default!) {
        try { return read(); } catch { return fallback; }
    }
    private static void SafeSet(Action write) {
        try { write(); } catch { /* property not implemented */ }
    }
}
