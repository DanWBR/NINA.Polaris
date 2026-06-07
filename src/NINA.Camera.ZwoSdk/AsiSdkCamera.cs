using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using NINA.Camera.ZwoSdk.Native;
using NINA.Core.Enum;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using static NINA.Camera.ZwoSdk.Native.AsiNative;

namespace NINA.Camera.ZwoSdk;

/// <summary>
/// <see cref="ICamera"/> backend for ZWO ASI cameras over the native
/// ASICamera2 SDK. Same shape as the SVBony backend: full still capture +
/// cooler + ROI, plus native video streaming (dedicated pull thread looping
/// ASIGetVideoData) so CameraStreamService gets the native path. This is the
/// high-fps path for ZWO planetary cams (e.g. ASI462/678) which is exactly
/// where 100 fps becomes attainable.
/// </summary>
public sealed class AsiSdkCamera : ICamera {
    private readonly int _cameraId;
    private bool _connected;

    private int _maxX, _maxY, _bitDepth = 16;
    private double _pixelSize;
    private BayerPatternEnum _bayer = BayerPatternEnum.None;
    private bool _isColor, _supportsCooler;
    private int _gainMin, _gainMax;

    private int _gain;
    private double _exposureSec = 0.03;
    private int _roiX, _roiY, _roiW, _roiH, _bin = 1;
    private ASI_IMG_TYPE _imgType = ASI_IMG_TYPE.ASI_IMG_RAW16;

    private readonly ConcurrentDictionary<int, Action<IImageData>> _streamSubs = new();
    private int _nextSubId;
    private volatile bool _streaming;
    private Thread? _streamThread;
    private CancellationTokenSource? _streamCts;
    private readonly object _gate = new();

    public AsiSdkCamera(string deviceId) {
        ZwoRegistry.EnsureResolver();
        _cameraId = int.TryParse(deviceId, out var id) ? id : 0;
        DeviceName = $"ASI #{_cameraId}";
    }

    public string DeviceName { get; private set; }
    public bool IsConnected => _connected;
    public CameraStates State { get; private set; } = CameraStates.NoState;

    public double Temperature => _connected ? ReadControl(ASI_CONTROL_TYPE.ASI_TEMPERATURE) / 10.0 : double.NaN;
    public bool CoolerOn => _connected && _supportsCooler && ReadControl(ASI_CONTROL_TYPE.ASI_COOLER_ON) != 0;
    public double CoolerPower => _connected && _supportsCooler ? ReadControl(ASI_CONTROL_TYPE.ASI_COOLER_POWER_PERC) : 0;
    public int BinX => _bin;
    public int BinY => _bin;
    public int BitDepth => _bitDepth > 8 ? 16 : 8;
    public int MaxX => _maxX;
    public int MaxY => _maxY;
    public double PixelSizeX => _pixelSize;
    public double PixelSizeY => _pixelSize;
    public int Gain => _gain;
    public int GainMin => _gainMin;
    public int GainMax => _gainMax;
    public IReadOnlyList<int> IsoOptions { get; } = Array.Empty<int>();
    public int SelectedIso => 0;

    public CameraCapabilities Capabilities => new(
        SupportsCooler: _supportsCooler,
        SupportsBinning: true,
        SupportsRoi: true,
        SupportsIso: false,
        SupportsBulb: false,
        SupportsVideoStream: true,
        SupportsWhiteBalance: false);

    public Task ConnectAsync(CancellationToken ct = default) => Task.Run(() => {
        // Read the matching camera info (by CameraID) before opening.
        int n = ASIGetNumOfConnectedCameras();
        var info = new ASI_CAMERA_INFO();
        bool found = false;
        for (int i = 0; i < n; i++) {
            var probe = new ASI_CAMERA_INFO();
            if (ASIGetCameraProperty(ref probe, i) == ASI_ERROR_CODE.ASI_SUCCESS && probe.CameraID == _cameraId) {
                info = probe; found = true; break;
            }
        }
        if (!found) throw new InvalidOperationException($"ASI camera id {_cameraId} not found.");

        var openErr = ASIOpenCamera(_cameraId);
        if (openErr != ASI_ERROR_CODE.ASI_SUCCESS)
            throw new InvalidOperationException(
                $"Failed to open the ZWO ASI camera (ASIOpenCamera: {openErr}). " +
                "The camera may already be in use by another process. If an INDI " +
                "driver for this camera is connected, disconnect it (or remove it " +
                "from the running indiserver profile) before using the native SDK backend.");
        Check(ASIInitCamera(_cameraId), "ASIInitCamera");

        if (!string.IsNullOrWhiteSpace(info.Name)) DeviceName = info.Name;
        _maxX = (int)info.MaxWidth.Value;
        _maxY = (int)info.MaxHeight.Value;
        _bitDepth = info.BitDepth;
        _isColor = info.IsColorCam != 0;
        _bayer = _isColor ? MapBayer((ASI_BAYER_PATTERN)info.BayerPattern) : BayerPatternEnum.None;
        _pixelSize = info.PixelSize;
        _supportsCooler = info.IsCoolerCam != 0;

        if (ASIGetNumOfControls(_cameraId, out var nCtrl) == ASI_ERROR_CODE.ASI_SUCCESS) {
            for (int i = 0; i < nCtrl; i++) {
                var caps = new ASI_CONTROL_CAPS();
                if (ASIGetControlCaps(_cameraId, i, ref caps) != ASI_ERROR_CODE.ASI_SUCCESS) continue;
                if ((ASI_CONTROL_TYPE)caps.ControlType == ASI_CONTROL_TYPE.ASI_GAIN) {
                    _gainMin = (int)caps.MinValue.Value;
                    _gainMax = (int)caps.MaxValue.Value;
                }
            }
        }

        _imgType = _bitDepth > 8 ? ASI_IMG_TYPE.ASI_IMG_RAW16 : ASI_IMG_TYPE.ASI_IMG_RAW8;
        _roiX = 0; _roiY = 0; _roiW = _maxX; _roiH = _maxY; _bin = 1;
        ASISetROIFormat(_cameraId, _maxX, _maxY, 1, _imgType);
        ASISetStartPos(_cameraId, 0, 0);
        _gain = ReadControl(ASI_CONTROL_TYPE.ASI_GAIN);
        _connected = true;
        State = CameraStates.Idle;
    }, ct);

