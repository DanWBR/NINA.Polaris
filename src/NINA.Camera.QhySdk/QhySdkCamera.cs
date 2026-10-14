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

using NINA.Image.NativeLibs;
using NINA.Core.Enum;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using static NINA.Camera.QhySdk.Native.QhyNative;

namespace NINA.Camera.QhySdk;

/// <summary>
/// <see cref="ICamera"/> backend for QHY cameras over the native QHYCCD SDK.
/// Same shape as the PlayerOne / ZWO backends for stills: single-frame
/// capture, ROI, binning, gain/offset, cooler. No native video stream yet
/// (<see cref="CameraCapabilities.SupportsVideoStream"/> is false, so
/// CameraStreamService falls back to its capture loop).
///
/// <para>Geometry follows N.I.N.A.'s QHY driver: the SDK is told to include
/// the overscan (CAM_IGNOREOVERSCAN_INTERFACE = 0) and the backend crops to
/// the effective area itself, so <see cref="MaxX"/>/<see cref="MaxY"/> are the
/// effective (light-sensitive) size and ROI coordinates are relative to it.</para>
/// </summary>
public sealed class QhySdkCamera : ICamera {
    private readonly string _cameraId;
    private IntPtr _handle;
    private volatile bool _connected;
    private string? _disconnectReason;

    // Sensor, from GetQHYCCDChipInfo / GetQHYCCDEffectiveArea at connect.
    private uint _chipW, _chipH;
    private QhyArea _sensor;
    private int _bitDepth = 16;
    private double _pixelSize;
    private BayerPatternEnum _bayer = BayerPatternEnum.None;
    private bool _isColor, _hasCooler, _hasChipTemp, _hasGain, _hasOffset;
    private double _pwmMax = 255;
    private int _gainMin, _gainMax;
    private double? _minExpSec, _maxExpSec;
    private IReadOnlyList<int> _supportedBins = Array.Empty<int>();

    private int _gain, _offset;
    private double _exposureSec = 1;
    private int _roiX, _roiY, _roiW, _roiH, _bin = 1;

    // The cooler. QHY firmware comes in two eras: the old one must be told
    // the target every couple of seconds or it stops regulating, the new one
    // does not care, and nothing in the SDK says which one a camera runs.
    // QHY's own advice (and what N.I.N.A. does) is to treat every camera as
    // old, so a background loop re-sends the target once a second while the
    // cooler is on. There is no "is the cooler on" read-back either, so the
    // state below is the truth this backend reports.
    private volatile bool _coolerOn;
    private double _targetTemp;
    private CancellationTokenSource? _coolerCts;
    private Task? _coolerTask;

    // Reused across stills: GetQHYCCDMemLength is the full-sensor size, and
    // allocating that per frame on a 60 MP camera is 120 MB of garbage a shot.
    private byte[]? _frameBuf;

    // _gate serialises whole operations (connect, capture, disconnect).
    // _sdk serialises individual SDK calls on this camera's handle: the QHY
    // SDK is not thread-safe per camera, and the status tick reads the
    // temperature and cooler power from another thread while a capture runs.
    // A still holds _sdk only around each call, never across the exposure
    // wait, so those reads keep flowing during a long sub. The camera's filter
    // wheel makes calls on the same handle, so _sdk is the registry's lock for
    // that handle, taken over at connect.
    private readonly object _gate = new();
    private object _sdk = new();
    private CancellationTokenSource? _captureCts;

    public QhySdkCamera(string deviceId) {
        QhyRegistry.EnsureResolver();
        _cameraId = deviceId ?? "";
        DeviceName = string.IsNullOrWhiteSpace(_cameraId) ? "QHY" : QhyRegistry.ModelFromId(_cameraId);
    }

    public string DeviceName { get; private set; }
    public bool IsConnected => _connected;
    public string? DisconnectReason => _disconnectReason;
    public CameraStates State { get; private set; } = CameraStates.NoState;

