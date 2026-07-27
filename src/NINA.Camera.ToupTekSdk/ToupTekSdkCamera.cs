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
using NINA.Core.Enum;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;

namespace NINA.Camera.ToupTekSdk;

/// <summary>
/// <see cref="ICamera"/> backend for ToupTek cameras over the native toupcam
/// SDK (via the vendored official <c>Toupcam</c> binding). Same shape as the
/// SVBony / ZWO / PlayerOne backends: full still capture + cooler + ROI, plus
/// native video streaming. ToupTek's SDK is callback-driven (pull mode): we
/// start pull mode once, and the SDK fires EVENT_IMAGE on its own thread; we
/// pull the frame and fan it out to subscribers, so CameraStreamService gets
/// the native path. This is the high-fps path for ToupTek planetary cams.
/// </summary>
public sealed class ToupTekSdkCamera : ICamera {
    private readonly string _camId;
    private Toupcam? _cam;
    private bool _connected;

    private int _maxX, _maxY, _bitDepth = 16;
    private double _pixelSize;
    private BayerPatternEnum _bayer = BayerPatternEnum.None;
    private bool _isColor, _supportsCooler;
    private int _gainMin, _gainMax = 100;

    private int _gain;
    private int _offset;   // sensor black level (OPTION_BLACKLEVEL); stamped into FITS
    private double _exposureSec = 0.03;
    private int _roiX, _roiY, _roiW, _roiH, _bin = 1;

    private Toupcam.DelegateEventCallback? _evtCb;
    private volatile bool _pullActive;
    private ushort[]? _buf16;
    private byte[]? _buf8;

    private readonly object _captureGate = new();
    private TaskCompletionSource<IImageData>? _captureTcs;

    private readonly ConcurrentDictionary<int, Action<IImageData>> _streamSubs = new();
    private int _nextSubId;
    private volatile bool _streaming;
    private readonly object _gate = new();

    public ToupTekSdkCamera(string deviceId) {
        ToupTekRegistry.EnsureResolver();
        _camId = deviceId ?? string.Empty;
        DeviceName = $"ToupTek {_camId}";
    }

    public string DeviceName { get; private set; }
    public bool IsConnected => _connected;
    public CameraStates State { get; private set; } = CameraStates.NoState;

    // Cache the last valid reading so WrapFrame can stamp CCD-TEMP into the
    // FITS without an extra SDK read on every streamed frame (the WS status
    // tick refreshes this every ~2 s).
    private double _lastTempC = double.NaN;
    public double Temperature {
        get {
            if (_cam != null && _connected && _cam.get_Temperature(out short t)) {
                _lastTempC = t / 10.0;
                return _lastTempC;
            }
            return double.NaN;
        }
    }
    public bool CoolerOn {
        get {
            if (_cam != null && _supportsCooler && _cam.get_Option(Toupcam.eOPTION.OPTION_TEC, out int v)) return v != 0;
            return false;
        }
    }
    public double CoolerPower => 0;
    public int BinX => _bin;
    public int BinY => _bin;
    public int BitDepth => _bitDepth > 8 ? 16 : 8;
    public int MaxX => _maxX;
    public int MaxY => _maxY;
    public double PixelSizeX => _pixelSize;
    public double PixelSizeY => _pixelSize;
    public int Gain => _gain;
    /// <summary>The SDK answers this outright, from the SDK's FLAG_MONO capability bit,
    /// so the live stacker never has to guess mono-vs-colour from an
    /// absent Bayer pattern.</summary>
    public bool? IsColorSensor => _connected ? _isColor : null;

    public int GainMin => _gainMin;
    public int GainMax => _gainMax;
    public IReadOnlyList<int> IsoOptions { get; } = Array.Empty<int>();
    public int SelectedIso => 0;

    public CameraCapabilities Capabilities => new(
        SupportsCooler: _supportsCooler,
        SupportsBinning: false,
        SupportsRoi: true,
        SupportsIso: false,
        SupportsBulb: false,
        SupportsVideoStream: true,
        SupportsWhiteBalance: false);

