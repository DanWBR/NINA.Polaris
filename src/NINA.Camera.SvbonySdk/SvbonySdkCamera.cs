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
    private double? _minExpSec, _maxExpSec;
    private int _offset;
    private bool _supportsCooler;
    private bool _isTriggerCam;

    private int _gain;
    private double _exposureSec = 0.03;
    private int _roiX, _roiY, _roiW, _roiH, _bin = 1;
    // Last geometry actually written to the SDK, for ApplyRoi's idempotency
    // guard. _roiApplied stays false until the first real write (and is reset on
    // connect, where the camera comes up at its own default full-frame ROI).
    private bool _roiApplied;
    private int _lastRoiX, _lastRoiY, _lastRoiW, _lastRoiH, _lastRoiBin;
    private SVB_IMG_TYPE _imgType = SVB_IMG_TYPE.SVB_IMG_RAW16;

    private readonly ConcurrentDictionary<int, Action<IImageData>> _streamSubs = new();
    private int _nextSubId;
    private volatile bool _streaming;
    private Thread? _streamThread;
    private CancellationTokenSource? _streamCts;
    private readonly object _gate = new();
    // The SVBony SDK is NOT thread-safe per camera handle: a control read from
    // the WS status tick (Temperature/Cooler) concurrent with the pull thread's
    // SVBGetVideoData crashes the native lib a few seconds into a stream. This
    // lock serialises every individual SDK call so get/set/grab never overlap.
    // Held only for the duration of one native call (incl. the blocking
    // GetVideoData, which has its own short waitMs), never across the loop.
    private readonly object _sdk = new();

    public SvbonySdkCamera(string deviceId) {
        SvbonyRegistry.EnsureResolver();
        _cameraId = int.TryParse(deviceId, out var id) ? id : 0;
        DeviceName = $"SVBony #{_cameraId}";
    }

    public string DeviceName { get; private set; }
    public bool IsConnected => _connected;
    public CameraStates State { get; private set; } = CameraStates.NoState;

    // Cache the last valid reading so WrapFrame can stamp CCD-TEMP into the
    // FITS without an extra locked SDK read on every streamed frame (the WS
    // status tick refreshes this every ~2 s).
    private double _lastTempC = double.NaN;
    public double Temperature {
        get {
            var t = _connected ? ReadControl(SVB_CONTROL_TYPE.SVB_CURRENT_TEMPERATURE) / 10.0 : double.NaN;
            if (!double.IsNaN(t)) _lastTempC = t;
            return t;
        }
    }
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
    /// <summary>The SDK answers this outright, from SVB_CAMERA_PROPERTY.IsColorCam,
    /// so the live stacker never has to guess mono-vs-colour from an
    /// absent Bayer pattern.</summary>
    public bool? IsColorSensor => _connected ? _isColor : null;

    public int GainMin => _gainMin;
    public int GainMax => _gainMax;

    /// <summary>Exposure bounds from the SVB_EXPOSURE control caps, cached at
    /// connect (fixed per camera). null when the SDK didn't answer.</summary>
    public double? MinExposureSeconds => _connected ? _minExpSec : null;
    public double? MaxExposureSeconds => _connected ? _maxExpSec : null;

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

        // Gain range + exposure range + cooler support from the control caps table.
        _minExpSec = null; _maxExpSec = null;
        if (SVBGetNumOfControls(_cameraId, out var nCtrl) == SVB_ERROR_CODE.SVB_SUCCESS) {
            for (int i = 0; i < nCtrl; i++) {
                var caps = new SVB_CONTROL_CAPS();
                if (SVBGetControlCaps(_cameraId, i, ref caps) != SVB_ERROR_CODE.SVB_SUCCESS) continue;
                switch ((SVB_CONTROL_TYPE)caps.ControlType) {
                    case SVB_CONTROL_TYPE.SVB_GAIN:
                        _gainMin = (int)caps.MinValue.Value;
                        _gainMax = (int)caps.MaxValue.Value;
                        break;
                    case SVB_CONTROL_TYPE.SVB_EXPOSURE: {
                        // SVB_EXPOSURE caps are in MICROSECONDS.
                        long emin = (long)caps.MinValue.Value, emax = (long)caps.MaxValue.Value;
                        if (emin > 0 && emax > emin) {
                            _minExpSec = emin / 1_000_000.0;
                            _maxExpSec = emax / 1_000_000.0;
                        }
                        break;
                    }
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
        // Seed ApplyRoi's idempotency guard with what we just wrote, so the
        // usual "reset to full frame" calls right after connect are no-ops
        // instead of redundant SDK writes.
        _lastRoiX = 0; _lastRoiY = 0; _lastRoiW = _maxX; _lastRoiH = _maxY; _lastRoiBin = 1;
        _roiApplied = true;

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
            lock (_sdk)
                SVBSetControlValue(_cameraId, SVB_CONTROL_TYPE.SVB_TARGET_TEMPERATURE,
                    new CLong((nint)Math.Round(temperature * 10)), 0);
        return Task.CompletedTask;
    }

    public Task SetCoolerAsync(bool on, CancellationToken ct = default) {
        if (_supportsCooler)
            lock (_sdk)
                SVBSetControlValue(_cameraId, SVB_CONTROL_TYPE.SVB_COOLER_ENABLE, new CLong(on ? 1 : 0), 0);
        return Task.CompletedTask;
    }

    public Task SetIsoAsync(int iso, CancellationToken ct = default) => Task.CompletedTask;

    public Task AbortExposureAsync(CancellationToken ct = default) {
        lock (_sdk) { try { SVBStopVideoCapture(_cameraId); } catch { } }
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
        // SVBony forbids changing ROI/format DURING capture — doing so wedges
        // the driver. While a stream is running just stash the fields; they take
        // effect on the next StartVideoStreamAsync, which re-applies ROI while
        // stopped.
        if (_streaming) return;
        // SVB wants output (post-bin) dims rounded to multiples of 8.
        int w = Math.Max(8, (_roiW / _bin) & ~7);
        int h = Math.Max(2, (_roiH / _bin) & ~1);
        // Idempotency guard, mirroring the INDI side's CCD_FRAME guard: skip the
        // SDK write when the geometry is already what we asked for. Slew-and-
        // centre, plate solve and autofocus all call SetSubframeAsync(0,0,0,0)
        // around every capture — always the SAME full-frame geometry — so without
        // this each one was a redundant SVBSetROIFormat on a driver documented
        // (see above) to wedge on ROI churn. Those bursts are exactly when the
        // camera stopped responding in the field. This also closes most of the
        // mid-still-capture race: _streaming only covers the video stream, so a
        // SetSubframeAsync landing during a still used to write ROI mid-exposure;
        // now the common no-op case never touches the SDK at all.
        if (_roiApplied && _lastRoiX == _roiX && _lastRoiY == _roiY
                && _lastRoiW == w && _lastRoiH == h && _lastRoiBin == _bin) return;
        lock (_sdk) SVBSetROIFormat(_cameraId, _roiX, _roiY, w, h, _bin);
        _lastRoiX = _roiX; _lastRoiY = _roiY;
        _lastRoiW = w; _lastRoiH = h; _lastRoiBin = _bin;
        _roiApplied = true;
    }

    // ----- still capture -----

    public Task<IImageData> CaptureAsync(double exposureSeconds, CaptureOptions? opts = null,
                                         CancellationToken ct = default) => Task.Run<IImageData>(() => {
        lock (_gate) {
            if (_streaming) throw new InvalidOperationException(
                "Stop the video stream before taking a still exposure.");
            ApplyExposureGain(exposureSeconds, opts?.Gain, opts?.Offset);
            lock (_sdk) SVBSetOutputImageType(_cameraId, _imgType);

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
                // The mode switch + start MUST sit inside the try: if
                // SVBStartVideoCapture throws with them outside it, the finally
                // below never runs and the camera is left latched in TRIG_SOFT
                // forever — every later capture then waits on a soft trigger the
                // stream path never fires, so the camera looks dead until it is
                // physically re-enumerated. The finally is idempotent (both calls
                // already swallow), so running it after a failed start is safe.
                try {
                    lock (_sdk) {
                        Check(SVBSetCameraMode(_cameraId, SVB_CAMERA_MODE.SVB_MODE_TRIG_SOFT),
                            "SVBSetCameraMode(TRIG_SOFT)");
                        // Same defensive stop the stream path does (see
                        // StartVideoStreamAsync): if the PREVIOUS capture didn't
                        // stop cleanly the SDK still thinks it's capturing, and the
                        // next SVBStartVideoCapture wedges the driver. Stills churn
                        // a full start/stop per frame, so slew+solve / autofocus —
                        // which fire captures back-to-back — hit this far harder
                        // than video does (field: camera stops responding after a
                        // burst of solve/AF captures). A stop on an idle camera is
                        // a harmless no-op.
                        try { SVBStopVideoCapture(_cameraId); } catch { }
                        Check(SVBStartVideoCapture(_cameraId), "SVBStartVideoCapture");
                    }
                    // Re-apply the exposure AFTER the mode switch: some SVBony
                    // bodies reset the exposure control when the camera mode
                    // changes, which made the soft-triggered frame come back at
                    // a stale/short exposure instead of the requested time.
                    ApplyExposureGain(exposureSeconds, opts?.Gain, opts?.Offset);
                    // Let the mode + exposure value latch before triggering.
                    Thread.Sleep(20);
                    lock (_sdk) Check(SVBSendSoftTrigger(_cameraId), "SVBSendSoftTrigger");
                    SVB_ERROR_CODE err;
                    lock (_sdk) err = SVBGetVideoData(_cameraId, bytes, new CLong(bytes.Length), waitMs);
                    Check(err, "SVBGetVideoData");
                } finally {
                    lock (_sdk) {
                        try { SVBStopVideoCapture(_cameraId); } catch { }
                        try { SVBSetCameraMode(_cameraId, SVB_CAMERA_MODE.SVB_MODE_NORMAL); } catch { }
                    }
                    State = CameraStates.Idle;
                }
            } else {
                // Camera without soft-trigger support: continuous video mode.
                // The SDK hands back whatever frame is already in flight, which
                // started integrating BEFORE we set this exposure, so the first
                // frame comes back almost immediately (the running video
                // cadence) instead of after the requested time. That made every
                // AUTORUN dark return in a couple of seconds and the whole run
                // appear to finish "all at once". Discard frames that came back
                // materially sooner than the requested integration until we get
                // a full-length one (capped so an oddly-reporting camera can't
                // loop forever). Skipped for ~zero exposures (bias) where the
                // first frame is already correct.
                lock (_sdk) {
                    // Defensive stop before start — see the soft-trigger branch
                    // above and StartVideoStreamAsync: an unclean previous stop
                    // leaves the SDK "capturing" and the next start wedges it.
                    try { SVBStopVideoCapture(_cameraId); } catch { }
                    Check(SVBStartVideoCapture(_cameraId), "SVBStartVideoCapture");
                }
                try {
                    double minIntegrationMs = exposureSeconds * 1000.0 * 0.6;
                    for (int attempt = 1; ; attempt++) {
                        ct.ThrowIfCancellationRequested();
                        long t0 = Environment.TickCount64;
                        SVB_ERROR_CODE err;
                        lock (_sdk) err = SVBGetVideoData(_cameraId, bytes, new CLong(bytes.Length), waitMs);
                        Check(err, "SVBGetVideoData");
                        long elapsedMs = Environment.TickCount64 - t0;
                        if (exposureSeconds <= 0.05 || elapsedMs >= minIntegrationMs || attempt >= 3)
                            break;
                    }
                } finally {
                    lock (_sdk) { try { SVBStopVideoCapture(_cameraId); } catch { } }
                    State = CameraStates.Idle;
                }
            }
            return WrapFrame(bytes, w, h, exposureSeconds >= 0 ? exposureSeconds : _exposureSec);
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
                if (opts?.BinX is int b) _bin = Math.Max(1, b);
                ApplyExposureGain(opts?.ExposureSeconds ?? _exposureSec, opts?.Gain);
                // Re-apply ROI + output format while STOPPED (SVBony forbids
                // changing them during capture). This also picks up any ROI set
                // via SetSubframe while a previous stream was running (ApplyRoi
                // defers the SDK write during capture). _streaming is still false
                // here, so ApplyRoi runs.
                ApplyRoi();
                lock (_sdk) {
                    SVBSetOutputImageType(_cameraId, _imgType);
                    // Defensive: if a previous session didn't stop cleanly the SDK
                    // is still "capturing", and a second SVBStartVideoCapture then
                    // wedges the driver (field: "can't record two videos in a row
                    // without reconnecting"). A stop on an idle camera is a
                    // harmless no-op.
                    try { SVBStopVideoCapture(_cameraId); } catch { }
                    Check(SVBStartVideoCapture(_cameraId), "SVBStartVideoCapture");
                }
                _streamCts = new CancellationTokenSource();
                _streaming = true;
                State = CameraStates.Exposing;
                _streamThread = new Thread(() => PullLoop(_streamCts.Token)) {
                    IsBackground = true, Name = "SVBony-stream"
                };
                _streamThread.Start();
            }
        }, ct);

    public Task StopVideoStreamAsync(CancellationToken ct = default) => Task.Run(StopStreamCore, ct);

    /// <summary>Retune exposure/gain on the running stream WITHOUT a stop/start.
    /// Restarting SVBStartVideoCapture just to change exposure could hang the
    /// driver (field-reported "changed exposure → froze"); the SDK accepts
    /// SVBSetControlValue while capturing, so apply it live under the SDK lock.
    /// PullLoop recomputes its wait timeout from the new exposure each iteration.</summary>
    public Task<bool> UpdateVideoStreamAsync(VideoStreamOptions opts, CancellationToken ct = default)
        => Task.Run(() => {
            if (!_streaming) return false;
            ApplyExposureGain(opts?.ExposureSeconds ?? _exposureSec, opts?.Gain);
            return true;
        }, ct);

    private void StopStreamCore() {
        Thread? t;
        lock (_gate) {
            if (!_streaming) return;
            _streaming = false;
            _streamCts?.Cancel();
            t = _streamThread;
            _streamThread = null;
        }
        // Join the pull thread BEFORE touching the SDK so its in-flight
        // SVBGetVideoData (holding _sdk) has finished — then SVBStopVideoCapture
        // under _sdk can't run concurrently with it. Generous timeout so a
        // long-exposure GetVideoData can return first.
        try { t?.Join(5000); } catch { }
        lock (_sdk) { try { SVBStopVideoCapture(_cameraId); } catch { } }
        State = CameraStates.Idle;
    }

    private void PullLoop(CancellationToken ct) {
        GetRoi(out var w, out var h);
        var buf = new byte[(long)w * h * BytesPerPixel()];
        while (!ct.IsCancellationRequested && _streaming) {
            // Recompute the wait each iteration so a live exposure change
            // (UpdateVideoStreamAsync) doesn't leave the timeout stale and
            // starve longer exposures into perpetual timeouts.
            int waitMs = (int)(_exposureSec * 1000 * 2 + 500);
            SVB_ERROR_CODE err;
            lock (_sdk) err = SVBGetVideoData(_cameraId, buf, new CLong(buf.Length), waitMs);
            if (err == SVB_ERROR_CODE.SVB_ERROR_TIMEOUT) continue;
            if (err != SVB_ERROR_CODE.SVB_SUCCESS) continue;
            IImageData frame;
            try { frame = WrapFrame(buf, w, h, _exposureSec); } catch { continue; }
            foreach (var s in _streamSubs.Values) {
                try { s(frame); } catch { }
            }
        }
    }

    // ----- helpers -----

    private void ApplyExposureGain(double exposureSeconds, int? gainOverride, int? offsetOverride = null) {
        // Honor an explicit 0 (BIAS) by driving the hardware to its shortest
        // exposure, instead of keeping the previous value (a prior DARK's 60 s
        // would otherwise leak into a following bias frame). Keep the field at
        // the last POSITIVE value so the video-stream default isn't left at 0.
        double effExp = exposureSeconds >= 0 ? exposureSeconds : _exposureSec;
        if (exposureSeconds > 0) _exposureSec = exposureSeconds;
        long us = Math.Max(1, (long)Math.Round(effExp * 1_000_000)); // ≥1 µs
        if (gainOverride is int g) _gain = g;
        if (offsetOverride is int o) _offset = o;
        // Serialise the control writes against the streaming pull thread / status
        // reads (see _sdk note): live exposure/gain tuning during a stream would
        // otherwise race SVBGetVideoData.
        lock (_sdk) {
            SVBSetControlValue(_cameraId, SVB_CONTROL_TYPE.SVB_EXPOSURE, new CLong((nint)us), 0); // microseconds
            SVBSetControlValue(_cameraId, SVB_CONTROL_TYPE.SVB_GAIN, new CLong(_gain), 0);
            if (offsetOverride is int)
                SVBSetControlValue(_cameraId, SVB_CONTROL_TYPE.SVB_BLACK_LEVEL, new CLong(_offset), 0);
        }
    }

    private int BytesPerPixel() => _imgType == SVB_IMG_TYPE.SVB_IMG_RAW16 ? 2 : 1;

    private void GetRoi(out int w, out int h) {
        SVB_ERROR_CODE rc; int rw, rh;
        lock (_sdk) rc = SVBGetROIFormat(_cameraId, out _, out _, out rw, out rh, out _);
        if (rc == SVB_ERROR_CODE.SVB_SUCCESS && rw > 0 && rh > 0) {
            w = rw; h = rh;
        } else {
            w = _roiW > 0 ? _roiW / _bin : _maxX;
            h = _roiH > 0 ? _roiH / _bin : _maxY;
        }
    }

    private IImageData WrapFrame(byte[] bytes, int w, int h, double exposureSec) {
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
            // RAW16 delivers right-aligned raw ADC values; advertise the real
            // depth so the SER recorder left-aligns to the 16-bit container the
            // way planetary tools expect. RAW8 is already widened above.
            SignificantBitDepth = _imgType == SVB_IMG_TYPE.SVB_IMG_RAW16 ? _maxBitDepth : 0,
        };
        var meta = new ImageMetaData();
        meta.Camera.Name = DeviceName;
        meta.Camera.Gain = _gain;
        meta.Camera.Offset = _offset;
        // Stamp the integration time so the FITS/XISF writers emit EXPTIME /
        // EXPOSURE (and DATE-AVG). Without this, SVBony SDK frames saved with
        // no exposure value.
        meta.Exposure.ExposureTime = exposureSec;
        // Binning (XBINNING/YBINNING) + sensor temperature (CCD-TEMP) — both
        // essential for matching calibration frames (darks/flats) and were
        // otherwise absent from native-SDK FITS.
        meta.Camera.BinX = (short)_bin;
        meta.Camera.BinY = (short)_bin;
        if (!double.IsNaN(_lastTempC)) meta.Camera.Temperature = _lastTempC;
        meta.Camera.PixelSizeX = _pixelSize;
        meta.Camera.PixelSizeY = _pixelSize;
        // The FITS/XISF writers stamp BAYERPAT from meta.Camera.BayerPattern,
        // not props, so a colour OSC frame would otherwise save with no Bayer
        // info. Propagate the detected pattern here too.
        meta.Camera.BayerPattern = _bayer;
        return new BaseImageData(pixels, props, meta);
    }

    private int ReadControl(SVB_CONTROL_TYPE t) {
        try {
            // Serialise against the streaming pull thread (see _sdk note).
            lock (_sdk) {
                if (SVBGetControlValue(_cameraId, t, out var v, out _) == SVB_ERROR_CODE.SVB_SUCCESS)
                    return (int)v.Value;
            }
        } catch { }
        return 0;
    }

    // ----- Dynamic control panel (self-describing via SVBGetControlCaps) -----

    public IReadOnlyList<CameraControl> GetControls() {
        var list = new List<CameraControl>();
        if (!_connected) return list;
        try {
            lock (_sdk) {
                if (SVBGetNumOfControls(_cameraId, out var n) != SVB_ERROR_CODE.SVB_SUCCESS) return list;
                for (int i = 0; i < n; i++) {
                    var caps = new SVB_CONTROL_CAPS();
                    if (SVBGetControlCaps(_cameraId, i, ref caps) != SVB_ERROR_CODE.SVB_SUCCESS) continue;
                    var type = (SVB_CONTROL_TYPE)caps.ControlType;
                    double cur = 0; int isAuto = 0;
                    if (SVBGetControlValue(_cameraId, type, out var v, out var a) == SVB_ERROR_CODE.SVB_SUCCESS) {
                        cur = v.Value; isAuto = a;
                    }
                    double min = caps.MinValue.Value, max = caps.MaxValue.Value, def = caps.DefaultValue.Value;
                    string vt = (min == 0 && max == 1) ? "bool" : "int";
                    string name = string.IsNullOrWhiteSpace(caps.Name) ? type.ToString() : caps.Name.Trim();
                    list.Add(new CameraControl(type.ToString(), name, caps.Description?.Trim(),
                        cur, min, max, def, caps.IsWritable != 0, isAuto != 0, caps.IsAutoSupported != 0, vt));
                }
            }
        } catch { }
        return list;
    }

    public bool SetControl(string id, double value, bool auto) {
        if (!_connected || !Enum.TryParse<SVB_CONTROL_TYPE>(id, out var type)) return false;
        try {
            lock (_sdk) {
                return SVBSetControlValue(_cameraId, type,
                    new CLong((nint)(long)Math.Round(value)), auto ? 1 : 0) == SVB_ERROR_CODE.SVB_SUCCESS;
            }
        } catch { return false; }
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