using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using NINA.Core.Enum;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;

namespace NINA.Polaris.Services.Alpaca;

/// <summary>
/// Alpaca (ASCOM HTTP) camera adapter exposed through Polaris's
/// <see cref="ICamera"/> contract. Talks Alpaca v1 over HTTP via
/// <see cref="AlpacaClient"/> for the small request/response actions,
/// and uses a dedicated <see cref="HttpClient"/> with a 512 MiB buffer
/// for the large /imagearray (or /imagebytes) download.
///
/// Capture flow follows the Alpaca v1 Camera spec:
///   1. optionally PUT /gain, /binx, /biny
///   2. PUT /startexposure with Duration + Light
///   3. poll /imageready every 200 ms (AbortExposure on cancel)
///   4. cache /cameraxsize + /cameraysize + /sensortype
///   5. GET /imagearray :: try ImageBytes binary first, fall back to JSON
///   6. convert int -> ushort (clamped) row-major
///   7. wrap in BaseImageData
///
/// ImageBytes binary fast path reference:
/// https://www.ascom-standards.org/AlpacaDeveloper/AlpacaImageBytes.html
/// </summary>
public sealed class AlpacaCamera : ICamera, IDisposable {
    private readonly AlpacaClient _client;
    private readonly HttpClient _imageHttp;
    // Cached metadata: filled at ConnectAsync, properties don't change
    // at runtime so re-reading them on every status broadcast would be
    // wasteful (mirrors IndiCamera / AscomComCamera patterns).
    private string _deviceName = "Alpaca Camera";
    private int _maxX, _maxY;
    private double _pixelSizeX, _pixelSizeY;
    private int _bitDepth = 16;
    private int _sensorType; // 0=Mono 1=Color 2=RGGB 3=CMYG 4=CMYG2 5=LRGB
    private bool _canCool, _canBin, _canAbort, _hasGain;
    private int _maxBinX = 1;
    private int _gainMin, _gainMax;

    public string Host { get; }
    public int Port { get; }
    public int DeviceNumber { get; }

    public AlpacaCamera(string host, int port, int deviceNumber) {
        Host = host;
        Port = port;
        DeviceNumber = deviceNumber;
        _client = new AlpacaClient(host, port, "camera", deviceNumber);
        // Image fetches can be 50-100 MiB on full-frame sensors. Bump
        // the per-response buffer cap and disable HttpClient's default
        // 100 s timeout (long exposures can take minutes; we already
        // cooperate with the caller's CancellationToken).
        _imageHttp = new HttpClient {
            Timeout = Timeout.InfiniteTimeSpan,
            MaxResponseContentBufferSize = 512L * 1024 * 1024
        };
    }

