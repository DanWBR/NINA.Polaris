// N.I.N.A. Polaris
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT
// ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or
// FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License
// for more details. You should have received a copy of the license along with
// this program. If not, see <https://www.gnu.org/licenses/>.

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using NINA.Camera.SvbonySdk.Native;
using NINA.Core.Enum;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using static NINA.Camera.SvbonySdk.Native.SvbonyNative;

namespace NINA.Camera.SvbonySdk;

/// <summary>
/// <see cref="ICamera"/> backend that talks to a SVBony camera through its
/// native USB SDK, bypassing the INDI transport. The point is high-fps
/// planetary video: native streaming runs a dedicated pull thread
/// (SVBStartVideoCapture → loop SVBGetVideoData → fan out) so CameraStream-
/// Service gets the native path instead of the slow per-exposure INDI loop.
/// Still capture, gain, cooler and ROI all go through the SDK too, so the
/// rig can select this as its camera entirely.
/// </summary>
public sealed class SvbonySdkCamera : ICamera {
    private readonly int _cameraId;
    private bool _connected;

    private int _maxX, _maxY, _maxBitDepth = 16;
    private double _pixelSize;
    private BayerPatternEnum _bayer = BayerPatternEnum.None;
    private bool _isColor;
    private int _gainMin, _gainMax;
    private int _offset;
    private bool _supportsCooler;
    private bool _isTriggerCam;

    private int _gain;
    private double _exposureSec = 0.03;
    private int _roiX, _roiY, _roiW, _roiH, _bin = 1;
    private SVB_IMG_TYPE _imgType = SVB_IMG_TYPE.SVB_IMG_RAW16;

    private readonly ConcurrentDictionary<int, Action<IImageData>> _streamSubs = new();
    private int _nextSubId;
    private volatile bool _streaming;
    private Thread? _streamThread;
    private CancellationTokenSource? _streamCts;
    private readonly object _gate = new();

    public SvbonySdkCamera(string deviceId) {
        SvbonyRegistry.EnsureResolver();
        _cameraId = int.TryParse(deviceId, out var id) ? id : 0;
        DeviceName = $"SVBony #{_cameraId}";
    }

    public string DeviceName { get; private set; }
    public bool IsConnected => _connected;
    public CameraStates State { get; private set; } = CameraStates.NoState;

    public double Temperature => _connected ? ReadControl(SVB_CONTROL_TYPE.SVB_CURRENT_TEMPERATURE) / 10.0 : double.NaN;
    public bool CoolerOn => _connected && _supportsCooler && ReadControl(SVB_CONTROL_TYPE.SVB_COOLER_ENABLE) != 0;
    public double CoolerPower => _connected && _supportsCooler ? ReadControl(SVB_CONTROL_TYPE.SVB_COOLER_POWER) : 0;
    public int BinX => _bin;
    public int BinY => _bin;
    public int BitDepth => _maxBitDepth > 8 ? 16 : 8;
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

    // ----- connect / disconnect -----