    public Task DisconnectAsync(CancellationToken ct = default) => Task.Run(() => {
        try { StopStreamCore(); } catch { }
        if (_connected) { try { ASICloseCamera(_cameraId); } catch { } }
        _connected = false;
        State = CameraStates.NoState;
    }, ct);

    public Task SetBinningAsync(int binX, int binY, CancellationToken ct = default) {
        _bin = Math.Max(1, binX); ApplyRoi(); return Task.CompletedTask;
    }

    public Task SetTemperatureAsync(double temperature, CancellationToken ct = default) {
        if (_supportsCooler)
            ASISetControlValue(_cameraId, ASI_CONTROL_TYPE.ASI_TARGET_TEMP, new CLong((nint)Math.Round(temperature)), 0);
        return Task.CompletedTask;
    }

    public Task SetCoolerAsync(bool on, CancellationToken ct = default) {
        if (_supportsCooler)
            ASISetControlValue(_cameraId, ASI_CONTROL_TYPE.ASI_COOLER_ON, new CLong(on ? 1 : 0), 0);
        return Task.CompletedTask;
    }

    public Task SetIsoAsync(int iso, CancellationToken ct = default) => Task.CompletedTask;

    public Task AbortExposureAsync(CancellationToken ct = default) {
        try { ASIStopVideoCapture(_cameraId); } catch { }
        State = CameraStates.Idle;
        return Task.CompletedTask;
    }

    public Task SetSubframeAsync(int x, int y, int width, int height, CancellationToken ct = default) {
        if (width <= 0 || height <= 0) { _roiX = 0; _roiY = 0; _roiW = _maxX; _roiH = _maxY; }
        else { _roiX = x; _roiY = y; _roiW = width; _roiH = height; }
        ApplyRoi();
        return Task.CompletedTask;
    }

    private void ApplyRoi() {
        if (!_connected) return;
        int w = Math.Max(8, (_roiW / _bin) & ~7); // ASI requires width % 8 == 0
        int h = Math.Max(2, (_roiH / _bin) & ~1); // and height % 2 == 0
        ASISetROIFormat(_cameraId, w, h, _bin, _imgType);
        ASISetStartPos(_cameraId, _roiX / _bin, _roiY / _bin);
    }

    public Task<IImageData> CaptureAsync(double exposureSeconds, CaptureOptions? opts = null,
                                         CancellationToken ct = default) => Task.Run<IImageData>(() => {
        lock (_gate) {
            if (_streaming) throw new InvalidOperationException("Stop the video stream before a still exposure.");
            ApplyExposureGain(exposureSeconds, opts?.Gain);
            GetRoi(out var w, out var h);
            var bytes = new byte[(long)w * h * BytesPerPixel()];
            int waitMs = (int)(exposureSeconds * 1000 * 2 + 500);
            State = CameraStates.Exposing;
            Check(ASIStartVideoCapture(_cameraId), "ASIStartVideoCapture");
            try {
                Check(ASIGetVideoData(_cameraId, bytes, new CLong(bytes.Length), waitMs), "ASIGetVideoData");
            } finally {
                try { ASIStopVideoCapture(_cameraId); } catch { }
                State = CameraStates.Idle;
            }
            return WrapFrame(bytes, w, h);
        }
    }, ct);

    public bool IsStreaming => _streaming;

    public IDisposable SubscribeVideoFrames(Action<IImageData> handler) {
        var id = Interlocked.Increment(ref _nextSubId);
        _streamSubs[id] = handler;
        return new Sub(this, id);
    }