    /// <summary>Parse a "host:port:deviceNumber" deviceId string into a
    /// new instance. Used by EquipmentManager so the rig record can
    /// store a single opaque string instead of three fields.</summary>
    public static AlpacaCamera FromDeviceId(string deviceId) {
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException("deviceId is empty", nameof(deviceId));
        var parts = deviceId.Split(':');
        if (parts.Length != 3
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
            || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var dev)) {
            throw new ArgumentException(
                $"Alpaca deviceId must be 'host:port:deviceNumber' (got '{deviceId}').",
                nameof(deviceId));
        }
        return new AlpacaCamera(parts[0], port, dev);
    }

    // ---- ICamera identity / state ----

    public string DeviceName => _deviceName;

    public bool IsConnected {
        get {
            try { return _client.GetAsync<bool>("connected").GetAwaiter().GetResult(); }
            catch { return false; }
        }
    }

    public CameraStates State {
        get {
            try {
                // Alpaca CameraState: 0=Idle 1=Waiting 2=Exposing
                // 3=Reading 4=Download 5=Error -- maps 1:1 to Polaris's
                // CameraStates enum.
                var v = _client.GetAsync<int>("camerastate").GetAwaiter().GetResult();
                return v switch {
                    0 => CameraStates.Idle,
                    1 => CameraStates.Waiting,
                    2 => CameraStates.Exposing,
                    3 => CameraStates.Reading,
                    4 => CameraStates.Download,
                    _ => CameraStates.Error
                };
            } catch { return CameraStates.NoState; }
        }
    }

    public double Temperature => SafeGet(() => _client.GetAsync<double>("ccdtemperature"), double.NaN);
    public bool CoolerOn => _canCool && SafeGet(() => _client.GetAsync<bool>("cooleron"), false);
    public double CoolerPower => _canCool ? SafeGet(() => _client.GetAsync<double>("coolerpower"), 0d) : 0d;

    public int BinX => SafeGet(() => _client.GetAsync<int>("binx"), 1);
    public int BinY => SafeGet(() => _client.GetAsync<int>("biny"), 1);

    public int BitDepth => _bitDepth;
    public int MaxX => _maxX;
    public int MaxY => _maxY;
    public double PixelSizeX => _pixelSizeX;
    public double PixelSizeY => _pixelSizeY;

    public int Gain => _hasGain ? SafeGet(() => _client.GetAsync<int>("gain"), 0) : 0;

    /// <summary>Alpaca cameras report gain, not ISO -- empty list signals
    /// the UI to hide the ISO dropdown.</summary>
    public IReadOnlyList<int> IsoOptions => Array.Empty<int>();
    public int SelectedIso => 0;

    public CameraCapabilities Capabilities => new(
        SupportsCooler: _canCool,
        SupportsBinning: _canBin,
        SupportsRoi: true,
        SupportsIso: false,
        SupportsBulb: false,
        SupportsVideoStream: false,
        SupportsWhiteBalance: false);

    // ---- Lifecycle ----

    public async Task ConnectAsync(CancellationToken ct = default) {
        await _client.PutAsync("connected",
            new Dictionary<string, string> { ["Connected"] = "true" }, ct);

        try { _deviceName = await _client.GetAsync<string>("name", ct) ?? "Alpaca Camera"; }
        catch { /* leave default */ }

        _maxX = await SafeGetAsync(() => _client.GetAsync<int>("cameraxsize", ct), 0);
        _maxY = await SafeGetAsync(() => _client.GetAsync<int>("cameraysize", ct), 0);
        _pixelSizeX = await SafeGetAsync(() => _client.GetAsync<double>("pixelsizex", ct), 0d);
        _pixelSizeY = await SafeGetAsync(() => _client.GetAsync<double>("pixelsizey", ct), 0d);
        _sensorType = await SafeGetAsync(() => _client.GetAsync<int>("sensortype", ct), 0);

        // Reset ROI to the full sensor. The ASCOM Remote Server (or
        // Alpaca Omni Simulator) hosts the same ICameraV3 driver as
        // the COM-direct path, but its StartX/StartY/NumX/NumY state
        // is sticky across connections — if a previous Alpaca client
        // ever subframed the sensor (or the server started with a
        // non-full-frame default), every subsequent capture comes
        // back smaller than MaxX × MaxY. The user noticed this as
        // "the preview is smaller via Alpaca than via ASCOM direct,
        // even though it's the same camera" — COM-direct doesn't
        // hit it because the ZWO driver appears to reset the ROI on
        // a fresh Activator.CreateInstance, while the long-lived
        // Alpaca server keeps the driver instance pinned. Explicit
        // writes here cost four extra HTTP calls per Connect (~80ms
        // on LAN) and are best-effort: any write failure leaves the
        // driver-side defaults in place, which is no worse than the
        // pre-fix behaviour.
        if (_maxX > 0 && _maxY > 0) {
            try {
                await _client.PutAsync("startx",
                    new Dictionary<string, string> { ["StartX"] = "0" }, ct);
                await _client.PutAsync("starty",
                    new Dictionary<string, string> { ["StartY"] = "0" }, ct);
                await _client.PutAsync("numx",
                    new Dictionary<string, string> {
                        ["NumX"] = _maxX.ToString(CultureInfo.InvariantCulture)
                    }, ct);
                await _client.PutAsync("numy",
                    new Dictionary<string, string> {
                        ["NumY"] = _maxY.ToString(CultureInfo.InvariantCulture)
                    }, ct);
            } catch { /* best-effort; some drivers reject ROI writes
                          on cameras that don't expose subframing */ }
        }

        var maxAdu = await SafeGetAsync(() => _client.GetAsync<long>("maxadu", ct), 65535L);
        _bitDepth = maxAdu switch {
            > 65535 => 32, > 16383 => 16, > 4095 => 14, > 1023 => 12, _ => 10
        };

        _canCool = await SafeGetAsync(() => _client.GetAsync<bool>("cansetccdtemperature", ct), false);
        _canAbort = await SafeGetAsync(() => _client.GetAsync<bool>("canabortexposure", ct), false);
        _maxBinX = await SafeGetAsync(() => _client.GetAsync<int>("maxbinx", ct), 1);
        _canBin = _maxBinX > 1;

        // Gain probe: some drivers throw on /gain when not implemented,
        // others return 0. The reliable signal is /gainmin + /gainmax
        // (mirrors the AscomComCamera pattern); if both succeed the
        // sensor exposes analogue gain.
        _hasGain = false;
        try {
            _gainMin = await _client.GetAsync<int>("gainmin", ct);
            _gainMax = await _client.GetAsync<int>("gainmax", ct);
            _hasGain = _gainMax > _gainMin;
        } catch { _hasGain = false; }
    }

    public async Task DisconnectAsync(CancellationToken ct = default) {
        try {
            await _client.PutAsync("connected",
                new Dictionary<string, string> { ["Connected"] = "false" }, ct);
        } catch { /* server may already be gone */ }
    }

    // ---- Capture ----

    public async Task<IImageData> CaptureAsync(double exposureSeconds,
            CaptureOptions? opts = null, CancellationToken ct = default) {
        // Per-capture overrides. Bin first because some drivers reset
        // ROI when binning changes.
        if (opts?.Gain is int g && _hasGain) {
            var clamped = Math.Clamp(g, _gainMin, _gainMax);
            await _client.PutAsync("gain",
                new Dictionary<string, string> { ["Gain"] = clamped.ToString(CultureInfo.InvariantCulture) }, ct);
        }
        if (_canBin) {
            if (opts?.BinX is int bx && bx >= 1) {
                await _client.PutAsync("binx",
                    new Dictionary<string, string> { ["BinX"] = bx.ToString(CultureInfo.InvariantCulture) }, ct);
            }
            if (opts?.BinY is int by && by >= 1) {
                await _client.PutAsync("biny",
                    new Dictionary<string, string> { ["BinY"] = by.ToString(CultureInfo.InvariantCulture) }, ct);
            }
        }

        var isLight = !string.Equals(opts?.ImageType, "DARK", StringComparison.OrdinalIgnoreCase)
                   && !string.Equals(opts?.ImageType, "BIAS", StringComparison.OrdinalIgnoreCase);

        await _client.PutAsync("startexposure", new Dictionary<string, string> {
            ["Duration"] = exposureSeconds.ToString(CultureInfo.InvariantCulture),
            ["Light"] = isLight ? "true" : "false"
        }, ct);

        // Poll ImageReady every 200 ms until true OR the caller
        // cancels. On cancel we call AbortExposure if the driver
        // supports it so the sensor isn't left holding the exposure.
        var deadline = DateTime.UtcNow.AddSeconds(exposureSeconds + 120);
        while (true) {
            if (ct.IsCancellationRequested) {
                if (_canAbort) {
                    try { await _client.PutAsync("abortexposure", null, CancellationToken.None); }
                    catch { /* state-dependent */ }
                }
                ct.ThrowIfCancellationRequested();
            }
            bool ready = false;
            try { ready = await _client.GetAsync<bool>("imageready", ct); } catch { /* keep polling */ }
            if (ready) break;
            if (DateTime.UtcNow > deadline) {
                throw new TimeoutException(
                    $"Alpaca ImageReady didn't go true within {exposureSeconds + 120} s.");
            }
            try { await Task.Delay(200, ct); }
            catch (OperationCanceledException) { /* loop top will rethrow after Abort */ }
        }

        // Make sure cached dimensions are populated -- some drivers only
        // expose final sensor size after the first exposure.
        if (_maxX <= 0 || _maxY <= 0) {
            _maxX = await SafeGetAsync(() => _client.GetAsync<int>("cameraxsize", ct), _maxX);
            _maxY = await SafeGetAsync(() => _client.GetAsync<int>("cameraysize", ct), _maxY);
        }

        var (px, width, height, channels) = await DownloadImageAsync(ct);

        var bayer = MapBayer(_sensorType);
        var props = new ImageProperties {
            Width = width,
            Height = height,
            BitDepth = _bitDepth,
            IsBayered = bayer != BayerPatternEnum.None,
            BayerPattern = bayer,
            Channels = channels
        };
        var meta = new ImageMetaData {
            Camera = new ImageMetaData.CameraInfo {
                Name = _deviceName,
                Temperature = SafeGet(() => _client.GetAsync<double>("ccdtemperature"), 0d),
                BinX = (short)BinX,
                BinY = (short)BinY,
                PixelSizeX = _pixelSizeX,
                PixelSizeY = _pixelSizeY,
                Gain = _hasGain ? Gain : 0,
                SensorType = MapSensorType(_sensorType),
                BayerPattern = bayer
            },
            Exposure = new ImageMetaData.ExposureInfo {
                ExposureTime = exposureSeconds,
                Filter = opts?.Filter ?? string.Empty,
                ImageType = opts?.ImageType ?? "LIGHT"
            },
            Target = string.IsNullOrEmpty(opts?.TargetName)
                ? new ImageMetaData.TargetInfo()
                : new ImageMetaData.TargetInfo { Name = opts!.TargetName! }
        };
        return new BaseImageData(px, props, meta);
    }

    public async Task SetBinningAsync(int binX, int binY, CancellationToken ct = default) {
        if (!_canBin) return;
        try {
            await _client.PutAsync("binx",
                new Dictionary<string, string> { ["BinX"] = Math.Max(1, binX).ToString(CultureInfo.InvariantCulture) }, ct);
            await _client.PutAsync("biny",
                new Dictionary<string, string> { ["BinY"] = Math.Max(1, binY).ToString(CultureInfo.InvariantCulture) }, ct);
        } catch { /* driver rejected the value */ }
    }

    public async Task SetTemperatureAsync(double temperature, CancellationToken ct = default) {
        if (!_canCool) return;
        try {
            await _client.PutAsync("setccdtemperature",
                new Dictionary<string, string> {
                    ["SetCCDTemperature"] = temperature.ToString(CultureInfo.InvariantCulture)
                }, ct);
            await _client.PutAsync("cooleron",
                new Dictionary<string, string> { ["CoolerOn"] = "true" }, ct);
        } catch { /* read-only or out-of-range */ }
    }

    public async Task SetCoolerAsync(bool on, CancellationToken ct = default) {
        if (!_canCool) return;
        try {
            await _client.PutAsync("cooleron",
                new Dictionary<string, string> { ["CoolerOn"] = on ? "true" : "false" }, ct);
        } catch { /* driver rejected */ }
    }

    /// <summary>Alpaca astronomy cameras don't expose ISO. No-op.</summary>
    public Task SetIsoAsync(int iso, CancellationToken ct = default) => Task.CompletedTask;

    public async Task AbortExposureAsync(CancellationToken ct = default) {
        if (!_canAbort) return;
        try { await _client.PutAsync("abortexposure", null, ct); }
        catch { /* state-dependent */ }
    }

    public async Task SetSubframeAsync(int x, int y, int width, int height,
            CancellationToken ct = default) {
        // w=0 OR h=0 = clear ROI per the ICamera contract.
        if (width <= 0 || height <= 0) {
            x = 0; y = 0;
            width = _maxX > 0 ? _maxX : 0;
            height = _maxY > 0 ? _maxY : 0;
        }
        try {
            await _client.PutAsync("startx",
                new Dictionary<string, string> { ["StartX"] = x.ToString(CultureInfo.InvariantCulture) }, ct);
            await _client.PutAsync("starty",
                new Dictionary<string, string> { ["StartY"] = y.ToString(CultureInfo.InvariantCulture) }, ct);
            await _client.PutAsync("numx",
                new Dictionary<string, string> { ["NumX"] = width.ToString(CultureInfo.InvariantCulture) }, ct);
            await _client.PutAsync("numy",
                new Dictionary<string, string> { ["NumY"] = height.ToString(CultureInfo.InvariantCulture) }, ct);
        } catch { /* driver rejected */ }
    }

    // ---- Video streaming: not in the Alpaca v1 camera spec. ----

    public bool IsStreaming => false;

    public IDisposable SubscribeVideoFrames(Action<IImageData> handler) => new NopSub();
    private sealed class NopSub : IDisposable { public void Dispose() { } }

    public Task StartVideoStreamAsync(VideoStreamOptions? opts = null, CancellationToken ct = default)
        => throw new NotSupportedException(
            "Alpaca v1 cameras don't expose a native video stream. Use loop mode instead.");

    public Task StopVideoStreamAsync(CancellationToken ct = default) => Task.CompletedTask;

    public void Dispose() {
        try { DisconnectAsync().GetAwaiter().GetResult(); } catch { }
        _imageHttp.Dispose();
    }

    // ====================================================================
    // ImageArray / ImageBytes download
    // ====================================================================

    /// <summary>
    /// Fetch the just-exposed frame. Tries the binary ImageBytes
    /// transport first (Accept: application/imagebytes); falls back to
    /// the JSON imagearray transport on 415/406 / wrong Content-Type.
    /// Returns row-major ushort pixels in (width, height, channels).
    /// </summary>
    private async Task<(ushort[] pixels, int width, int height, int channels)> DownloadImageAsync(CancellationToken ct) {
        // Reuse AlpacaClient's URL convention; we have to build the URL
        // by hand because we need full control over the request headers
        // and we deliberately use a fatter HttpClient instance.
        var url = $"http://{Host}:{Port}/api/v1/camera/{DeviceNumber}/imagearray" +
                  $"?ClientID=1&ClientTransactionID=0";

        // ImageBytes attempt -- much faster + smaller than JSON. The
        // server tells us which transport it actually used via the
        // response Content-Type so we don't have to trust our own
        // Accept header.
        using (var req = new HttpRequestMessage(HttpMethod.Get, url)) {
            req.Headers.Accept.Clear();
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/imagebytes"));
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var resp = await _imageHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

            if (resp.StatusCode == HttpStatusCode.UnsupportedMediaType
                || resp.StatusCode == HttpStatusCode.NotAcceptable) {
                // Old server, fall back to JSON below.
            } else {
                resp.EnsureSuccessStatusCode();
                var ct2 = resp.Content.Headers.ContentType?.MediaType;
                if (string.Equals(ct2, "application/imagebytes", StringComparison.OrdinalIgnoreCase)) {
                    var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                    return ParseImageBytes(bytes);
                }
                // Server returned JSON despite our Accept, fall through
                // and re-fetch as JSON (the body we already have is
                // valid JSON but it's simpler to just re-issue with the
                // streaming JSON path so we don't double-buffer).
            }
        }

        // JSON fallback path: stream the body straight into JsonDocument
        // so we don't materialise the entire string before parsing.
        using var stream = await _imageHttp.GetStreamAsync(url, ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return ParseImageArrayJson(doc.RootElement);
    }

    /// <summary>
    /// Parse the 44-byte AlpacaImageBytes metadata header + the raw
    /// int32 (or int16/uint8) little-endian pixel data. Reference:
    /// https://www.ascom-standards.org/AlpacaDeveloper/AlpacaImageBytes.html
    ///
    /// Header layout (all int32 LE):
    ///   [0]  MetadataVersion          (1)
    ///   [1]  ErrorNumber
    ///   [2]  ClientTransactionID
    ///   [3]  ServerTransactionID
    ///   [4]  DataStart                (offset to pixel data, usually 44)
    ///   [5]  ImageElementType         (1=int16 2=int32 6=byte 8=uint16 ...)
    ///   [6]  TransmissionElementType  (same enum)
    ///   [7]  Rank                     (2 mono, 3 colour)
    ///   [8]  Dimension1               (width)
    ///   [9]  Dimension2               (height)
    ///   [10] Dimension3               (channels, 0 for rank 2)
    /// </summary>
    private static (ushort[] pixels, int width, int height, int channels) ParseImageBytes(byte[] buf) {
        if (buf.Length < 44)
            throw new InvalidDataException($"ImageBytes payload too short ({buf.Length} bytes).");

        var errorNumber = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(4, 4));
        if (errorNumber != 0) {
            // ErrorMessage is a UTF-8 string starting at DataStart in
            // this case -- surface it verbatim so the toast is useful.
            var dataStartErr = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(16, 4));
            var msgLen = Math.Max(0, buf.Length - dataStartErr);
            var msg = msgLen > 0
                ? System.Text.Encoding.UTF8.GetString(buf, dataStartErr, msgLen)
                : "(no message)";
            throw new InvalidOperationException($"Alpaca ImageBytes error {errorNumber}: {msg}");
        }

        var dataStart = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(16, 4));
        var transmissionType = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(24, 4));
        var rank = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(28, 4));
        var width = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(32, 4));
        var height = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(36, 4));
        var dim3 = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(40, 4));
        var channels = (rank == 3 && dim3 > 0) ? dim3 : 1;

        var pixelCount = (long)width * height * channels;
        if (pixelCount <= 0)
            throw new InvalidDataException(
                $"ImageBytes header has invalid dimensions {width}x{height}x{channels}.");

        // ImageArrayElementType / TransmissionElementType enum (subset
        // we actually see in the wild). 0 = Unknown is sometimes used
        // by drivers that emit raw int32, so treat it as int32 too.
        var px = new ushort[pixelCount];
        var pixSpan = buf.AsSpan(dataStart);
        switch (transmissionType) {
            case 1: // Int16
                ReadInt16Le(pixSpan, px, width, height, channels);
                break;
            case 2: // Int32
            case 0: // Unknown -- assume int32 (the spec default)
                ReadInt32Le(pixSpan, px, width, height, channels);
                break;
            case 6: // Byte
                ReadByte(pixSpan, px, width, height, channels);
                break;
            case 8: // UInt16
                ReadUInt16Le(pixSpan, px, width, height, channels);
                break;
            default:
                // Best-effort: treat anything unknown as int32 and let
                // the caller see clamped pixels rather than crash.
                ReadInt32Le(pixSpan, px, width, height, channels);
                break;
        }
        return (px, width, height, channels);
    }

    /// <summary>
    /// Walk the JSON envelope's Value array. Avoids deserialising into
    /// int[][] (which allocates a heap object per row + per pixel). The
    /// Value shape is [width][height] for mono and [width][height][3]
    /// for colour, per Alpaca v1.
    /// </summary>
    private static (ushort[] pixels, int width, int height, int channels) ParseImageArrayJson(JsonElement root) {
        var errNum = root.TryGetProperty("ErrorNumber", out var en) ? en.GetInt32() : 0;
        if (errNum != 0) {
            var msg = root.TryGetProperty("ErrorMessage", out var em) ? em.GetString() : "(no message)";
            throw new InvalidOperationException($"Alpaca imagearray error {errNum}: {msg}");
        }
        if (!root.TryGetProperty("Value", out var value) || value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Alpaca imagearray response missing Value array.");

        var width = value.GetArrayLength();
        if (width == 0)
            throw new InvalidDataException("Alpaca imagearray Value has width 0.");

        // Look at Value[0] to figure out height + rank.
        var col0 = value[0];
        if (col0.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Alpaca imagearray Value[0] is not an array.");
        var height = col0.GetArrayLength();
        var channels = 1;
        if (height > 0 && col0[0].ValueKind == JsonValueKind.Array) {
            channels = col0[0].GetArrayLength();
        }

        var px = new ushort[(long)width * height * channels];

        int x = 0;
        foreach (var column in value.EnumerateArray()) {
            int y = 0;
            foreach (var cell in column.EnumerateArray()) {
                if (channels == 1) {
                    px[y * width + x] = ClampU16(cell.GetInt32());
                } else {
                    int c = 0;
                    foreach (var chanVal in cell.EnumerateArray()) {
                        if (c >= channels) break;
                        // Plane-sequential layout (matches BaseImageData
                        // / ImageProperties.Channels=3 convention: R
                        // plane first, then G, then B).
                        var planeOffset = (long)c * width * height;
                        px[planeOffset + y * width + x] = ClampU16(chanVal.GetInt32());
                        c++;
                    }
                }
                y++;
            }
            x++;
        }
        return (px, width, height, channels);
    }

    // ====================================================================
    // helpers
    // ====================================================================

    private static void ReadInt32Le(ReadOnlySpan<byte> src, ushort[] dst, int w, int h, int ch) {
        // Source order: column-major (Alpaca convention is value[x][y]
        // for both transports). Convert to row-major + plane-sequential
        // so BaseImageData consumers see the layout they expect.
        int idx = 0;
        for (int x = 0; x < w; x++) {
            for (int y = 0; y < h; y++) {
                for (int c = 0; c < ch; c++) {
                    var v = BinaryPrimitives.ReadInt32LittleEndian(src.Slice(idx * 4, 4));
                    var planeOffset = (long)c * w * h;
                    dst[planeOffset + y * w + x] = ClampU16(v);
                    idx++;
                }
            }
        }
    }

    private static void ReadInt16Le(ReadOnlySpan<byte> src, ushort[] dst, int w, int h, int ch) {
        int idx = 0;
        for (int x = 0; x < w; x++) {
            for (int y = 0; y < h; y++) {
                for (int c = 0; c < ch; c++) {
                    var v = BinaryPrimitives.ReadInt16LittleEndian(src.Slice(idx * 2, 2));
                    var planeOffset = (long)c * w * h;
                    dst[planeOffset + y * w + x] = ClampU16(v);
                    idx++;
                }
            }
        }
    }

    private static void ReadUInt16Le(ReadOnlySpan<byte> src, ushort[] dst, int w, int h, int ch) {
        int idx = 0;
        for (int x = 0; x < w; x++) {
            for (int y = 0; y < h; y++) {
                for (int c = 0; c < ch; c++) {
                    var v = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(idx * 2, 2));
                    var planeOffset = (long)c * w * h;
                    dst[planeOffset + y * w + x] = v;
                    idx++;
                }
            }
        }
    }

    private static void ReadByte(ReadOnlySpan<byte> src, ushort[] dst, int w, int h, int ch) {
        int idx = 0;
        for (int x = 0; x < w; x++) {
            for (int y = 0; y < h; y++) {
                for (int c = 0; c < ch; c++) {
                    // Scale 8-bit -> 16-bit so downstream stretchers
                    // don't have to special-case the range.
                    var v = src[idx] << 8;
                    var planeOffset = (long)c * w * h;
                    dst[planeOffset + y * w + x] = (ushort)v;
                    idx++;
                }
            }
        }
    }

    private static ushort ClampU16(int v) =>
        v < 0 ? (ushort)0 : v > 65535 ? (ushort)65535 : (ushort)v;

    // Alpaca SensorType:  0=Mono  1=Color(no bayer)  2=RGGB  3=CMYG  4=CMYG2  5=LRGB
    private static SensorType MapSensorType(int alpaca) => alpaca switch {
        0 => Core.Enum.SensorType.Monochrome,
        1 => Core.Enum.SensorType.Color,
        2 => Core.Enum.SensorType.RGGB,
        3 => Core.Enum.SensorType.CMYG,
        4 => Core.Enum.SensorType.CMYG2,
        5 => Core.Enum.SensorType.LRGB,
        _ => Core.Enum.SensorType.Monochrome
    };

    // Only sensorType 2 maps to a BayerPattern that BayerPatternEnum
    // knows about. Alpaca's CMYG variants don't have an entry in our
    // enum so they fall through to None (debayering still uses the
    // SensorType field above).
    private static BayerPatternEnum MapBayer(int alpaca) => alpaca switch {
        2 => BayerPatternEnum.RGGB,
        _ => BayerPatternEnum.None
    };

    private static T SafeGet<T>(Func<Task<T>> read, T fallback) {
        try { return read().GetAwaiter().GetResult(); }
        catch { return fallback; }
    }

    private static async Task<T> SafeGetAsync<T>(Func<Task<T>> read, T fallback) {
        try { return await read(); }
        catch { return fallback; }
    }
}
