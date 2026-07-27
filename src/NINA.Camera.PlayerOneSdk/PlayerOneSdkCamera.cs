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
using NINA.Camera.PlayerOneSdk.Native;
using NINA.Core.Enum;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using static NINA.Camera.PlayerOneSdk.Native.PoaNative;

namespace NINA.Camera.PlayerOneSdk;

/// <summary>
/// <see cref="ICamera"/> backend for PlayerOne cameras over the native
/// PlayerOneCamera SDK. Same shape as the SVBony / ZWO backends: full still
/// capture + cooler + ROI, plus native video streaming (dedicated pull thread
/// looping POAGetImageData) so CameraStreamService gets the native path. This
/// is the high-fps path for PlayerOne planetary cams (e.g. Mars-C / Neptune).
/// </summary>
public sealed class PlayerOneSdkCamera : ICamera {
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
    // guard. Reset on connect, where the camera comes up at its own full frame.
    private bool _roiApplied;
    private int _lastRoiX, _lastRoiY, _lastRoiW, _lastRoiH, _lastRoiBin;
    private POAImgFormat _imgFormat = POAImgFormat.POA_RAW16;

    private readonly ConcurrentDictionary<int, Action<IImageData>> _streamSubs = new();
    private int _nextSubId;
    private volatile bool _streaming;
    private Thread? _streamThread;
    private CancellationTokenSource? _streamCts;
    private readonly object _gate = new();
    // The PlayerOne (POA) SDK is NOT thread-safe per camera handle: a control
    // read from the WS status tick (Temperature/Cooler) concurrent with the pull
    // thread's POAGetImageData wedges/crashes the native lib a few seconds into a
    // stream. This lock serialises every individual SDK call so get/set/grab
    // never overlap. Held only for the duration of one native call (incl. the
    // blocking POAGetImageData, which has its own waitMs), never across the loop.
    private readonly object _sdk = new();