    public Task ConnectAsync(CancellationToken ct = default) => Task.Run(() => {
        // Find the model entry to read flags + pixel size before opening.
        ulong flag = 0;
        try {
            foreach (var d in Toupcam.EnumV2()) {
                if (d.id == _camId) {
                    flag = d.model.flag;
                    _pixelSize = d.model.xpixsz;
                    if (!string.IsNullOrWhiteSpace(d.displayname)) DeviceName = d.displayname;
                    break;
                }
            }
        } catch { }

        var cam = Toupcam.Open(_camId);
        if (cam == null) throw new InvalidOperationException(
            $"Failed to open the ToupTek camera '{_camId}'. The camera may already " +
            "be in use by another process. If an INDI driver for this camera is " +
            "connected, disconnect it (or remove it from the running indiserver " +
            "profile) before using the native SDK backend.");
        _cam = cam;

        _isColor = (flag & (ulong)Toupcam.eFLAG.FLAG_MONO) == 0;
        _supportsCooler = (flag & (ulong)(Toupcam.eFLAG.FLAG_TEC | Toupcam.eFLAG.FLAG_TEC_ONOFF)) != 0;

        // Raw (bayer) output, max bit depth.
        cam.put_Option(Toupcam.eOPTION.OPTION_RAW, 1);
        cam.put_Option(Toupcam.eOPTION.OPTION_BITDEPTH, 1);

        if (cam.get_RawFormat(out uint fourcc, out uint bitdepth)) {
            _bitDepth = (int)bitdepth;
            _bayer = _isColor ? MapBayer(fourcc) : BayerPatternEnum.None;
        }
        if (cam.get_Size(out int w, out int h)) { _maxX = w; _maxY = h; }
        if (cam.get_ExpoAGainRange(out ushort gmin, out ushort gmax, out _)) {
            _gainMin = gmin; _gainMax = gmax;
        }

        _roiX = 0; _roiY = 0; _roiW = _maxX; _roiH = _maxY; _bin = 1;
        _gain = _gainMin;
        if (cam.get_Option(Toupcam.eOPTION.OPTION_BLACKLEVEL, out int bl0)) _offset = bl0;
        _connected = true;
        State = CameraStates.Idle;
    }, ct);

    public Task DisconnectAsync(CancellationToken ct = default) => Task.Run(() => {
        try { StopPull(); } catch { }
        _streaming = false;
        try { _cam?.Close(); } catch { }
        _cam = null;
        _connected = false;
        State = CameraStates.NoState;
    }, ct);

    public Task SetBinningAsync(int binX, int binY, CancellationToken ct = default) => Task.CompletedTask;

    public Task SetTemperatureAsync(double temperature, CancellationToken ct = default) {
        if (_cam != null && _supportsCooler)
            _cam.put_Option(Toupcam.eOPTION.OPTION_TECTARGET, (int)Math.Round(temperature * 10));
        return Task.CompletedTask;
    }

    public Task SetCoolerAsync(bool on, CancellationToken ct = default) {
        if (_cam != null && _supportsCooler)
            _cam.put_Option(Toupcam.eOPTION.OPTION_TEC, on ? 1 : 0);
        return Task.CompletedTask;
    }

    // ----- Dynamic control panel (curated — the ToupTek SDK is option-based,
    // not self-describing, so we surface a fixed set of well-known controls). -----