    // Cache the last valid reading so WrapFrame can stamp CCD-TEMP into the
    // FITS without an extra SDK read, and so a status read that would have
    // to wait out a readout can answer with it instead (see ReadParam).
    private double _lastTempC = double.NaN;
    public double Temperature {
        get {
            if (!_connected || !_hasChipTemp) return double.NaN;
            var t = ReadParam(CONTROL_ID.CONTROL_CURTEMP, _lastTempC);
            if (!double.IsNaN(t)) _lastTempC = t;
            return t;
        }
    }
    public bool CoolerOn => _connected && _hasCooler && _coolerOn;
    private double _lastPwm;
    public double CoolerPower {
        get {
            if (!_connected || !_hasCooler) return 0;
            var pwm = ReadParam(CONTROL_ID.CONTROL_CURPWM, _lastPwm);
            if (double.IsNaN(pwm)) return 0;
            _lastPwm = pwm;
            // CONTROL_CURPWM is the raw PWM duty (0..255 on most models), not
            // a percentage; scale by the MANULPWM ceiling the camera reports.
            return Math.Clamp(pwm / _pwmMax * 100.0, 0, 100);
        }
    }
    public int BinX => _bin;
    public int BinY => _bin;
    public int BitDepth => _bitDepth > 8 ? 16 : 8;
    public int MaxX => (int)_sensor.W;
    public int MaxY => (int)_sensor.H;
    public double PixelSizeX => _pixelSize;
    public double PixelSizeY => _pixelSize;
    public int Gain => _gain;
    /// <summary>From IsQHYCCDControlAvailable(CAM_COLOR), which answers with
    /// the Bayer layout for a colour sensor and an error for a mono one.</summary>
    public bool? IsColorSensor => _connected ? _isColor : null;

    public int GainMin => _gainMin;
    public int GainMax => _gainMax;

    /// <summary>Exposure bounds from GetQHYCCDParamMinMaxStep(CONTROL_EXPOSURE),
    /// cached at connect. null when the SDK didn't answer.</summary>
    public double? MinExposureSeconds => _connected ? _minExpSec : null;
    public double? MaxExposureSeconds => _connected ? _maxExpSec : null;

    public IReadOnlyList<int> IsoOptions { get; } = Array.Empty<int>();
    public int SelectedIso => 0;

    public CameraCapabilities Capabilities => new(
        SupportsCooler: _hasCooler,
        SupportsBinning: true,
        SupportsRoi: true,
        SupportsIso: false,
        SupportsBulb: false,
        SupportsVideoStream: false,
        SupportsWhiteBalance: false,
        SupportedBins: _supportedBins);

    public Task ConnectAsync(CancellationToken ct = default) => Task.Run(() => {
        lock (_gate) {
            if (_connected) return;
            // A camera that dropped off the bus (NoteSdkResult) still holds its
            // old handle; close it so the registry's open count stays honest.
            if (_handle != IntPtr.Zero) {
                StopCoolerLoop();
                lock (_sdk) { QhyRegistry.Close(_handle); _handle = IntPtr.Zero; }
            }
            var (h, model) = QhyRegistry.Open(_cameraId);
            if (h == IntPtr.Zero)
                throw new InvalidOperationException(
                    "Failed to open the QHY camera (OpenQHYCCD). " +
                    "The camera may already be in use by another process. If an INDI " +
                    "driver for this camera is connected, disconnect it (or remove it " +
                    "from the running indiserver profile) before using the native SDK backend.");
            _sdk = QhyRegistry.CallLock(h);
            try {
                lock (_sdk) {
                    _handle = h;
                    NativeCallGuard.Run(QhyRegistry.GuardName, "initialising the camera", InitCameraLocked);
                }
                QhyRegistry.MarkCameraInitialized(h);
            } catch {
                lock (_sdk) { QhyRegistry.Close(h); _handle = IntPtr.Zero; }
                throw;
            }
            if (!string.IsNullOrWhiteSpace(model)) DeviceName = model;
            _roiX = 0; _roiY = 0; _roiW = 0; _roiH = 0; _bin = 1;
            _disconnectReason = null;
            _connected = true;
            State = CameraStates.Idle;
            if (_hasCooler) StartCoolerLoop();
        }
    }, ct);