    public Task ConnectAsync(CancellationToken ct = default) => Task.Run(() => {
        var openErr = SVBOpenCamera(_cameraId);
        if (openErr != SVB_ERROR_CODE.SVB_SUCCESS)
            throw new InvalidOperationException(
                $"Failed to open the SVBony camera (SVBOpenCamera: {openErr}). " +
                "The camera may already be in use by another process. If an INDI " +
                "driver for this camera is connected, disconnect it (or remove it " +
                "from the running indiserver profile) before using the native SDK backend.");

        // SVBGetCameraInfo takes an enumeration INDEX, not the CameraID, so we
        // can't pass _cameraId here (for the SV405CC the CameraID is 1 while
        // the index is 0, which made the lookup miss and the name fall back to
        // "SVBony #1"). Match by CameraID across indices, like the ZWO backend.
        int nCams = SVBGetNumOfConnectedCameras();
        for (int i = 0; i < nCams; i++) {
            var probe = new SVB_CAMERA_INFO();
            if (SVBGetCameraInfo(ref probe, i) == SVB_ERROR_CODE.SVB_SUCCESS
                    && probe.CameraID == _cameraId
                    && !string.IsNullOrWhiteSpace(probe.FriendlyName)) {
                DeviceName = probe.FriendlyName;
                break;
            }
        }

        var prop = new SVB_CAMERA_PROPERTY();
        Check(SVBGetCameraProperty(_cameraId, ref prop), "SVBGetCameraProperty");
        _maxX = (int)prop.MaxWidth.Value;
        _maxY = (int)prop.MaxHeight.Value;
        _maxBitDepth = prop.MaxBitDepth;
        _isColor = prop.IsColorCam != 0;
        _isTriggerCam = prop.IsTriggerCam != 0;
        _bayer = _isColor ? MapBayer((SVB_BAYER_PATTERN)prop.BayerPattern) : BayerPatternEnum.None;

        // Gain range + cooler support from the control caps table.
        if (SVBGetNumOfControls(_cameraId, out var nCtrl) == SVB_ERROR_CODE.SVB_SUCCESS) {
            for (int i = 0; i < nCtrl; i++) {
                var caps = new SVB_CONTROL_CAPS();
                if (SVBGetControlCaps(_cameraId, i, ref caps) != SVB_ERROR_CODE.SVB_SUCCESS) continue;
                switch ((SVB_CONTROL_TYPE)caps.ControlType) {
                    case SVB_CONTROL_TYPE.SVB_GAIN:
                        _gainMin = (int)caps.MinValue.Value;
                        _gainMax = (int)caps.MaxValue.Value;
                        break;
                    case SVB_CONTROL_TYPE.SVB_COOLER_ENABLE:
                        _supportsCooler = true;
                        break;
                }
            }
        }

        if (SVBGetSensorPixelSize(_cameraId, out var px) == SVB_ERROR_CODE.SVB_SUCCESS) _pixelSize = px;

        _imgType = _maxBitDepth > 8 ? SVB_IMG_TYPE.SVB_IMG_RAW16 : SVB_IMG_TYPE.SVB_IMG_RAW8;
        SVBSetCameraMode(_cameraId, SVB_CAMERA_MODE.SVB_MODE_NORMAL);
        SVBSetOutputImageType(_cameraId, _imgType);

        _roiX = 0; _roiY = 0; _roiW = _maxX; _roiH = _maxY; _bin = 1;
        SVBSetROIFormat(_cameraId, 0, 0, _maxX, _maxY, 1);

        _gain = ReadControl(SVB_CONTROL_TYPE.SVB_GAIN);
        // SVBony exposes the sensor bias pedestal ("offset") as the black-level
        // control.
        _offset = ReadControl(SVB_CONTROL_TYPE.SVB_BLACK_LEVEL);
        _connected = true;
        State = CameraStates.Idle;
    }, ct);

    public Task DisconnectAsync(CancellationToken ct = default) => Task.Run(() => {
        try { StopStreamCore(); } catch { }
        if (_connected) { try { SVBCloseCamera(_cameraId); } catch { } }
        _connected = false;
        State = CameraStates.NoState;
    }, ct);

    // ----- controls -----

    public Task SetBinningAsync(int binX, int binY, CancellationToken ct = default) {
        _bin = Math.Max(1, binX);
        ApplyRoi();
        return Task.CompletedTask;
    }

    public Task SetTemperatureAsync(double temperature, CancellationToken ct = default) {
        if (_supportsCooler)
            SVBSetControlValue(_cameraId, SVB_CONTROL_TYPE.SVB_TARGET_TEMPERATURE,
                new CLong((nint)Math.Round(temperature * 10)), 0);
        return Task.CompletedTask;
    }

    public Task SetCoolerAsync(bool on, CancellationToken ct = default) {
        if (_supportsCooler)
            SVBSetControlValue(_cameraId, SVB_CONTROL_TYPE.SVB_COOLER_ENABLE, new CLong(on ? 1 : 0), 0);
        return Task.CompletedTask;
    }

    public Task SetIsoAsync(int iso, CancellationToken ct = default) => Task.CompletedTask;

    public Task AbortExposureAsync(CancellationToken ct = default) {
        try { SVBStopVideoCapture(_cameraId); } catch { }
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
        // SVB wants output (post-bin) dims rounded to multiples of 8.
        int w = Math.Max(8, (_roiW / _bin) & ~7);
        int h = Math.Max(2, (_roiH / _bin) & ~1);
        SVBSetROIFormat(_cameraId, _roiX, _roiY, w, h, _bin);
    }