    public IReadOnlyList<CameraControl> GetControls() {
        var list = new List<CameraControl>();
        if (_cam == null || !_connected) return list;
        list.Add(new CameraControl("Gain", "Gain", "Analog gain",
            _gain, _gainMin, _gainMax == 0 ? ushort.MaxValue : _gainMax, _gainMin,
            Writable: true, Auto: false, AutoSupported: false, "int"));
        // Offset / black level. Range scales with the current bit depth.
        int blackMax = 31 << Math.Max(0, (_bitDepth > 8 ? 16 : 8) - 8);
        if (_cam.get_Option(Toupcam.eOPTION.OPTION_BLACKLEVEL, out int bl))
            list.Add(new CameraControl("Offset", "Offset (black level)", "Sensor black level / pedestal",
                bl, 0, blackMax, 0, Writable: true, Auto: false, AutoSupported: false, "int"));
        // Gamma (display curve). 100 = linear.
        if (_cam.get_Gamma(out int gm))
            list.Add(new CameraControl("Gamma", "Gamma", "Tone curve (100 = linear)",
                gm, Toupcam.GAMMA_MIN, Toupcam.GAMMA_MAX, Toupcam.GAMMA_DEF,
                Writable: true, Auto: false, AutoSupported: false, "int"));
        // USB / frame-speed level. Lower it if the link drops frames.
        uint maxSpeed = 0;
        try { maxSpeed = _cam.MaxSpeed; } catch { }
        if (maxSpeed > 0 && _cam.get_Speed(out ushort sp))
            list.Add(new CameraControl("Speed", "USB speed level", "Frame speed level; lower on unstable USB",
                sp, 0, maxSpeed, maxSpeed, Writable: true, Auto: false, AutoSupported: false, "int"));
        // Saturation only makes sense on colour sensors.
        if (_isColor && _cam.get_Saturation(out int sat))
            list.Add(new CameraControl("Saturation", "Saturation", "Colour saturation (128 = neutral)",
                sat, Toupcam.SATURATION_MIN, Toupcam.SATURATION_MAX, Toupcam.SATURATION_DEF,
                Writable: true, Auto: false, AutoSupported: false, "int"));
        // Contrast + brightness apply to mono and colour alike.
        if (_cam.get_Contrast(out int ct))
            list.Add(new CameraControl("Contrast", "Contrast", "Display contrast (0 = neutral)",
                ct, Toupcam.CONTRAST_MIN, Toupcam.CONTRAST_MAX, Toupcam.CONTRAST_DEF,
                Writable: true, Auto: false, AutoSupported: false, "int"));
        if (_cam.get_Brightness(out int br))
            list.Add(new CameraControl("Brightness", "Brightness", "Display brightness (0 = neutral)",
                br, Toupcam.BRIGHTNESS_MIN, Toupcam.BRIGHTNESS_MAX, Toupcam.BRIGHTNESS_DEF,
                Writable: true, Auto: false, AutoSupported: false, "int"));
        // White balance (colour only). Temperature + tint are a paired setter,
        // surfaced as two controls; setting one reads the other's live value.
        if (_isColor && _cam.get_TempTint(out int wbTemp, out int wbTint)) {
            list.Add(new CameraControl("WBTemp", "WB temperature", "White-balance colour temperature (K)",
                wbTemp, Toupcam.TEMP_MIN, Toupcam.TEMP_MAX, Toupcam.TEMP_DEF,
                Writable: true, Auto: false, AutoSupported: false, "int"));
            list.Add(new CameraControl("WBTint", "WB tint", "White-balance tint",
                wbTint, Toupcam.TINT_MIN, Toupcam.TINT_MAX, Toupcam.TINT_DEF,
                Writable: true, Auto: false, AutoSupported: false, "int"));
        }
        // In-camera denoise strength. Keep at 0 for stacking (denoise later).
        if (_cam.get_Option(Toupcam.eOPTION.OPTION_DENOISE, out int dn))
            list.Add(new CameraControl("Denoise", "Denoise", "In-camera denoise strength (0 = off)",
                dn, 0, 100, 0, Writable: true, Auto: false, AutoSupported: false, "int"));
        // Cooling fan — only if this model actually has one.
        uint fanMax = 0;
        try { fanMax = _cam.FanMaxSpeed; } catch { }
        if (fanMax > 0 && _cam.get_Option(Toupcam.eOPTION.OPTION_FAN, out int fan))
            list.Add(new CameraControl("Fan", "Fan speed", "Cooling fan speed (0 = off)",
                fan, 0, fanMax, fanMax, Writable: true, Auto: false, AutoSupported: false, "int"));
        double temp = double.NaN;
        if (_cam.get_Temperature(out short t)) temp = t / 10.0;
        list.Add(new CameraControl("Temperature", "Sensor temperature", "°C (read-only)",
            temp, -50, 50, 0, Writable: false, Auto: false, AutoSupported: false, "float"));
        if (_supportsCooler) {
            _cam.get_Option(Toupcam.eOPTION.OPTION_TEC, out int tec);
            list.Add(new CameraControl("Cooler", "Cooler (TEC)", "Thermoelectric cooler on/off",
                tec != 0 ? 1 : 0, 0, 1, 0, Writable: true, Auto: false, AutoSupported: false, "bool"));
            double target = 0;
            if (_cam.get_Option(Toupcam.eOPTION.OPTION_TECTARGET, out int tt)) target = tt / 10.0;
            list.Add(new CameraControl("TargetTemp", "Target temperature", "Cooler set-point (°C)",
                target, -40, 20, 0, Writable: true, Auto: false, AutoSupported: false, "int"));
        }
        return list;
    }