    public Task StartVideoStreamAsync(VideoStreamOptions? opts = null, CancellationToken ct = default)
        => Task.Run(() => {
            lock (_gate) {
                if (_streaming) return;
                ApplyExposureGain(opts?.ExposureSeconds ?? _exposureSec, opts?.Gain);
                if (opts?.BinX is int b && b != _bin) { _bin = Math.Max(1, b); ApplyRoi(); }
                _streamCts = new CancellationTokenSource();
                _streaming = true;
                State = CameraStates.Exposing;
                Check(ASIStartVideoCapture(_cameraId), "ASIStartVideoCapture");
                _streamThread = new Thread(() => PullLoop(_streamCts.Token)) {
                    IsBackground = true, Name = "ASI-stream"
                };
                _streamThread.Start();
            }
        }, ct);

    public Task StopVideoStreamAsync(CancellationToken ct = default) => Task.Run(StopStreamCore, ct);

    private void StopStreamCore() {
        Thread? t;
        lock (_gate) {
            if (!_streaming) return;
            _streaming = false;
            _streamCts?.Cancel();
            t = _streamThread; _streamThread = null;
        }
        try { t?.Join(2000); } catch { }
        try { ASIStopVideoCapture(_cameraId); } catch { }
        State = CameraStates.Idle;
    }

    private void PullLoop(CancellationToken ct) {
        GetRoi(out var w, out var h);
        var buf = new byte[(long)w * h * BytesPerPixel()];
        int waitMs = (int)(_exposureSec * 1000 * 2 + 500);
        while (!ct.IsCancellationRequested && _streaming) {
            var err = ASIGetVideoData(_cameraId, buf, new CLong(buf.Length), waitMs);
            if (err == ASI_ERROR_CODE.ASI_ERROR_TIMEOUT) continue;
            if (err != ASI_ERROR_CODE.ASI_SUCCESS) continue;
            IImageData frame;
            try { frame = WrapFrame(buf, w, h); } catch { continue; }
            foreach (var s in _streamSubs.Values) { try { s(frame); } catch { } }
        }
    }

    private void ApplyExposureGain(double exposureSeconds, int? gainOverride) {
        _exposureSec = exposureSeconds > 0 ? exposureSeconds : _exposureSec;
        ASISetControlValue(_cameraId, ASI_CONTROL_TYPE.ASI_EXPOSURE,
            new CLong((nint)Math.Round(_exposureSec * 1_000_000)), 0);
        if (gainOverride is int g) _gain = g;
        ASISetControlValue(_cameraId, ASI_CONTROL_TYPE.ASI_GAIN, new CLong(_gain), 0);
    }

    private int BytesPerPixel() => _imgType == ASI_IMG_TYPE.ASI_IMG_RAW16 ? 2 : 1;

    private void GetRoi(out int w, out int h) {
        if (ASIGetROIFormat(_cameraId, out var rw, out var rh, out _, out _) == ASI_ERROR_CODE.ASI_SUCCESS
                && rw > 0 && rh > 0) { w = rw; h = rh; }
        else { w = _roiW > 0 ? _roiW / _bin : _maxX; h = _roiH > 0 ? _roiH / _bin : _maxY; }
    }

    private IImageData WrapFrame(byte[] bytes, int w, int h) {
        var pixels = new ushort[(long)w * h];
        if (_imgType == ASI_IMG_TYPE.ASI_IMG_RAW16)
            Buffer.BlockCopy(bytes, 0, pixels, 0, pixels.Length * 2);
        else
            for (int i = 0; i < pixels.Length; i++) pixels[i] = (ushort)(bytes[i] << 8);
        var props = new ImageProperties {
            Width = w, Height = h, BitDepth = BitDepth,
            IsBayered = _bayer != BayerPatternEnum.None, BayerPattern = _bayer,
        };
        var meta = new ImageMetaData();
        meta.Camera.Name = DeviceName;
        meta.Camera.Gain = _gain;
        meta.Camera.PixelSizeX = _pixelSize;
        meta.Camera.PixelSizeY = _pixelSize;
        return new BaseImageData(pixels, props, meta);
    }

    private int ReadControl(ASI_CONTROL_TYPE t) {
        try {
            if (ASIGetControlValue(_cameraId, t, out var v, out _) == ASI_ERROR_CODE.ASI_SUCCESS)
                return (int)v.Value;
        } catch { }
        return 0;
    }

    private static BayerPatternEnum MapBayer(ASI_BAYER_PATTERN p) => p switch {
        ASI_BAYER_PATTERN.ASI_BAYER_RG => BayerPatternEnum.RGGB,
        ASI_BAYER_PATTERN.ASI_BAYER_BG => BayerPatternEnum.BGGR,
        ASI_BAYER_PATTERN.ASI_BAYER_GR => BayerPatternEnum.GRBG,
        ASI_BAYER_PATTERN.ASI_BAYER_GB => BayerPatternEnum.GBRG,
        _ => BayerPatternEnum.None
    };

    private static void Check(ASI_ERROR_CODE err, string op) {
        if (err != ASI_ERROR_CODE.ASI_SUCCESS)
            throw new InvalidOperationException($"ASI {op} failed: {err}");
    }

    private sealed class Sub : IDisposable {
        private readonly AsiSdkCamera _cam; private readonly int _id;
        public Sub(AsiSdkCamera cam, int id) { _cam = cam; _id = id; }
        public void Dispose() => _cam._streamSubs.TryRemove(_id, out _);
    }
}