    // ----- still capture -----

    public Task<IImageData> CaptureAsync(double exposureSeconds, CaptureOptions? opts = null,
                                         CancellationToken ct = default) => Task.Run<IImageData>(() => {
        lock (_gate) {
            if (_streaming) throw new InvalidOperationException(
                "Stop the video stream before taking a still exposure.");
            ApplyExposureGain(exposureSeconds, opts?.Gain, opts?.Offset);
            SVBSetOutputImageType(_cameraId, _imgType);

            GetRoi(out var w, out var h);
            var bytes = new byte[(long)w * h * BytesPerPixel()];
            // Generous upper bound: the requested integration + readout/USB
            // margin. It's only the max wait, not the actual exposure time.
            int waitMs = (int)(exposureSeconds * 1000) + 8000;

            State = CameraStates.Exposing;
            if (_isTriggerCam) {
                // Soft-trigger mode = exact on-demand exposure. In the NORMAL
                // (continuous) video mode the SDK hands back whatever frame is
                // already in flight, so long stills came back early (~the
                // camera's running video frame time, a few seconds) instead of
                // integrating for the requested duration. Switching to
                // SVB_MODE_TRIG_SOFT and firing one SVBSendSoftTrigger makes
                // SVBGetVideoData block until a single full-length exposure
                // completes. Mode is restored to NORMAL afterwards so the
                // video-stream path keeps working.
                Check(SVBSetCameraMode(_cameraId, SVB_CAMERA_MODE.SVB_MODE_TRIG_SOFT),
                    "SVBSetCameraMode(TRIG_SOFT)");
                Check(SVBStartVideoCapture(_cameraId), "SVBStartVideoCapture");
                try {
                    // Let the mode + exposure value latch before triggering.
                    Thread.Sleep(20);
                    Check(SVBSendSoftTrigger(_cameraId), "SVBSendSoftTrigger");
                    var err = SVBGetVideoData(_cameraId, bytes, new CLong(bytes.Length), waitMs);
                    Check(err, "SVBGetVideoData");
                } finally {
                    try { SVBStopVideoCapture(_cameraId); } catch { }
                    try { SVBSetCameraMode(_cameraId, SVB_CAMERA_MODE.SVB_MODE_NORMAL); } catch { }
                    State = CameraStates.Idle;
                }
            } else {
                // Camera without trigger support: continuous mode. Best effort,
                // unchanged behaviour.
                Check(SVBStartVideoCapture(_cameraId), "SVBStartVideoCapture");
                try {
                    var err = SVBGetVideoData(_cameraId, bytes, new CLong(bytes.Length), waitMs);
                    Check(err, "SVBGetVideoData");
                } finally {
                    try { SVBStopVideoCapture(_cameraId); } catch { }
                    State = CameraStates.Idle;
                }
            }
            return WrapFrame(bytes, w, h);
        }
    }, ct);

    // ----- native video streaming -----

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
                SVBSetOutputImageType(_cameraId, _imgType);