    public bool SetControl(string id, double value, bool auto) {
        if (_cam == null || !_connected) return false;
        try {
            switch (id) {
                case "Gain":
                    _gain = (int)Math.Round(value);
                    return _cam.put_ExpoAGain((ushort)Math.Clamp(_gain, _gainMin,
                        _gainMax == 0 ? ushort.MaxValue : _gainMax));
                case "Offset":
                    _offset = Math.Clamp((int)Math.Round(value), 0, BlackMax());
                    return _cam.put_Option(Toupcam.eOPTION.OPTION_BLACKLEVEL, _offset);
                case "Gamma":
                    return _cam.put_Gamma((int)Math.Round(value));
                case "Speed":
                    return _cam.put_Speed((ushort)Math.Round(value));
                case "Saturation":
                    return _cam.put_Saturation((int)Math.Round(value));
                case "Contrast":
                    return _cam.put_Contrast((int)Math.Round(value));
                case "Brightness":
                    return _cam.put_Brightness((int)Math.Round(value));
                case "WBTemp": {
                    _cam.get_TempTint(out int _, out int curTint);
                    return _cam.put_TempTint((int)Math.Round(value), curTint);
                }
                case "WBTint": {
                    _cam.get_TempTint(out int curTemp, out int _);
                    return _cam.put_TempTint(curTemp, (int)Math.Round(value));
                }
                case "Denoise":
                    return _cam.put_Option(Toupcam.eOPTION.OPTION_DENOISE, (int)Math.Round(value));
                case "Fan":
                    return _cam.put_Option(Toupcam.eOPTION.OPTION_FAN, (int)Math.Round(value));
                case "Cooler":
                    return _cam.put_Option(Toupcam.eOPTION.OPTION_TEC, value != 0 ? 1 : 0);
                case "TargetTemp":
                    return _cam.put_Option(Toupcam.eOPTION.OPTION_TECTARGET, (int)Math.Round(value * 10));
                default:
                    return false; // Temperature is read-only; unknown ids ignored.
            }
        } catch { return false; }
    }

    public Task SetIsoAsync(int iso, CancellationToken ct = default) => Task.CompletedTask;

    public Task AbortExposureAsync(CancellationToken ct = default) {
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
        if (_cam == null || !_connected) return;
        if (_roiW >= _maxX && _roiH >= _maxY && _roiX == 0 && _roiY == 0) {
            _cam.put_Roi(0, 0, 0, 0); // reset to full frame
        } else {
            uint x = (uint)(_roiX & ~1), y = (uint)(_roiY & ~1);
            uint w = (uint)Math.Max(16, _roiW & ~1), h = (uint)Math.Max(16, _roiH & ~1);
            _cam.put_Roi(x, y, w, h);
        }
    }

