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

    private int _gain;
    private double _exposureSec = 0.03;
    private int _roiX, _roiY, _roiW, _roiH, _bin = 1;
    private POAImgFormat _imgFormat = POAImgFormat.POA_RAW16;

    private readonly ConcurrentDictionary<int, Action<IImageData>> _streamSubs = new();
    private int _nextSubId;
    private volatile bool _streaming;
    private Thread? _streamThread;
    private CancellationTokenSource? _streamCts;
    private readonly object _gate = new();

    public PlayerOneSdkCamera(string deviceId) {
        PlayerOneRegistry.EnsureResolver();
        _cameraId = int.TryParse(deviceId, out var id) ? id : 0;
        DeviceName = $"PlayerOne #{_cameraId}";
    }

    public string DeviceName { get; private set; }
    public bool IsConnected => _connected;
    public CameraStates State { get; private set; } = CameraStates.NoState;

    public double Temperature => _connected ? ReadFloat(POAConfig.POA_TEMPERATURE) : double.NaN;
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

        _imgFormat = _bitDepth > 8 ? POAImgFormat.POA_RAW16 : POAImgFormat.POA_RAW8;
        POASetImageFormat(_cameraId, _imgFormat);
        _roiX = 0; _roiY = 0; _roiW = _maxX; _roiH = _maxY; _bin = 1;
        POASetImageBin(_cameraId, 1);
        POASetImageSize(_cameraId, _maxX, _maxY);
        POASetImageStartPos(_cameraId, 0, 0);
        _gain = ReadInt(POAConfig.POA_GAIN);
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
            POASetConfig(_cameraId, POAConfig.POA_TARGET_TEMP, POAConfigValue.Int((int)Math.Round(temperature)), POABool.POA_FALSE);
        return Task.CompletedTask;
    }

    public Task SetCoolerAsync(bool on, CancellationToken ct = default) {
        if (_supportsCooler)
            POASetConfig(_cameraId, POAConfig.POA_COOLER, POAConfigValue.Bool(on), POABool.POA_FALSE);
        return Task.CompletedTask;
    }

    public Task SetIsoAsync(int iso, CancellationToken ct = default) => Task.CompletedTask;

    public Task AbortExposureAsync(CancellationToken ct = default) {
        try { POAStopExposure(_cameraId); } catch { }
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
        // PlayerOne wants width % 4 == 0 and height % 2 == 0 (post-bin).
        int w = Math.Max(4, (_roiW / _bin) & ~3);
        int h = Math.Max(2, (_roiH / _bin) & ~1);
        POASetImageBin(_cameraId, _bin);
        POASetImageSize(_cameraId, w, h);
        POASetImageStartPos(_cameraId, _roiX / _bin, _roiY / _bin);
    }

    public Task<IImageData> CaptureAsync(double exposureSeconds, CaptureOptions? opts = null,
                                         CancellationToken ct = default) => Task.Run<IImageData>(() => {
        lock (_gate) {
            if (_streaming) throw new InvalidOperationException("Stop the video stream before a still exposure.");
            // Re-assert the 16-bit pixel format before the still, mirroring the
            // SVBony native path. We own the SDK handle exclusively so it can't
            // be flipped to RAW8 like an external INDI driver can, but this
            // keeps the native backends consistent + future-proof.
            POASetImageFormat(_cameraId, _imgFormat);
            ApplyExposureGain(exposureSeconds, opts?.Gain);
            GetRoi(out var w, out var h);
            var bytes = new byte[(long)w * h * BytesPerPixel()];
            int waitMs = (int)(exposureSeconds * 1000 * 2 + 500);
            State = CameraStates.Exposing;
            Check(POAStartExposure(_cameraId, POABool.POA_TRUE), "POAStartExposure");
            try {
                Check(POAGetImageData(_cameraId, bytes, new CLong(bytes.Length), waitMs), "POAGetImageData");
            } finally {
                try { POAStopExposure(_cameraId); } catch { }
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
                Check(POAStartExposure(_cameraId, POABool.POA_FALSE), "POAStartExposure");
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
        try { t?.Join(2000); } catch { }
        try { POAStopExposure(_cameraId); } catch { }
        State = CameraStates.Idle;
    }

    private void PullLoop(CancellationToken ct) {
        GetRoi(out var w, out var h);
        var buf = new byte[(long)w * h * BytesPerPixel()];
        int waitMs = (int)(_exposureSec * 1000 * 2 + 500);
        while (!ct.IsCancellationRequested && _streaming) {
            var err = POAGetImageData(_cameraId, buf, new CLong(buf.Length), waitMs);
            if (err == POAErrors.POA_ERROR_TIMEOUT) continue;
            if (err != POAErrors.POA_OK) continue;
            IImageData frame;
            try { frame = WrapFrame(buf, w, h); } catch { continue; }
            foreach (var s in _streamSubs.Values) { try { s(frame); } catch { } }
        }
    }

    private void ApplyExposureGain(double exposureSeconds, int? gainOverride) {
        _exposureSec = exposureSeconds > 0 ? exposureSeconds : _exposureSec;
        POASetConfig(_cameraId, POAConfig.POA_EXPOSURE,
            POAConfigValue.Int((int)Math.Round(_exposureSec * 1_000_000)), POABool.POA_FALSE);
        if (gainOverride is int g) _gain = g;
        POASetConfig(_cameraId, POAConfig.POA_GAIN, POAConfigValue.Int(_gain), POABool.POA_FALSE);
    }

    private int BytesPerPixel() => _imgFormat == POAImgFormat.POA_RAW16 ? 2 : 1;

    private void GetRoi(out int w, out int h) {
        if (POAGetImageSize(_cameraId, out var rw, out var rh) == POAErrors.POA_OK && rw > 0 && rh > 0) {
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
        meta.Camera.PixelSizeX = _pixelSize;
        meta.Camera.PixelSizeY = _pixelSize;
        return new BaseImageData(pixels, props, meta);
    }

    private int ReadInt(POAConfig c) {
        try {
            var v = new POAConfigValue(); var a = POABool.POA_FALSE;
            if (POAGetConfig(_cameraId, c, ref v, ref a) == POAErrors.POA_OK) return v.intValue;
        } catch { }
        return 0;
    }

    private double ReadFloat(POAConfig c) {
        try {
            var v = new POAConfigValue(); var a = POABool.POA_FALSE;
            if (POAGetConfig(_cameraId, c, ref v, ref a) == POAErrors.POA_OK) return v.floatValue;
        } catch { }
        return double.NaN;
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