    /// <summary>Everything between OpenQHYCCD and the first exposure, in the
    /// order the SDK documents: stream mode before InitQHYCCD, chip geometry
    /// after it, bit depth and controls last. Caller holds _sdk.</summary>
    private void InitCameraLocked() {
        var h = _handle;
        Check(SetQHYCCDStreamMode(h, SingleFrameMode), "SetQHYCCDStreamMode");
        Check(InitQHYCCD(h), "InitQHYCCD");

        Check(GetQHYCCDChipInfo(h, out _, out _, out var imageW, out var imageH,
            out var pixelW, out _, out var bpp), "GetQHYCCDChipInfo");
        _chipW = imageW; _chipH = imageH;
        _pixelSize = pixelW;
        _bitDepth = (int)bpp;

        // Bin 1 before asking for the effective area: binning changes it.
        if (SetQHYCCDBinMode(h, 1, 1) == QHYCCD_DELAY_200MS) Thread.Sleep(200);
        // Keep the overscan in the SDK's output and crop to the effective area
        // here, the arrangement N.I.N.A. settled on: what the SDK does with
        // the flag set varies by model, the effective area does not.
        if (Has(CONTROL_ID.CAM_IGNOREOVERSCAN_INTERFACE))
            SetQHYCCDParam(h, CONTROL_ID.CAM_IGNOREOVERSCAN_INTERFACE, 0);
        var effOk = GetQHYCCDEffectiveArea(h, out var ex, out var ey, out var ew, out var eh) == QHYCCD_SUCCESS;
        _sensor = PickSensorArea(imageW, imageH, effOk, ex, ey, ew, eh);

        _bayer = MapBayer(IsQHYCCDControlAvailable(h, CONTROL_ID.CAM_COLOR));
        _isColor = _bayer != BayerPatternEnum.None;
        // Raw CFA data, never the SDK's own debayer: Polaris debayers itself.
        if (_isColor) SetQHYCCDDebayerOnOff(h, false);

        // 16-bit transfer where the camera offers a choice; 8-bit-only models
        // stay as they are and the readout's bpp says which arrived.
        if (Has(CONTROL_ID.CONTROL_TRANSFERBIT)) SetQHYCCDBitsMode(h, 16);

        // Cameras with on-board DDR memory buffer the whole frame before it
        // crosses the USB link, so a bus hiccup mid-readout no longer tears
        // the frame. N.I.N.A. switches it on whenever the camera has it.
        if (Has(CONTROL_ID.CONTROL_DDR)) SetQHYCCDParam(h, CONTROL_ID.CONTROL_DDR, 1);

        _hasGain = Has(CONTROL_ID.CONTROL_GAIN);
        _gainMin = _gainMax = 0;
        if (_hasGain && GetQHYCCDParamMinMaxStep(h, CONTROL_ID.CONTROL_GAIN, out var gmin, out var gmax, out _) == QHYCCD_SUCCESS) {
            _gainMin = (int)Math.Round(gmin);
            _gainMax = (int)Math.Round(gmax);
        }
        _hasOffset = Has(CONTROL_ID.CONTROL_OFFSET);

        // CONTROL_EXPOSURE is in MICROSECONDS.
        _minExpSec = null; _maxExpSec = null;
        if (GetQHYCCDParamMinMaxStep(h, CONTROL_ID.CONTROL_EXPOSURE, out var emin, out var emax, out _) == QHYCCD_SUCCESS
            && emax > emin && emax > 0) {
            _minExpSec = Math.Max(emin, 1) / 1_000_000.0;
            _maxExpSec = emax / 1_000_000.0;
        }

        _hasCooler = Has(CONTROL_ID.CONTROL_COOLER);
        _hasChipTemp = Has(CONTROL_ID.CONTROL_CURTEMP);
        _pwmMax = 255;
        if (_hasCooler) {
            // The return code is ignored on purpose: N.I.N.A. records that the
            // SDK reports failure here even when it filled the values in.
            GetQHYCCDParamMinMaxStep(h, CONTROL_ID.CONTROL_MANULPWM, out _, out var pmax, out _);
            if (pmax > 0 && pmax < 100_000) _pwmMax = pmax;
            // Start from a known state: TEC off, target 0 C. There is no way to
            // read back whether the cooler is on, so reporting "off" while a
            // previous session left it running would be a lie the operator
            // could not see. N.I.N.A. does the same on connect.
            SetQHYCCDParam(h, CONTROL_ID.CONTROL_MANULPWM, 0);
            _coolerOn = false;
            _targetTemp = 0;
        }

        var bins = new List<int>();
        foreach (var (bin, id) in BinControls)
            if (Has(id)) bins.Add(bin);
        _supportedBins = bins;

        _gain = _hasGain ? ClampToRange((int)Math.Round(SafeParam(CONTROL_ID.CONTROL_GAIN)), _gainMin, _gainMax) : 0;
        _offset = _hasOffset ? (int)Math.Round(SafeParam(CONTROL_ID.CONTROL_OFFSET)) : 0;
        _lastTempC = double.NaN;
        _lastPwm = 0;
    }

