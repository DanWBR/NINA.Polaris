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
    private double? _minExpSec, _maxExpSec;
    private int _offset;

    private int _gain;
    private double _exposureSec = 0.03;
    private int _roiX, _roiY, _roiW, _roiH, _bin = 1;
    // Last geometry actually written to the SDK, for ApplyRoi's idempotency
    // guard. Includes _imgType because ASISetROIFormat takes it too — unlike
    // SVBony, where the output format is a separate call — so a RAW8<->RAW16
    // switch must NOT be skipped. Reset on connect.
    private bool _roiApplied;
    private int _lastRoiX, _lastRoiY, _lastRoiW, _lastRoiH, _lastRoiBin;
    private ASI_IMG_TYPE _lastRoiImgType;
    private ASI_IMG_TYPE _imgType = ASI_IMG_TYPE.ASI_IMG_RAW16;

    private readonly ConcurrentDictionary<int, Action<IImageData>> _streamSubs = new();
    private int _nextSubId;
    private volatile bool _streaming;
    private Thread? _streamThread;
    private CancellationTokenSource? _streamCts;
    private readonly object _gate = new();
    // The ASI SDK is NOT thread-safe per camera handle: a control read from the
    // WS status tick (Temperature/Cooler) concurrent with the pull thread's
    // ASIGetVideoData wedges/crashes the native lib. This lock serialises every
    // individual SDK call so get/set/grab never overlap. Held only for the
    // duration of one native call (incl. the blocking ASIGetVideoData, which has
    // its own waitMs), never across the loop.
    private readonly object _sdk = new();

    public AsiSdkCamera(string deviceId) {
        ZwoRegistry.EnsureResolver();
        _cameraId = int.TryParse(deviceId, out var id) ? id : 0;
        DeviceName = $"ASI #{_cameraId}";
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
            var t = _connected ? ReadControl(ASI_CONTROL_TYPE.ASI_TEMPERATURE) / 10.0 : double.NaN;
            if (!double.IsNaN(t)) _lastTempC = t;
            return t;
        }
    }
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
    /// <summary>The SDK answers this outright, from ASI_CAMERA_INFO.IsColorCam,
    /// so the live stacker never has to guess mono-vs-colour from an
    /// absent Bayer pattern.</summary>
    public bool? IsColorSensor => _connected ? _isColor : null;

    public int GainMin => _gainMin;
    public int GainMax => _gainMax;

    /// <summary>Exposure bounds from the ASI_EXPOSURE control caps, cached at
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
        SupportsWhiteBalance: false,
        SupportedBins: _supportedBins);

    /// <summary>Straight out of ASI_CAMERA_INFO.SupportedBins, which the SDK
    /// hands over at connect and which nothing used to read.</summary>
    private IReadOnlyList<int> _supportedBins = Array.Empty<int>();

    /// <summary>Decode the SDK's supported-bin array: up to 16 ints, ascending,
    /// terminated by a 0. Whatever follows the terminator is uninitialised
    /// memory, which is how a naive read ends up offering bin 32.</summary>
    public static IReadOnlyList<int> ParseSupportedBins(int[]? raw) {
        if (raw == null) return Array.Empty<int>();
        var bins = new List<int>();
        foreach (var b in raw) {
            if (b <= 0) break;                       // terminator
            if (!bins.Contains(b)) bins.Add(b);
        }
        bins.Sort();
        return bins;
    }

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
        _supportedBins = ParseSupportedBins(info.SupportedBins);

        _minExpSec = null; _maxExpSec = null;
        if (ASIGetNumOfControls(_cameraId, out var nCtrl) == ASI_ERROR_CODE.ASI_SUCCESS) {
            for (int i = 0; i < nCtrl; i++) {
                var caps = new ASI_CONTROL_CAPS();
                if (ASIGetControlCaps(_cameraId, i, ref caps) != ASI_ERROR_CODE.ASI_SUCCESS) continue;
                switch ((ASI_CONTROL_TYPE)caps.ControlType) {
                    case ASI_CONTROL_TYPE.ASI_GAIN:
                        _gainMin = (int)caps.MinValue.Value;
                        _gainMax = (int)caps.MaxValue.Value;
                        break;
                    case ASI_CONTROL_TYPE.ASI_EXPOSURE: {
                        // ASI_EXPOSURE caps are in MICROSECONDS.
                        long emin = (long)caps.MinValue.Value, emax = (long)caps.MaxValue.Value;
                        if (emin > 0 && emax > emin) {
                            _minExpSec = emin / 1_000_000.0;
                            _maxExpSec = emax / 1_000_000.0;
                        }
                        break;
                    }
                }
            }
        }

        _imgType = _bitDepth > 8 ? ASI_IMG_TYPE.ASI_IMG_RAW16 : ASI_IMG_TYPE.ASI_IMG_RAW8;
        _roiX = 0; _roiY = 0; _roiW = _maxX; _roiH = _maxY; _bin = 1;
        ASISetROIFormat(_cameraId, _maxX, _maxY, 1, _imgType);
        ASISetStartPos(_cameraId, 0, 0);
        // Seed ApplyRoi's idempotency guard with what we just wrote, so the usual
        // "reset to full frame" calls right after connect are no-ops.
        _lastRoiX = 0; _lastRoiY = 0; _lastRoiW = _maxX; _lastRoiH = _maxY; _lastRoiBin = 1;
        _lastRoiImgType = _imgType;
        _roiApplied = true;
        _gain = ReadControl(ASI_CONTROL_TYPE.ASI_GAIN);
        _offset = ReadControl(ASI_CONTROL_TYPE.ASI_OFFSET);
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
        var want = Math.Max(1, binX);
        // Refuse a bin the SDK told us this model does not have, instead of
        // setting it and hoping. ASISetROIFormat's return code used to be
        // discarded, so an unsupported mode left the camera as it was while
        // Polaris recorded the new bin and reported it upward: the UI, the FITS
        // header and the status payload all claimed a binning the sensor never
        // applied.
        if (!Capabilities.AllowsBin(want)) {
            throw new NotSupportedException(
                $"{DeviceName} does not support bin {want}x{want}. Supported: "
                + string.Join(", ", _supportedBins.Select(b => $"{b}x{b}")) + ".");
        }
        _bin = want; ApplyRoi(); return Task.CompletedTask;
    }

    public Task SetTemperatureAsync(double temperature, CancellationToken ct = default) {
        if (_supportsCooler)
            lock (_sdk)
                ASISetControlValue(_cameraId, ASI_CONTROL_TYPE.ASI_TARGET_TEMP, new CLong((nint)Math.Round(temperature)), 0);
        return Task.CompletedTask;
    }

    public Task SetCoolerAsync(bool on, CancellationToken ct = default) {
        if (_supportsCooler)
            lock (_sdk)
                ASISetControlValue(_cameraId, ASI_CONTROL_TYPE.ASI_COOLER_ON, new CLong(on ? 1 : 0), 0);
        return Task.CompletedTask;
    }

    public Task SetIsoAsync(int iso, CancellationToken ct = default) => Task.CompletedTask;

    public Task AbortExposureAsync(CancellationToken ct = default) {
        // Must stop the EXPOSURE, not the video capture: CaptureAsync uses the
        // snap API (ASIStartExposure / ASIGetDataAfterExp), and those are
        // distinct SDK entry points from ASIStartVideoCapture / ASIGetVideoData.
        // This called ASIStopVideoCapture — a leftover from the snap-mode
        // migration documented in CaptureAsync itself ("The old path used video
        // capture..."): the capture path was migrated, this one wasn't. Aborting
        // a still therefore stopped an idle video engine and left the exposure
        // integrating, so POST /api/camera/abort silently did nothing (it does
        // not cancel the capture token either — the poll loop just ran to its
        // deadline). The guider path masked this: it cancels the token first, so
        // CaptureAsync's finally cleaned up regardless of what Abort did.
        // Stills and streams are mutually exclusive (CaptureAsync throws while
        // _streaming), so there is no case where a stream needs stopping here —
        // the stream has StopVideoStreamAsync. Mirrors PlayerOne's POAStopExposure.
        lock (_sdk) { try { ASIStopExposure(_cameraId); } catch { } }
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
        // ASI forbids changing ROI/format DURING capture — doing so wedges the
        // driver. While a stream is running just stash the fields; they take
        // effect on the next StartVideoStreamAsync, which re-applies ROI while
        // stopped.
        if (_streaming) return;
        int w = Math.Max(8, (_roiW / _bin) & ~7); // ASI requires width % 8 == 0
        int h = Math.Max(2, (_roiH / _bin) & ~1); // and height % 2 == 0
        // Idempotency guard, mirroring SVBony (SvbonySdkCamera.ApplyRoi) and the
        // INDI CCD_FRAME guard: skip the SDK writes when the geometry already
        // matches. This matters MORE here than on the other natives: CaptureAsync
        // re-calls ApplyRoi() on EVERY still, and slew-and-centre / plate solve /
        // autofocus each call SetSubframeAsync(0,0,0,0) around every capture with
        // the SAME full-frame geometry — so a burst of solve/AF captures fired two
        // unconditional SDK writes per frame at a driver whose own comment (above)
        // says ROI must not change during capture. Also closes most of the
        // mid-still race: `_streaming` only covers the video stream, so a
        // SetSubframeAsync landing during a STILL used to write ROI mid-exposure.
        if (_roiApplied && _lastRoiX == _roiX && _lastRoiY == _roiY
                && _lastRoiW == w && _lastRoiH == h && _lastRoiBin == _bin
                && _lastRoiImgType == _imgType) return;
        ASI_ERROR_CODE roiErr;
        lock (_sdk) {
            roiErr = ASISetROIFormat(_cameraId, w, h, _bin, _imgType);
            ASISetStartPos(_cameraId, _roiX / _bin, _roiY / _bin);
        }
        // The return code used to be thrown away. A rejected format left the
        // camera on its previous geometry while everything downstream believed
        // the new one, so a bin that never took hold looked exactly like a bin
        // that did. Do NOT cache it as applied: the next call must retry rather
        // than skip on the idempotency guard.
        if (roiErr != ASI_ERROR_CODE.ASI_SUCCESS) {
            _roiApplied = false;
            throw new InvalidOperationException(
                $"{DeviceName} refused {w}x{h} bin {_bin} (ASISetROIFormat: {roiErr}).");
        }
        _lastRoiX = _roiX; _lastRoiY = _roiY;
        _lastRoiW = w; _lastRoiH = h; _lastRoiBin = _bin;
        _lastRoiImgType = _imgType;
        _roiApplied = true;
    }

    public Task<IImageData> CaptureAsync(double exposureSeconds, CaptureOptions? opts = null,
                                         CancellationToken ct = default) => Task.Run<IImageData>(() => {
        lock (_gate) {
            if (_streaming) throw new InvalidOperationException("Stop the video stream before a still exposure.");
            // Re-assert the 16-bit ROI format (size + bin + image type) right
            // before the still, mirroring the SVBony native path. We own the
            // SDK handle exclusively so it can't be flipped to RAW8 the way an
            // external INDI driver can, but this keeps all native backends
            // consistent and guards against any future stream/ROI path that
            // might change the format.
            ApplyRoi();
            ApplyExposureGain(exposureSeconds, opts?.Gain, opts?.Offset);
            GetRoi(out var w, out var h);
            var bytes = new byte[(long)w * h * BytesPerPixel()];
            State = CameraStates.Exposing;
            // Snap (still) mode: ASIStartExposure integrates exactly the
            // configured exposure. The old path used video capture
            // (ASIStartVideoCapture + ASIGetVideoData), which hands back the
            // frame already in flight, so long subs (15s/60s) came back early
            // instead of integrating the requested time. bIsDark=0 (ASI has no
            // mechanical shutter; the flag is informational).
            lock (_sdk) {
                // Defensive stop before start, mirroring PlayerOne and SVBony: if a
                // previous capture didn't stop cleanly the SDK still thinks it's
                // exposing, and the next start can wedge the driver. Stills churn a
                // full start/stop per frame, so bursts of slew+solve / autofocus
                // captures hit it hardest — that's how the SVBony twin failed in
                // the field. A stop on an idle camera is a harmless no-op. Rated
                // lower risk here (the ASI SDK is more tolerant than SVBony's), but
                // it costs nothing and keeps the natives consistent.
                try { ASIStopExposure(_cameraId); } catch { }
                Check(ASIStartExposure(_cameraId, 0), "ASIStartExposure");
            }
            try {
                long deadline = Environment.TickCount64 + (long)(exposureSeconds * 1000) + 8000;
                while (true) {
                    ct.ThrowIfCancellationRequested();
                    ASI_EXPOSURE_STATUS st;
                    lock (_sdk) Check(ASIGetExpStatus(_cameraId, out st), "ASIGetExpStatus");
                    if (st == ASI_EXPOSURE_STATUS.ASI_EXP_SUCCESS) break;
                    if (st == ASI_EXPOSURE_STATUS.ASI_EXP_FAILED)
                        throw new InvalidOperationException("ASI exposure failed.");
                    if (Environment.TickCount64 > deadline)
                        throw new TimeoutException("ASI exposure timed out.");
                    // Poll coarse while integrating, fine once it should be done.
                    Thread.Sleep(st == ASI_EXPOSURE_STATUS.ASI_EXP_WORKING ? 50 : 5);
                }
                lock (_sdk) Check(ASIGetDataAfterExp(_cameraId, bytes, new CLong(bytes.Length)), "ASIGetDataAfterExp");
            } finally {
                lock (_sdk) { try { ASIStopExposure(_cameraId); } catch { } }
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
                if (opts?.BinX is int b) _bin = Math.Max(1, b);
                ApplyExposureGain(opts?.ExposureSeconds ?? _exposureSec, opts?.Gain);
                // Re-apply ROI + output format while STOPPED (ASI forbids
                // changing them during capture). This also picks up any ROI set
                // via SetSubframe while a previous stream was running (ApplyRoi
                // defers the SDK write during capture). _streaming is still false
                // here, so ApplyRoi runs.
                ApplyRoi();
                lock (_sdk) {
                    // Defensive: if a previous session didn't stop cleanly the SDK
                    // is still "capturing", and a second ASIStartVideoCapture then
                    // wedges the driver. A stop on an idle camera is a harmless
                    // no-op.
                    try { ASIStopVideoCapture(_cameraId); } catch { }
                    Check(ASIStartVideoCapture(_cameraId), "ASIStartVideoCapture");
                }
                _streamCts = new CancellationTokenSource();
                _streaming = true;
                State = CameraStates.Exposing;
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
        // Join the pull thread BEFORE touching the SDK so its in-flight
        // ASIGetVideoData (holding _sdk) has finished — then ASIStopVideoCapture
        // under _sdk can't run concurrently with it. Generous timeout so a
        // long-exposure GetVideoData can return first.
        try { t?.Join(5000); } catch { }
        lock (_sdk) { try { ASIStopVideoCapture(_cameraId); } catch { } }
        State = CameraStates.Idle;
    }

    private void PullLoop(CancellationToken ct) {
        GetRoi(out var w, out var h);
        var buf = new byte[(long)w * h * BytesPerPixel()];
        int waitMs = (int)(_exposureSec * 1000 * 2 + 500);
        while (!ct.IsCancellationRequested && _streaming) {
            ASI_ERROR_CODE err;
            lock (_sdk) err = ASIGetVideoData(_cameraId, buf, new CLong(buf.Length), waitMs);
            if (err == ASI_ERROR_CODE.ASI_ERROR_TIMEOUT) continue;
            if (err != ASI_ERROR_CODE.ASI_SUCCESS) continue;
            IImageData frame;
            try { frame = WrapFrame(buf, w, h); } catch { continue; }
            foreach (var s in _streamSubs.Values) { try { s(frame); } catch { } }
        }
    }

    private void ApplyExposureGain(double exposureSeconds, int? gainOverride, int? offsetOverride = null) {
        _exposureSec = exposureSeconds > 0 ? exposureSeconds : _exposureSec;
        if (gainOverride is int g) _gain = g;
        if (offsetOverride is int o) _offset = o;
        // Serialise the control writes against the streaming pull thread / status
        // reads (see _sdk note): live exposure/gain tuning during a stream would
        // otherwise race ASIGetVideoData.
        lock (_sdk) {
            ASISetControlValue(_cameraId, ASI_CONTROL_TYPE.ASI_EXPOSURE,
                new CLong((nint)Math.Round(_exposureSec * 1_000_000)), 0);
            ASISetControlValue(_cameraId, ASI_CONTROL_TYPE.ASI_GAIN, new CLong(_gain), 0);
            if (offsetOverride is int)
                ASISetControlValue(_cameraId, ASI_CONTROL_TYPE.ASI_OFFSET, new CLong(_offset), 0);
        }
    }

    private int BytesPerPixel() => _imgType == ASI_IMG_TYPE.ASI_IMG_RAW16 ? 2 : 1;

    private void GetRoi(out int w, out int h) {
        ASI_ERROR_CODE rc; int rw, rh;
        lock (_sdk) rc = ASIGetROIFormat(_cameraId, out rw, out rh, out _, out _);
        if (rc == ASI_ERROR_CODE.ASI_SUCCESS && rw > 0 && rh > 0) { w = rw; h = rh; }
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
            // RAW16 delivers right-aligned raw ADC values (a 12-bit sensor gives
            // 0..4095 in the low bits). Advertise the real depth so the SER
            // recorder can left-align to the 16-bit container the way ZWO's own
            // tools expect. RAW8 is already widened above (px << 8), so leave it 0.
            SignificantBitDepth = _imgType == ASI_IMG_TYPE.ASI_IMG_RAW16 ? _bitDepth : 0,
        };
        var meta = new ImageMetaData();
        meta.Camera.Name = DeviceName;
        meta.Camera.Gain = _gain;
        // Stamp the integration time so the FITS/XISF writers emit EXPTIME /
        // EXPOSURE (otherwise native-SDK frames saved with no exposure value).
        meta.Exposure.ExposureTime = _exposureSec;
        meta.Camera.Offset = _offset;
        meta.Camera.PixelSizeX = _pixelSize;
        meta.Camera.PixelSizeY = _pixelSize;
        // Binning + sensor temperature (essential for calibration matching) and
        // the Bayer pattern (was missing entirely, so OSC ASI colour frames
        // saved with no BAYERPAT and couldn't be debayered downstream).
        meta.Camera.BinX = (short)_bin;
        meta.Camera.BinY = (short)_bin;
        if (!double.IsNaN(_lastTempC)) meta.Camera.Temperature = _lastTempC;
        meta.Camera.BayerPattern = _bayer;
        return new BaseImageData(pixels, props, meta);
    }

    private int ReadControl(ASI_CONTROL_TYPE t) {
        try {
            // Serialise against the streaming pull thread (see _sdk note).
            lock (_sdk) {
                if (ASIGetControlValue(_cameraId, t, out var v, out _) == ASI_ERROR_CODE.ASI_SUCCESS)
                    return (int)v.Value;
            }
        } catch { }
        return 0;
    }

    // ----- Dynamic control panel (self-describing via ASIGetControlCaps) -----

    public IReadOnlyList<CameraControl> GetControls() {
        var list = new List<CameraControl>();
        if (!_connected) return list;
        try {
            lock (_sdk) {
                if (ASIGetNumOfControls(_cameraId, out var n) != ASI_ERROR_CODE.ASI_SUCCESS) return list;
                for (int i = 0; i < n; i++) {
                    var caps = new ASI_CONTROL_CAPS();
                    if (ASIGetControlCaps(_cameraId, i, ref caps) != ASI_ERROR_CODE.ASI_SUCCESS) continue;
                    var type = (ASI_CONTROL_TYPE)caps.ControlType;
                    double cur = 0; int isAuto = 0;
                    if (ASIGetControlValue(_cameraId, type, out var v, out var a) == ASI_ERROR_CODE.ASI_SUCCESS) {
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
        if (!_connected || !Enum.TryParse<ASI_CONTROL_TYPE>(id, out var type)) return false;
        try {
            lock (_sdk) {
                return ASISetControlValue(_cameraId, type,
                    new CLong((nint)(long)Math.Round(value)), auto ? 1 : 0) == ASI_ERROR_CODE.ASI_SUCCESS;
            }
        } catch { return false; }
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