                _streamCts = new CancellationTokenSource();
                _streaming = true;
                State = CameraStates.Exposing;
                Check(SVBStartVideoCapture(_cameraId), "SVBStartVideoCapture");
                _streamThread = new Thread(() => PullLoop(_streamCts.Token)) {
                    IsBackground = true, Name = "SVBony-stream"
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
            t = _streamThread;
            _streamThread = null;
        }
        try { t?.Join(2000); } catch { }
        try { SVBStopVideoCapture(_cameraId); } catch { }
        State = CameraStates.Idle;
    }

    private void PullLoop(CancellationToken ct) {
        GetRoi(out var w, out var h);
        var buf = new byte[(long)w * h * BytesPerPixel()];
        int waitMs = (int)(_exposureSec * 1000 * 2 + 500);
        while (!ct.IsCancellationRequested && _streaming) {
            var err = SVBGetVideoData(_cameraId, buf, new CLong(buf.Length), waitMs);
            if (err == SVB_ERROR_CODE.SVB_ERROR_TIMEOUT) continue;
            if (err != SVB_ERROR_CODE.SVB_SUCCESS) continue;
            IImageData frame;
            try { frame = WrapFrame(buf, w, h); } catch { continue; }
            foreach (var s in _streamSubs.Values) {
                try { s(frame); } catch { }
            }
        }
    }

    // ----- helpers -----

    private void ApplyExposureGain(double exposureSeconds, int? gainOverride, int? offsetOverride = null) {
        _exposureSec = exposureSeconds > 0 ? exposureSeconds : _exposureSec;
        SVBSetControlValue(_cameraId, SVB_CONTROL_TYPE.SVB_EXPOSURE,
            new CLong((nint)Math.Round(_exposureSec * 1_000_000)), 0); // microseconds
        if (gainOverride is int g) _gain = g;
        SVBSetControlValue(_cameraId, SVB_CONTROL_TYPE.SVB_GAIN, new CLong(_gain), 0);
        if (offsetOverride is int o) {
            _offset = o;
            SVBSetControlValue(_cameraId, SVB_CONTROL_TYPE.SVB_BLACK_LEVEL, new CLong(_offset), 0);
        }
    }

    private int BytesPerPixel() => _imgType == SVB_IMG_TYPE.SVB_IMG_RAW16 ? 2 : 1;

    private void GetRoi(out int w, out int h) {
        if (SVBGetROIFormat(_cameraId, out _, out _, out var rw, out var rh, out _) == SVB_ERROR_CODE.SVB_SUCCESS
                && rw > 0 && rh > 0) {
            w = rw; h = rh;
        } else {
            w = _roiW > 0 ? _roiW / _bin : _maxX;
            h = _roiH > 0 ? _roiH / _bin : _maxY;
        }
    }

    private IImageData WrapFrame(byte[] bytes, int w, int h) {
        var pixels = new ushort[(long)w * h];
        if (_imgType == SVB_IMG_TYPE.SVB_IMG_RAW16) {
            Buffer.BlockCopy(bytes, 0, pixels, 0, pixels.Length * 2);
        } else {
            // RAW8 → scale into the 16-bit range so the rest of the pipeline
            // (auto-stretch, stats) sees a consistent depth.
            for (int i = 0; i < pixels.Length; i++) pixels[i] = (ushort)(bytes[i] << 8);
        }
        var props = new ImageProperties {
            Width = w, Height = h,
            BitDepth = BitDepth,
            IsBayered = _bayer != BayerPatternEnum.None,
            BayerPattern = _bayer,
        };
        var meta = new ImageMetaData();
        meta.Camera.Name = DeviceName;
        meta.Camera.Gain = _gain;
        meta.Camera.Offset = _offset;
        meta.Camera.PixelSizeX = _pixelSize;
        meta.Camera.PixelSizeY = _pixelSize;
        return new BaseImageData(pixels, props, meta);
    }

    private int ReadControl(SVB_CONTROL_TYPE t) {
        try {
            if (SVBGetControlValue(_cameraId, t, out var v, out _) == SVB_ERROR_CODE.SVB_SUCCESS)
                return (int)v.Value;
        } catch { }
        return 0;
    }

    private static BayerPatternEnum MapBayer(SVB_BAYER_PATTERN p) => p switch {
        SVB_BAYER_PATTERN.SVB_BAYER_RG => BayerPatternEnum.RGGB,
        SVB_BAYER_PATTERN.SVB_BAYER_BG => BayerPatternEnum.BGGR,
        SVB_BAYER_PATTERN.SVB_BAYER_GR => BayerPatternEnum.GRBG,
        SVB_BAYER_PATTERN.SVB_BAYER_GB => BayerPatternEnum.GBRG,
        _ => BayerPatternEnum.None
    };

    private static void Check(SVB_ERROR_CODE err, string op) {
        if (err != SVB_ERROR_CODE.SVB_SUCCESS)
            throw new InvalidOperationException($"SVBony {op} failed: {err}");
    }

    private sealed class Sub : IDisposable {
        private readonly SvbonySdkCamera _cam;
        private readonly int _id;
        public Sub(SvbonySdkCamera cam, int id) { _cam = cam; _id = id; }
        public void Dispose() => _cam._streamSubs.TryRemove(_id, out _);
    }
}