    private static readonly (int Bin, CONTROL_ID Id)[] BinControls = {
        (1, CONTROL_ID.CAM_BIN1X1MODE), (2, CONTROL_ID.CAM_BIN2X2MODE),
        (3, CONTROL_ID.CAM_BIN3X3MODE), (4, CONTROL_ID.CAM_BIN4X4MODE),
        (6, CONTROL_ID.CAM_BIN6X6MODE), (8, CONTROL_ID.CAM_BIN8X8MODE),
    };

    public Task DisconnectAsync(CancellationToken ct = default) => Task.Run(() => {
        // A still in flight is abandoned rather than waited out: cancel it
        // first so _gate frees up as soon as the SDK lets go.
        try { _captureCts?.Cancel(); } catch { }
        lock (_gate) {
            _connected = false;
            StopCoolerLoop();
            lock (_sdk) {
                if (_handle != IntPtr.Zero) {
                    // Old-era firmware keeps whatever PWM it was last given
                    // once nobody steers it, so a cooler left on here would run
                    // unregulated until the camera loses power. Off, as N.I.N.A.
                    // does on disconnect.
                    if (_hasCooler) { try { SetQHYCCDParam(_handle, CONTROL_ID.CONTROL_MANULPWM, 0); } catch { } }
                    QhyRegistry.Close(_handle);
                    _handle = IntPtr.Zero;
                }
            }
            _coolerOn = false;
            _frameBuf = null;
            // A disconnect the operator asked for is not news.
            _disconnectReason = null;
            State = CameraStates.NoState;
        }
    }, ct);

    public Task SetBinningAsync(int binX, int binY, CancellationToken ct = default) {
        var want = Math.Max(1, binX);
        // Refuse a bin the camera does not list (CAM_BINnXnMODE) instead of
        // recording it and returning a frame at some other binning.
        if (!Capabilities.AllowsBin(want)) {
            throw new NotSupportedException(
                $"{DeviceName} does not support bin {want}x{want}. Supported: "
                + string.Join(", ", _supportedBins.Select(b => $"{b}x{b}")) + ".");
        }
        _bin = want;
        return Task.CompletedTask;
    }

    public Task SetTemperatureAsync(double temperature, CancellationToken ct = default) {
        _targetTemp = temperature;
        // Applied at once when the cooler is running; the loop keeps sending it.
        if (_hasCooler && _coolerOn && _connected)
            lock (_sdk) { if (_handle != IntPtr.Zero) ControlQHYCCDTemp(_handle, temperature); }
        return Task.CompletedTask;
    }

    public Task SetCoolerAsync(bool on, CancellationToken ct = default) {
        if (!_hasCooler || !_connected) return Task.CompletedTask;
        _coolerOn = on;
        lock (_sdk) {
            if (_handle == IntPtr.Zero) return Task.CompletedTask;
            if (on) ControlQHYCCDTemp(_handle, _targetTemp);
            else SetQHYCCDParam(_handle, CONTROL_ID.CONTROL_MANULPWM, 0);
        }
        return Task.CompletedTask;
    }