    public PlayerOneSdkCamera(string deviceId) {
        PlayerOneRegistry.EnsureResolver();
        _cameraId = int.TryParse(deviceId, out var id) ? id : 0;
        DeviceName = $"PlayerOne #{_cameraId}";
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
            var t = _connected ? ReadFloat(POAConfig.POA_TEMPERATURE) : double.NaN;
            if (!double.IsNaN(t)) _lastTempC = t;
            return t;
        }
    }
    public bool CoolerOn => _connected && _supportsCooler && ReadInt(POAConfig.POA_COOLER) != 0;
    public double CoolerPower => _connected && _supportsCooler ? ReadInt(POAConfig.POA_COOLER_POWER) : 0;
    public int BinX => _bin;
    public int BinY => _bin;
    public int BitDepth => _bitDepth > 8 ? 16 : 8;
    public int MaxX => _maxX;
    public int MaxY => _maxY;
    public double PixelSizeX => _pixelSize;
    public double PixelSizeY => _pixelSize;
    public int Gain => _gain;
    /// <summary>The SDK answers this outright, from POACameraProperties.isColorCamera,
    /// so the live stacker never has to guess mono-vs-colour from an
    /// absent Bayer pattern.</summary>
    public bool? IsColorSensor => _connected ? _isColor : null;

    public int GainMin => _gainMin;
    public int GainMax => _gainMax;

    /// <summary>Exposure bounds from the POA_EXPOSURE config attributes, cached
    /// at connect (fixed per camera). null when the SDK didn't answer.</summary>
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

    public Task ConnectAsync(CancellationToken ct = default) => Task.Run(() => {
        var info = new POACameraProperties();
        if (POAGetCameraPropertiesByID(_cameraId, ref info) != POAErrors.POA_OK)
            throw new InvalidOperationException($"PlayerOne camera id {_cameraId} not found.");

        var openErr = POAOpenCamera(_cameraId);
        if (openErr != POAErrors.POA_OK)
            throw new InvalidOperationException(
                $"Failed to open the PlayerOne camera (POAOpenCamera: {openErr}). " +
                "The camera may already be in use by another process. If an INDI " +
                "driver for this camera is connected, disconnect it (or remove it " +
                "from the running indiserver profile) before using the native SDK backend.");
        Check(POAInitCamera(_cameraId), "POAInitCamera");

        if (!string.IsNullOrWhiteSpace(info.cameraModelName)) DeviceName = info.cameraModelName;
        _maxX = info.maxWidth;
        _maxY = info.maxHeight;
        _bitDepth = info.bitDepth;
        _isColor = info.isColorCamera != 0;
        _bayer = _isColor ? MapBayer(info.bayerPattern) : BayerPatternEnum.None;
        _pixelSize = info.pixelSize;
        _supportsCooler = info.isHasCooler != 0;

        // Gain range from the config attributes.
        var attr = new POAConfigAttributes();
        if (POAGetConfigAttributesByConfigID(_cameraId, POAConfig.POA_GAIN, ref attr) == POAErrors.POA_OK) {
            _gainMin = attr.minValue.intValue;
            _gainMax = attr.maxValue.intValue;
        }
        // Exposure range, likewise from the config attributes. POA_EXPOSURE is
        // an integer config in MICROSECONDS.
        _minExpSec = null; _maxExpSec = null;
        try {
            var expAttr = new POAConfigAttributes();
            if (POAGetConfigAttributesByConfigID(_cameraId, POAConfig.POA_EXPOSURE, ref expAttr) == POAErrors.POA_OK) {
                long emin = expAttr.minValue.intValue, emax = expAttr.maxValue.intValue;
                if (emin > 0 && emax > emin) {
                    _minExpSec = emin / 1_000_000.0;
                    _maxExpSec = emax / 1_000_000.0;
                }
            }
        } catch { }

        _imgFormat = _bitDepth > 8 ? POAImgFormat.POA_RAW16 : POAImgFormat.POA_RAW8;
        POASetImageFormat(_cameraId, _imgFormat);
        _roiX = 0; _roiY = 0; _roiW = _maxX; _roiH = _maxY; _bin = 1;
        POASetImageBin(_cameraId, 1);
        POASetImageSize(_cameraId, _maxX, _maxY);
        POASetImageStartPos(_cameraId, 0, 0);
        // Seed ApplyRoi's idempotency guard with what we just wrote, so the usual
        // "reset to full frame" calls right after connect are no-ops.
        _lastRoiX = 0; _lastRoiY = 0; _lastRoiW = _maxX; _lastRoiH = _maxY; _lastRoiBin = 1;
        _roiApplied = true;
        _gain = ReadInt(POAConfig.POA_GAIN);
        _offset = ReadInt(POAConfig.POA_OFFSET);
        _connected = true;
        State = CameraStates.Idle;
    }, ct);

    public Task DisconnectAsync(CancellationToken ct = default) => Task.Run(() => {
        try { StopStreamCore(); } catch { }
        if (_connected) { try { POACloseCamera(_cameraId); } catch { } }
        _connected = false;
        State = CameraStates.NoState;
    }, ct);

    public Task SetBinningAsync(int binX, int binY, CancellationToken ct = default) {
        _bin = Math.Max(1, binX); ApplyRoi(); return Task.CompletedTask;
    }

    public Task SetTemperatureAsync(double temperature, CancellationToken ct = default) {
        if (_supportsCooler)
            lock (_sdk)
                POASetConfig(_cameraId, POAConfig.POA_TARGET_TEMP, POAConfigValue.Int((int)Math.Round(temperature)), POABool.POA_FALSE);
        return Task.CompletedTask;
    }

    public Task SetCoolerAsync(bool on, CancellationToken ct = default) {
        if (_supportsCooler)
            lock (_sdk)
                POASetConfig(_cameraId, POAConfig.POA_COOLER, POAConfigValue.Bool(on), POABool.POA_FALSE);
        return Task.CompletedTask;
    }

    public Task SetIsoAsync(int iso, CancellationToken ct = default) => Task.CompletedTask;

    public Task AbortExposureAsync(CancellationToken ct = default) {
        lock (_sdk) { try { POAStopExposure(_cameraId); } catch { } }
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
        // PlayerOne forbids changing ROI/format DURING capture (the exposure must
        // be stopped). While a stream is running just stash the fields; they take
        // effect on the next StartVideoStreamAsync, which re-applies ROI while
        // stopped.
        if (_streaming) return;
        // PlayerOne wants width % 4 == 0 and height % 2 == 0 (post-bin).
        int w = Math.Max(4, (_roiW / _bin) & ~3);
        int h = Math.Max(2, (_roiH / _bin) & ~1);
        // Idempotency guard, mirroring SVBony (SvbonySdkCamera.ApplyRoi) and the
        // INDI CCD_FRAME guard: skip the SDK writes when the geometry is already
        // what we asked for. Slew-and-centre, plate solve and autofocus all call
        // SetSubframeAsync(0,0,0,0) around every capture — always the SAME
        // full-frame geometry — so without this each one was three redundant SDK
        // writes on a driver whose own comment (above) says ROI must not change
        // during capture. It also closes most of the mid-still race: `_streaming`
        // only covers the video stream, so a SetSubframeAsync landing during a
        // STILL used to write ROI mid-exposure; the common no-op case now never
        // touches the SDK at all.
        if (_roiApplied && _lastRoiX == _roiX && _lastRoiY == _roiY
                && _lastRoiW == w && _lastRoiH == h && _lastRoiBin == _bin) return;
        lock (_sdk) {
            POASetImageBin(_cameraId, _bin);
            POASetImageSize(_cameraId, w, h);
            POASetImageStartPos(_cameraId, _roiX / _bin, _roiY / _bin);
        }
        _lastRoiX = _roiX; _lastRoiY = _roiY;
        _lastRoiW = w; _lastRoiH = h; _lastRoiBin = _bin;
        _roiApplied = true;
    }

    public Task<IImageData> CaptureAsync(double exposureSeconds, CaptureOptions? opts = null,
                                         CancellationToken ct = default) => Task.Run<IImageData>(() => {
        lock (_gate) {
            if (_streaming) throw new InvalidOperationException("Stop the video stream before a still exposure.");
            // Re-assert the 16-bit pixel format before the still, mirroring the
            // SVBony native path. We own the SDK handle exclusively so it can't
            // be flipped to RAW8 like an external INDI driver can, but this
            // keeps the native backends consistent + future-proof.
            lock (_sdk) POASetImageFormat(_cameraId, _imgFormat);
            ApplyExposureGain(exposureSeconds, opts?.Gain, opts?.Offset);
            GetRoi(out var w, out var h);
            var bytes = new byte[(long)w * h * BytesPerPixel()];
            int waitMs = (int)(exposureSeconds * 1000 * 2 + 500);
            State = CameraStates.Exposing;
            lock (_sdk) {
                // Same defensive stop the stream path does (see
                // StartVideoStreamAsync, whose comment names POAStartExposure as
                // the call that wedges the driver when the SDK still thinks it's
                // exposing). Stills churn a full start/stop PER FRAME, so bursts
                // of slew+solve / autofocus captures hit this far harder than
                // video ever does — that's how the SVBony twin failed in the
                // field. A stop on an idle camera is a harmless no-op.
                try { POAStopExposure(_cameraId); } catch { }
                Check(POAStartExposure(_cameraId, POABool.POA_TRUE), "POAStartExposure");
            }
            try {
                POAErrors err;
                lock (_sdk) err = POAGetImageData(_cameraId, bytes, new CLong(bytes.Length), waitMs);
                Check(err, "POAGetImageData");
            } finally {
                lock (_sdk) { try { POAStopExposure(_cameraId); } catch { } }
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
                // Re-apply ROI + output format while STOPPED (PlayerOne forbids
                // changing them during capture). This also picks up any ROI set
                // via SetSubframe while a previous stream was running (ApplyRoi
                // defers the SDK write during capture). _streaming is still false
                // here, so ApplyRoi runs.
                ApplyRoi();
                lock (_sdk) {
                    POASetImageFormat(_cameraId, _imgFormat);
                    // Defensive: if a previous session didn't stop cleanly the SDK
                    // is still "exposing", and a second POAStartExposure then wedges
                    // the driver. A stop on an idle camera is a harmless no-op.
                    try { POAStopExposure(_cameraId); } catch { }
                    Check(POAStartExposure(_cameraId, POABool.POA_FALSE), "POAStartExposure");
                }
                _streamCts = new CancellationTokenSource();
                _streaming = true;
                State = CameraStates.Exposing;
                _streamThread = new Thread(() => PullLoop(_streamCts.Token)) {
                    IsBackground = true, Name = "PlayerOne-stream"
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
        // POAGetImageData (holding _sdk) has finished — then POAStopExposure under
        // _sdk can't run concurrently with it. Generous timeout so a long-exposure
        // GetImageData can return first.
        try { t?.Join(5000); } catch { }
        lock (_sdk) { try { POAStopExposure(_cameraId); } catch { } }
        State = CameraStates.Idle;
    }

    private void PullLoop(CancellationToken ct) {
        GetRoi(out var w, out var h);
        var buf = new byte[(long)w * h * BytesPerPixel()];
        int waitMs = (int)(_exposureSec * 1000 * 2 + 500);
        while (!ct.IsCancellationRequested && _streaming) {
            POAErrors err;
            lock (_sdk) err = POAGetImageData(_cameraId, buf, new CLong(buf.Length), waitMs);
            if (err == POAErrors.POA_ERROR_TIMEOUT) continue;
            if (err != POAErrors.POA_OK) continue;
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
        // otherwise race POAGetImageData.
        lock (_sdk) {
            POASetConfig(_cameraId, POAConfig.POA_EXPOSURE,
                POAConfigValue.Int((int)Math.Round(_exposureSec * 1_000_000)), POABool.POA_FALSE);
            POASetConfig(_cameraId, POAConfig.POA_GAIN, POAConfigValue.Int(_gain), POABool.POA_FALSE);
            if (offsetOverride is int)
                POASetConfig(_cameraId, POAConfig.POA_OFFSET, POAConfigValue.Int(_offset), POABool.POA_FALSE);
        }
    }

    private int BytesPerPixel() => _imgFormat == POAImgFormat.POA_RAW16 ? 2 : 1;

    private void GetRoi(out int w, out int h) {
        POAErrors rc; int rw, rh;
        lock (_sdk) rc = POAGetImageSize(_cameraId, out rw, out rh);
        if (rc == POAErrors.POA_OK && rw > 0 && rh > 0) {
            w = rw; h = rh;
        } else {
            w = _roiW > 0 ? _roiW / _bin : _maxX;
            h = _roiH > 0 ? _roiH / _bin : _maxY;
        }
    }

    private IImageData WrapFrame(byte[] bytes, int w, int h) {
        var pixels = new ushort[(long)w * h];
        if (_imgFormat == POAImgFormat.POA_RAW16)
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
        // Stamp the integration time so the FITS/XISF writers emit EXPTIME /
        // EXPOSURE (otherwise native-SDK frames saved with no exposure value).
        meta.Exposure.ExposureTime = _exposureSec;
        // Binning + sensor temperature — essential for matching calibration
        // frames (darks/flats); otherwise absent from native-SDK FITS.
        meta.Camera.BinX = (short)_bin;
        meta.Camera.BinY = (short)_bin;
        if (!double.IsNaN(_lastTempC)) meta.Camera.Temperature = _lastTempC;
        meta.Camera.Offset = _offset;
        meta.Camera.PixelSizeX = _pixelSize;
        meta.Camera.PixelSizeY = _pixelSize;
        // FITS/XISF writers stamp BAYERPAT from meta.Camera.BayerPattern, not
        // props — propagate the detected pattern so OSC frames save with it.
        meta.Camera.BayerPattern = _bayer;
        return new BaseImageData(pixels, props, meta);
    }

    private int ReadInt(POAConfig c) {
        try {
            var v = new POAConfigValue(); var a = POABool.POA_FALSE;
            // Serialise against the streaming pull thread (see _sdk note).
            lock (_sdk)
                if (POAGetConfig(_cameraId, c, ref v, ref a) == POAErrors.POA_OK) return v.intValue;
        } catch { }
        return 0;
    }

    private double ReadFloat(POAConfig c) {
        try {
            var v = new POAConfigValue(); var a = POABool.POA_FALSE;
            // Serialise against the streaming pull thread (see _sdk note).
            lock (_sdk)
                if (POAGetConfig(_cameraId, c, ref v, ref a) == POAErrors.POA_OK) return v.floatValue;
        } catch { }
        return double.NaN;
    }

    // ----- Dynamic control panel (self-describing via POAGetConfigAttributes) -----

    public IReadOnlyList<CameraControl> GetControls() {
        var list = new List<CameraControl>();
        if (!_connected) return list;
        try {
            lock (_sdk) {
                foreach (POAConfig cfg in Enum.GetValues<POAConfig>()) {
                    var attr = new POAConfigAttributes();
                    if (POAGetConfigAttributesByConfigID(_cameraId, cfg, ref attr) != POAErrors.POA_OK) continue;
                    double min, max, def; string vt;
                    switch (attr.valueType) {
                        case POAValueType.VAL_FLOAT:
                            min = attr.minValue.floatValue; max = attr.maxValue.floatValue;
                            def = attr.defaultValue.floatValue; vt = "float"; break;
                        case POAValueType.VAL_BOOL:
                            min = 0; max = 1; def = attr.defaultValue.intValue; vt = "bool"; break;
                        default:
                            min = attr.minValue.intValue; max = attr.maxValue.intValue;
                            def = attr.defaultValue.intValue; vt = "int"; break;
                    }
                    double cur = def; bool isAuto = false;
                    if (attr.isReadable != 0) {
                        var val = new POAConfigValue(); var pa = POABool.POA_FALSE;
                        if (POAGetConfig(_cameraId, cfg, ref val, ref pa) == POAErrors.POA_OK) {
                            cur = attr.valueType == POAValueType.VAL_FLOAT ? val.floatValue : val.intValue;
                            isAuto = pa == POABool.POA_TRUE;
                        }
                    }
                    string name = string.IsNullOrWhiteSpace(attr.szConfName) ? cfg.ToString() : attr.szConfName.Trim();
                    list.Add(new CameraControl(cfg.ToString(), name, attr.szDescription?.Trim(),
                        cur, min, max, def, attr.isWritable != 0, isAuto, attr.isSupportAuto != 0, vt));
                }
            }
        } catch { }
        return list;
    }

    public bool SetControl(string id, double value, bool auto) {
        if (!_connected || !Enum.TryParse<POAConfig>(id, out var cfg)) return false;
        try {
            lock (_sdk) {
                var attr = new POAConfigAttributes();
                var vt = POAValueType.VAL_INT;
                if (POAGetConfigAttributesByConfigID(_cameraId, cfg, ref attr) == POAErrors.POA_OK)
                    vt = attr.valueType;
                var cv = vt == POAValueType.VAL_FLOAT
                    ? POAConfigValue.Float(value)
                    : POAConfigValue.Int((int)Math.Round(value));
                return POASetConfig(_cameraId, cfg, cv, auto ? POABool.POA_TRUE : POABool.POA_FALSE) == POAErrors.POA_OK;
            }
        } catch { return false; }
    }

    private static BayerPatternEnum MapBayer(POABayerPattern p) => p switch {
        POABayerPattern.POA_BAYER_RG => BayerPatternEnum.RGGB,
        POABayerPattern.POA_BAYER_BG => BayerPatternEnum.BGGR,
        POABayerPattern.POA_BAYER_GR => BayerPatternEnum.GRBG,
        POABayerPattern.POA_BAYER_GB => BayerPatternEnum.GBRG,
        _ => BayerPatternEnum.None
    };

    private static void Check(POAErrors err, string op) {
        if (err != POAErrors.POA_OK)
            throw new InvalidOperationException($"PlayerOne {op} failed: {err}");
    }

    private sealed class Sub : IDisposable {
        private readonly PlayerOneSdkCamera _cam; private readonly int _id;
        public Sub(PlayerOneSdkCamera cam, int id) { _cam = cam; _id = id; }
        public void Dispose() => _cam._streamSubs.TryRemove(_id, out _);
    }
}