    public Task<IImageData> CaptureAsync(double exposureSeconds, CaptureOptions? opts = null,
                                         CancellationToken ct = default) => Task.Run<IImageData>(() => {
        if (_cam == null) throw new InvalidOperationException("Camera not connected.");
        ApplyExposureGain(exposureSeconds, opts?.Gain, opts?.Offset);
        var tcs = new TaskCompletionSource<IImageData>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_captureGate) _captureTcs = tcs;
        bool startedForCapture = !_pullActive;
        EnsurePull();
        State = CameraStates.Exposing;
        try {
            int waitMs = (int)(exposureSeconds * 1000 * 2 + 3000);
            if (!tcs.Task.Wait(waitMs, ct))
                throw new TimeoutException("ToupTek frame timed out.");
            return tcs.Task.Result;
        } finally {
            lock (_captureGate) _captureTcs = null;
            if (startedForCapture && !_streaming) StopPull();
            State = CameraStates.Idle;
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
                _streaming = true;
                State = CameraStates.Exposing;
                EnsurePull();
            }
        }, ct);

    public Task StopVideoStreamAsync(CancellationToken ct = default) => Task.Run(() => {
        lock (_gate) {
            if (!_streaming) return;
            _streaming = false;
        }
        bool capturePending; lock (_captureGate) capturePending = _captureTcs != null;
        if (!capturePending) StopPull();
        State = CameraStates.Idle;
    }, ct);

    private void EnsurePull() {
        if (_cam == null || _pullActive) return;
        _evtCb = OnEvent;
        if (_cam.StartPullModeWithCallback(_evtCb)) _pullActive = true;
    }

    private void StopPull() {
        if (_cam == null || !_pullActive) return;
        try { _cam.Stop(); } catch { }
        _pullActive = false;
    }

    private void OnEvent(Toupcam.eEVENT e) {
        if (e != Toupcam.eEVENT.EVENT_IMAGE) return;
        if (_cam == null) return;
        if (!_cam.get_Size(out int w, out int h) || w <= 0 || h <= 0) return;
        IImageData frame;
        try {
            if (_bitDepth > 8) {
                if (_buf16 == null || _buf16.Length < (long)w * h) _buf16 = new ushort[(long)w * h];
                if (!_cam.PullImage(_buf16, 0, 16, 0, out Toupcam.FrameInfoV4 _)) return;
                frame = WrapFrame16(_buf16, w, h);
            } else {
                if (_buf8 == null || _buf8.Length < (long)w * h) _buf8 = new byte[(long)w * h];
                if (!_cam.PullImage(_buf8, 0, 8, 0, out Toupcam.FrameInfoV4 _)) return;
                frame = WrapFrame8(_buf8, w, h);
            }
        } catch { return; }

        TaskCompletionSource<IImageData>? tcs;
        lock (_captureGate) tcs = _captureTcs;
        tcs?.TrySetResult(frame);

        if (_streaming)
            foreach (var s in _streamSubs.Values) { try { s(frame); } catch { } }
    }

    // Max black level scales with the current bit depth (see GetControls).
    private int BlackMax() => 31 << Math.Max(0, (_bitDepth > 8 ? 16 : 8) - 8);

    private void ApplyExposureGain(double exposureSeconds, int? gainOverride, int? offsetOverride = null) {
        if (_cam == null) return;
        _exposureSec = exposureSeconds > 0 ? exposureSeconds : _exposureSec;
        _cam.put_ExpoTime((uint)Math.Round(_exposureSec * 1_000_000));
        if (gainOverride is int g) _gain = g;
        _cam.put_ExpoAGain((ushort)Math.Clamp(_gain, _gainMin, _gainMax == 0 ? ushort.MaxValue : _gainMax));
        if (offsetOverride is int o) {
            _offset = Math.Clamp(o, 0, BlackMax());
            _cam.put_Option(Toupcam.eOPTION.OPTION_BLACKLEVEL, _offset);
        }
    }

    private IImageData WrapFrame16(ushort[] src, int w, int h) {
        var pixels = new ushort[(long)w * h];
        Array.Copy(src, pixels, pixels.Length);
        return Wrap(pixels, w, h);
    }

    private IImageData WrapFrame8(byte[] src, int w, int h) {
        var pixels = new ushort[(long)w * h];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = (ushort)(src[i] << 8);
        return Wrap(pixels, w, h);
    }

    private IImageData Wrap(ushort[] pixels, int w, int h) {
        var props = new ImageProperties {
            Width = w, Height = h, BitDepth = BitDepth,
            IsBayered = _bayer != BayerPatternEnum.None, BayerPattern = _bayer,
        };
        var meta = new ImageMetaData();
        meta.Camera.Name = DeviceName;
        meta.Camera.Gain = _gain;
        meta.Camera.Offset = _offset;
        // Stamp the integration time so the FITS/XISF writers emit EXPTIME /
        // EXPOSURE (otherwise native-SDK frames saved with no exposure value).
        meta.Exposure.ExposureTime = _exposureSec;
        // Binning + sensor temperature — essential for matching calibration
        // frames (darks/flats); otherwise absent from native-SDK FITS.
        meta.Camera.BinX = (short)_bin;
        meta.Camera.BinY = (short)_bin;
        if (!double.IsNaN(_lastTempC)) meta.Camera.Temperature = _lastTempC;
        meta.Camera.PixelSizeX = _pixelSize;
        meta.Camera.PixelSizeY = _pixelSize;
        // FITS/XISF writers stamp BAYERPAT from meta.Camera.BayerPattern, not
        // props — propagate the detected pattern so OSC frames save with it.
        meta.Camera.BayerPattern = _bayer;
        return new BaseImageData(pixels, props, meta);
    }

    /// <summary>Map a ToupTek raw FourCC (e.g. 'GBRG') to a Bayer pattern.
    /// The FourCC is packed little-endian: byte0 is the first character.</summary>
    private static BayerPatternEnum MapBayer(uint fourcc) {
        Span<char> c = stackalloc char[4];
        c[0] = (char)(fourcc & 0xff);
        c[1] = (char)((fourcc >> 8) & 0xff);
        c[2] = (char)((fourcc >> 16) & 0xff);
        c[3] = (char)((fourcc >> 24) & 0xff);
        var s = new string(c);
        return s switch {
            "RGGB" => BayerPatternEnum.RGGB,
            "BGGR" => BayerPatternEnum.BGGR,
            "GRBG" => BayerPatternEnum.GRBG,
            "GBRG" => BayerPatternEnum.GBRG,
            _ => BayerPatternEnum.RGGB,
        };
    }

    private sealed class Sub : IDisposable {
        private readonly ToupTekSdkCamera _cam; private readonly int _id;
        public Sub(ToupTekSdkCamera cam, int id) { _cam = cam; _id = id; }
        public void Dispose() => _cam._streamSubs.TryRemove(_id, out _);
    }
}