    public Task SetIsoAsync(int iso, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Stops a still. The capture itself issues
    /// CancelQHYCCDExposingAndReadout when it sees the request, which is at
    /// once while it is waiting out the exposure; this call never waits on
    /// the SDK lock, so an abort during a readout does not block the caller.</summary>
    public Task AbortExposureAsync(CancellationToken ct = default) {
        try { _captureCts?.Cancel(); } catch (ObjectDisposedException) { }
        return Task.CompletedTask;
    }

    /// <summary>Only records the ROI: it is written to the SDK right before
    /// the next exposure, inside the capture, so a call landing mid-exposure
    /// can never change the geometry of a frame in flight.</summary>
    public Task SetSubframeAsync(int x, int y, int width, int height, CancellationToken ct = default) {
        if (width <= 0 || height <= 0) { _roiX = 0; _roiY = 0; _roiW = 0; _roiH = 0; }
        else { _roiX = x; _roiY = y; _roiW = width; _roiH = height; }
        return Task.CompletedTask;
    }

    public Task<IImageData> CaptureAsync(double exposureSeconds, CaptureOptions? opts = null,
                                         CancellationToken ct = default) => Task.Run<IImageData>(() => {
        lock (_gate) {
            if (!_connected || _handle == IntPtr.Zero)
                throw new InvalidOperationException($"{DeviceName} is not connected.");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _captureCts = cts;
            try {
                return CaptureLocked(exposureSeconds, opts, cts.Token);
            } finally {
                _captureCts = null;
            }
        }
    }, ct);

    private IImageData CaptureLocked(double exposureSeconds, CaptureOptions? opts, CancellationToken token) {
        ApplyExposureGain(exposureSeconds, opts?.Gain, opts?.Offset);
        var frame = ComputeResolution(_roiX, _roiY, _roiW, _roiH, _bin, _sensor);
        lock (_sdk) {
            // Bin mode then resolution, before every exposure, the order
            // N.I.N.A. and QHY's own sample use. Some models ask for a 200 ms
            // pause after a bin change.
            var rc = SetQHYCCDBinMode(_handle, (uint)_bin, (uint)_bin);
            if (rc == QHYCCD_DELAY_200MS) Thread.Sleep(200);
            else if (rc != QHYCCD_SUCCESS)
                throw new InvalidOperationException($"{DeviceName} refused bin {_bin}x{_bin} (SetQHYCCDBinMode: 0x{rc:X8}).");
            Check(SetQHYCCDResolution(_handle, frame.X, frame.Y, frame.W, frame.H), "SetQHYCCDResolution");
        }

        State = CameraStates.Exposing;
        bool inFlight = false;
        try {
            uint rc;
            lock (_sdk) rc = ExpQHYCCDSingleFrame(_handle);
            if (IsFailure(rc)) {
                NoteSdkResult(rc);
                throw new InvalidOperationException($"QHY ExpQHYCCDSingleFrame failed: 0x{rc:X8}");
            }
            inFlight = true;
            // Wait out the exposure WITHOUT the SDK lock, so the status tick's
            // temperature and cooler reads (and the cooler loop) keep going.
            // QHYCCD_READ_DIRECTLY means the frame is already there.
            if (rc != QHYCCD_READ_DIRECTLY) WaitExposure(_exposureSec, token);
            token.ThrowIfCancellationRequested();

            State = CameraStates.Download;
            // GetQHYCCDSingleFrame blocks until the readout is complete, so
            // once it returns there is nothing left in flight to cancel.
            inFlight = false;
            var img = ReadFrame();
            // An abort that arrived during the readout discards the frame.
            token.ThrowIfCancellationRequested();
            return img;
        } catch (OperationCanceledException) {
            // "Host software must not readout the data" after this call, and
            // CaptureLocked never does: it is only issued before the readout.
            if (inFlight)
                lock (_sdk) { try { if (_handle != IntPtr.Zero) CancelQHYCCDExposingAndReadout(_handle); } catch { } }
            throw;
        } finally {
            State = _connected ? CameraStates.Idle : CameraStates.NoState;
        }
    }

    private static void WaitExposure(double seconds, CancellationToken token) {
        long until = Environment.TickCount64 + (long)Math.Ceiling(seconds * 1000);
        while (true) {
            long left = until - Environment.TickCount64;
            if (left <= 0) return;
            // WaitOne returns early, and true, the moment the token is cancelled.
            if (token.WaitHandle.WaitOne((int)Math.Min(left, 1000)))
                token.ThrowIfCancellationRequested();
        }
    }

    private IImageData ReadFrame() {
        uint w = 0, h = 0, bpp = 0, channels = 0, rc;
        byte[] buf;
        lock (_sdk) {
            // The SDK writes as much as GetQHYCCDMemLength says it may, so the
            // buffer is never smaller than that, whatever the ROI.
            var need = BufferBytes(GetQHYCCDMemLength(_handle), _chipW, _chipH);
            if (_frameBuf == null || _frameBuf.LongLength < need) _frameBuf = new byte[need];
            buf = _frameBuf;
            rc = GetQHYCCDSingleFrame(_handle, ref w, ref h, ref bpp, ref channels, buf);
        }
        if (rc != QHYCCD_SUCCESS) {
            NoteSdkResult(rc);
            throw new InvalidOperationException($"QHY GetQHYCCDSingleFrame failed: 0x{rc:X8}");
        }
        if (channels > 1)
            throw new InvalidOperationException(
                $"{DeviceName} returned a {channels}-channel frame; the backend expects raw single-channel data.");
        long count = (long)w * h;
        int bytesPerPixel = bpp > 8 ? 2 : 1;
        if (count <= 0 || count * bytesPerPixel > buf.LongLength)
            throw new InvalidOperationException($"QHY GetQHYCCDSingleFrame returned an impossible frame: {w}x{h} at {bpp} bpp.");
        var pixels = bytesPerPixel == 2 ? Copy16(buf, count) : Widen8To16(buf, count);
        return WrapFrame(pixels, (int)w, (int)h, bytesPerPixel == 2);
    }

    private void ApplyExposureGain(double exposureSeconds, int? gainOverride, int? offsetOverride) {
        _exposureSec = exposureSeconds > 0 ? exposureSeconds : _exposureSec;
        if (gainOverride is int g) _gain = g;
        if (offsetOverride is int o) _offset = o;
        lock (_sdk) {
            Check(SetQHYCCDParam(_handle, CONTROL_ID.CONTROL_EXPOSURE, Math.Round(_exposureSec * 1_000_000)),
                "SetQHYCCDParam(CONTROL_EXPOSURE)");
            // Gain is written on every still because some models read it back
            // wrong (N.I.N.A. found the QHY163M reporting a tenth of it), so
            // the value this backend tracks is the one that counts.
            if (_hasGain) SetQHYCCDParam(_handle, CONTROL_ID.CONTROL_GAIN, _gain);
            if (_hasOffset && offsetOverride is int)
                SetQHYCCDParam(_handle, CONTROL_ID.CONTROL_OFFSET, _offset);
        }
    }

    private IImageData WrapFrame(ushort[] pixels, int w, int h, bool sixteenBit) {
        // A colour sensor's binned output is no longer a CFA mosaic, which is
        // why N.I.N.A. marks QHY frames bayered only at bin 1.
        var bayer = _bin == 1 ? _bayer : BayerPatternEnum.None;
        // SignificantBitDepth stays 0, the full 16-bit container. Whether every
        // QHY model scales its ADC output to the top of that range is not
        // confirmed, and the value only matters to the SER recorder, which a
        // stills-only backend never feeds.
        var props = new ImageProperties {
            Width = w, Height = h, BitDepth = sixteenBit ? 16 : 8,
            IsBayered = bayer != BayerPatternEnum.None, BayerPattern = bayer,
        };
        var meta = new ImageMetaData();
        meta.Camera.Name = DeviceName;
        meta.Camera.Gain = _gain;
        // Stamp the integration time so the FITS/XISF writers emit EXPTIME /
        // EXPOSURE (otherwise native-SDK frames saved with no exposure value).
        meta.Exposure.ExposureTime = _exposureSec;
        // Binning + sensor temperature, essential for matching calibration
        // frames (darks/flats).
        meta.Camera.BinX = (short)_bin;
        meta.Camera.BinY = (short)_bin;
        if (!double.IsNaN(_lastTempC)) meta.Camera.Temperature = _lastTempC;
        meta.Camera.Offset = _offset;
        meta.Camera.PixelSizeX = _pixelSize;
        meta.Camera.PixelSizeY = _pixelSize;
        // FITS/XISF writers stamp BAYERPAT from meta.Camera.BayerPattern, not
        // props, so propagate the pattern for OSC frames to save with it.
        meta.Camera.BayerPattern = bayer;
        return new BaseImageData(pixels, props, meta);
    }

    // ----- Pure helpers (unit-tested without hardware) -----

    /// <summary>A rectangle in sensor pixels, or in binned pixels once it has
    /// been through <see cref="ComputeResolution"/>.</summary>
    internal readonly record struct QhyArea(uint X, uint Y, uint W, uint H);

    /// <summary>The effective (light-sensitive) area when the SDK reports one
    /// that fits inside the chip image, else the whole chip image.</summary>
    internal static QhyArea PickSensorArea(uint imageW, uint imageH, bool effectiveOk,
                                           uint ex, uint ey, uint ew, uint eh) {
        if (effectiveOk && ew > 0 && eh > 0
            && (ulong)ex + ew <= imageW && (ulong)ey + eh <= imageH)
            return new QhyArea(ex, ey, ew, eh);
        return new QhyArea(0, 0, imageW, imageH);
    }

    /// <summary>
    /// Turn a Polaris ROI (unbinned pixels, relative to the effective area,
    /// width or height 0 meaning full frame) into the SetQHYCCDResolution
    /// arguments, which are in BINNED pixels relative to the chip image
    /// origin. The ROI is clamped into the effective area, its origin is made
    /// even so a colour sensor keeps its Bayer phase, and the size is kept
    /// even and at least 2x2.
    /// </summary>
    internal static QhyArea ComputeResolution(int roiX, int roiY, int roiW, int roiH, int bin, QhyArea sensor) {
        bin = Math.Max(1, bin);
        int sw = (int)sensor.W, sh = (int)sensor.H;
        if (roiW <= 0 || roiH <= 0) { roiX = 0; roiY = 0; roiW = sw; roiH = sh; }
        roiX = Math.Clamp(roiX, 0, Math.Max(0, sw - 2)) & ~1;
        roiY = Math.Clamp(roiY, 0, Math.Max(0, sh - 2)) & ~1;
        roiW = Math.Min(roiW, sw - roiX);
        roiH = Math.Min(roiH, sh - roiY);
        int x = ((int)sensor.X + roiX) / bin;
        int y = ((int)sensor.Y + roiY) / bin;
        int w = Math.Max(2, (roiW / bin) & ~1);
        int h = Math.Max(2, (roiH / bin) & ~1);
        return new QhyArea((uint)x, (uint)y, (uint)w, (uint)h);
    }

    /// <summary>Bytes to hand GetQHYCCDSingleFrame: what GetQHYCCDMemLength
    /// asks for, and never less than the full chip image at 16 bits, which is
    /// also the fallback when the SDK answers with its error value.</summary>
    internal static long BufferBytes(uint memLength, uint chipW, uint chipH) {
        long full = (long)chipW * chipH * 2;
        if (memLength == 0 || memLength == QHYCCD_ERROR) return Math.Max(full, 2);
        return Math.Max(memLength, full);
    }

    /// <summary>IsQHYCCDControlAvailable(CAM_COLOR) result to Polaris's Bayer
    /// enum. The SDK names the layout by its first row pair (BAYER_GB is
    /// G B / R G, i.e. GBRG), the same convention N.I.N.A. and indi_qhy_ccd
    /// use. Anything else, the error value of a mono sensor included, is
    /// None.</summary>
    internal static BayerPatternEnum MapBayer(uint bayerId) => bayerId switch {
        (uint)BAYER_ID.BAYER_GB => BayerPatternEnum.GBRG,
        (uint)BAYER_ID.BAYER_GR => BayerPatternEnum.GRBG,
        (uint)BAYER_ID.BAYER_BG => BayerPatternEnum.BGGR,
        (uint)BAYER_ID.BAYER_RG => BayerPatternEnum.RGGB,
        _ => BayerPatternEnum.None,
    };

    /// <summary>8-bit frame to the 16-bit container, shifted into the high
    /// byte the way every other native backend widens RAW8.</summary>
    internal static ushort[] Widen8To16(byte[] src, long count) {
        var dst = new ushort[count];
        for (long i = 0; i < count; i++) dst[i] = (ushort)(src[i] << 8);
        return dst;
    }

    /// <summary>16-bit little-endian frame bytes to pixels.</summary>
    internal static ushort[] Copy16(byte[] src, long count) {
        var dst = new ushort[count];
        Buffer.BlockCopy(src, 0, dst, 0, checked((int)(count * 2)));
        return dst;
    }

    /// <summary>The negative QHYCCD_ERROR_* codes, the generic 0xFFFFFFFF
    /// among them, as the unsigned values the SDK actually returns.</summary>
    internal static bool IsFailure(uint rc) => rc >= 0xFFFFFF00u;

    /// <summary>GetQHYCCDParam answers a double, and its failure is
    /// QHYCCD_ERROR converted to double.</summary>
    internal static bool IsParamError(double v) => double.IsNaN(v) || v == QHYCCD_ERROR;

    private static int ClampToRange(int v, int min, int max) =>
        max > min ? Math.Clamp(v, min, max) : v;

    // ----- SDK access -----

    private bool Has(CONTROL_ID id) => IsQHYCCDControlAvailable(_handle, id) == QHYCCD_SUCCESS;

    /// <summary>GetQHYCCDParam for connect-time reads; caller holds _sdk.</summary>
    private double SafeParam(CONTROL_ID id) {
        var v = GetQHYCCDParam(_handle, id);
        return IsParamError(v) ? 0 : v;
    }

    /// <summary>A status read (temperature, cooler PWM). It waits at most a
    /// moment for the SDK lock and otherwise answers with the last value: a
    /// GetQHYCCDSingleFrame readout holds the lock for seconds on a big
    /// sensor, and the status tick must not stall behind it.</summary>
    private double ReadParam(CONTROL_ID id, double fallback) {
        if (!Monitor.TryEnter(_sdk, 250)) return fallback;
        try {
            if (_handle == IntPtr.Zero) return double.NaN;
            var v = GetQHYCCDParam(_handle, id);
            return IsParamError(v) ? double.NaN : v;
        } catch {
            return double.NaN;
        } finally {
            Monitor.Exit(_sdk);
        }
    }

    /// <summary>Drop the connection when the SDK says the camera left the
    /// bus (QHYCCD_ERROR_DEVICELOSE). The generic error is not enough: it is
    /// what nearly every failure returns.</summary>
    private void NoteSdkResult(uint rc) {
        if (rc != QHYCCD_ERROR_DEVICELOSE || !_connected) return;
        _connected = false;
        _disconnectReason = "the camera reported itself removed from the USB bus";
    }

    private static void Check(uint rc, string op) {
        if (rc != QHYCCD_SUCCESS)
            throw new InvalidOperationException($"QHY {op} failed: 0x{rc:X8}");
    }

    // ----- Cooler loop -----

    private void StartCoolerLoop() {
        StopCoolerLoop();
        var cts = new CancellationTokenSource();
        _coolerCts = cts;
        _coolerTask = Task.Run(() => CoolerLoop(cts.Token));
    }

    private void StopCoolerLoop() {
        var cts = _coolerCts; var task = _coolerTask;
        _coolerCts = null; _coolerTask = null;
        if (cts == null) return;
        try { cts.Cancel(); } catch { }
        // Bounded: one SDK call can be stuck behind a readout for a while.
        try { task?.Wait(TimeSpan.FromSeconds(10)); } catch { }
        cts.Dispose();
    }

    private async Task CoolerLoop(CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            try {
                if (_coolerOn && _connected) {
                    lock (_sdk) {
                        if (!ct.IsCancellationRequested && _handle != IntPtr.Zero)
                            ControlQHYCCDTemp(_handle, _targetTemp);
                    }
                }
            } catch { }
            try { await Task.Delay(1000, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    // ----- Dynamic control panel -----

    /// <summary>The controls offered to the config panel. QHY has no
    /// self-describing control list (names, types, writability), so this is a
    /// curated set; each one only appears when the camera reports it.</summary>
    private static readonly (CONTROL_ID Id, string Name, string Type, bool Writable)[] PanelControls = {
        (CONTROL_ID.CONTROL_GAIN, "Gain", "int", true),
        (CONTROL_ID.CONTROL_OFFSET, "Offset", "int", true),
        (CONTROL_ID.CONTROL_USBTRAFFIC, "USB traffic", "int", true),
        (CONTROL_ID.CONTROL_SPEED, "Readout speed", "int", true),
        (CONTROL_ID.CONTROL_CURTEMP, "Sensor temperature", "float", false),
        (CONTROL_ID.CONTROL_CURPWM, "Cooler PWM", "int", false),
    };

    public IReadOnlyList<CameraControl> GetControls() {
        var list = new List<CameraControl>();
        if (!_connected) return list;
        try {
            lock (_sdk) {
                if (_handle == IntPtr.Zero) return list;
                foreach (var (id, name, type, writable) in PanelControls) {
                    if (!Has(id)) continue;
                    double min = 0, max = 0;
                    if (GetQHYCCDParamMinMaxStep(_handle, id, out var mn, out var mx, out _) == QHYCCD_SUCCESS) {
                        min = mn; max = mx;
                    }
                    var cur = GetQHYCCDParam(_handle, id);
                    if (IsParamError(cur)) cur = min;
                    // Gain is tracked here (see ApplyExposureGain), so report
                    // the value the next still will use.
                    if (id == CONTROL_ID.CONTROL_GAIN) cur = _gain;
                    if (id == CONTROL_ID.CONTROL_CURPWM && max <= 0) max = _pwmMax;
                    list.Add(new CameraControl(id.ToString(), name, null,
                        cur, min, max, cur, writable, false, false, type));
                }
            }
        } catch { }
        return list;
    }

    public bool SetControl(string id, double value, bool auto) {
        if (!_connected || !Enum.TryParse<CONTROL_ID>(id, out var cid)) return false;
        bool writable = false;
        foreach (var c in PanelControls) if (c.Id == cid) writable = c.Writable;
        if (!writable) return false;
        try {
            lock (_sdk) {
                if (_handle == IntPtr.Zero) return false;
                if (SetQHYCCDParam(_handle, cid, Math.Round(value)) != QHYCCD_SUCCESS) return false;
            }
            if (cid == CONTROL_ID.CONTROL_GAIN) _gain = (int)Math.Round(value);
            if (cid == CONTROL_ID.CONTROL_OFFSET) _offset = (int)Math.Round(value);
            return true;
        } catch { return false; }
    }
}
