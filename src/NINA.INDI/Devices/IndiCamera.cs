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

using NINA.Core.Enum;
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.INDI.Client;
using NINA.INDI.Protocol;

namespace NINA.INDI.Devices;

public class IndiCamera : ICamera {
    private readonly IndiClient _client;
    private TaskCompletionSource<IImageData>? _exposureTcs;
    // Native CCD_VIDEO_STREAM subscribers, added by CameraStreamService
    // when a native stream is active. Frames arrive via OnBlobReceived
    // and fan out to every subscriber. Concurrent for safety against
    // late-arriving BLOBs after Stop.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, Action<IImageData>> _streamSubscribers = new();
    private int _nextSubscriberId;
    private volatile bool _isStreaming;
    // Counter of stream BLOBs that parsed as empty (FITSReader returned
    // Width=0 / Height=0, typically a driver that doesn't actually
    // emit FITS under CCD_VIDEO_STREAM). CameraStreamService reads this
    // to decide whether native streaming is producing usable frames or
    // whether it should bail out to loop mode.
    private int _emptyStreamFrameCount;
    public int EmptyStreamFrameCount => _emptyStreamFrameCount;

    public string DeviceName { get; }
    /// <summary>
    /// True only when the INDI client is up AND the device's per-device
    /// CONNECTION switch is in the CONNECT state. The legacy
    /// implementation just delegated to <c>_client.IsConnected</c> (the
    /// global server link), so the property reported true even after
    /// the user disconnected the device through the UI, causing the
    /// frontend toggle to flip itself back on within the next status
    /// tick. Reading the actual CONNECTION switch fixes that.
    /// </summary>
    public bool IsConnected
        => _client.IsConnected
           && _client.GetSwitch(DeviceName, "CONNECTION", "CONNECT");

    public CameraStates State {
        get {
            var prop = _client.GetProperty(DeviceName, "CCD_EXPOSURE");
            if (prop == null) return CameraStates.NoState;
            return prop.State switch {
                IndiPropertyState.Busy => CameraStates.Exposing,
                IndiPropertyState.Ok => CameraStates.Idle,
                IndiPropertyState.Alert => CameraStates.Error,
                _ => CameraStates.Idle
            };
        }
    }

    public double Temperature => _client.GetNumber(DeviceName, "CCD_TEMPERATURE", "CCD_TEMPERATURE_VALUE");
    public int BinX => (int)_client.GetNumber(DeviceName, "CCD_BINNING", "HOR_BIN");
    public int BinY => (int)_client.GetNumber(DeviceName, "CCD_BINNING", "VER_BIN");
    public bool CoolerOn => _client.GetSwitch(DeviceName, "CCD_COOLER", "COOLER_ON");
    public double CoolerPower => _client.GetNumber(DeviceName, "CCD_COOLER_POWER", "CCD_COOLER_VALUE");
    public int MaxX => (int)_client.GetNumber(DeviceName, "CCD_INFO", "CCD_MAX_X");
    public int MaxY => (int)_client.GetNumber(DeviceName, "CCD_INFO", "CCD_MAX_Y");
    public double PixelSizeX => _client.GetNumber(DeviceName, "CCD_INFO", "CCD_PIXEL_SIZE_X");
    public double PixelSizeY => _client.GetNumber(DeviceName, "CCD_INFO", "CCD_PIXEL_SIZE_Y");
    public int BitDepth => (int)_client.GetNumber(DeviceName, "CCD_INFO", "CCD_BITSPERPIXEL");

    /// <summary>Smallest exposure the driver advertises for
    /// <c>CCD_EXPOSURE_VALUE</c> (the number element's <c>min</c>).
    /// Used to satisfy BIAS / zero-second requests: most INDI drivers
    /// will not start a readout at exactly 0 s, so we clamp up to this
    /// instead. Returns 0 when the driver doesn't advertise a positive
    /// minimum (the caller falls back to a tiny default).</summary>
    public double MinExposure {
        get {
            var prop = _client.GetProperty(DeviceName, "CCD_EXPOSURE") as IndiNumberProperty;
            if (prop != null
                && prop.Values.TryGetValue("CCD_EXPOSURE_VALUE", out var el)
                && el.Min > 0) {
                return el.Min;
            }
            return 0;
        }
    }

    /// <summary>Read the Bayer mosaic pattern from the INDI
    /// <c>CCD_CFA</c> text property (<c>CFA_TYPE</c> element).
    /// Most OSC drivers (indi_asi_ccd, indi_svbony_ccd, indi_qhy_ccd,
    /// etc.) advertise this when the camera is connected. The FITS
    /// BLOBs they emit typically do NOT include a BAYERPAT keyword,
    /// so this is the only reliable source for the Bayer pattern on
    /// the INDI side. Returns <see cref="BayerPatternEnum.None"/>
    /// for mono cameras or drivers that don't publish CCD_CFA.</summary>
    public BayerPatternEnum BayerPattern {
        get {
            var cfaProp = _client.GetProperty(DeviceName, "CCD_CFA") as IndiTextProperty;
            if (cfaProp == null) return BayerPatternEnum.None;
            if (!cfaProp.Values.TryGetValue("CFA_TYPE", out var cfaType))
                return BayerPatternEnum.None;
            return (cfaType?.Trim().ToUpperInvariant()) switch {
                "RGGB" => BayerPatternEnum.RGGB,
                "BGGR" => BayerPatternEnum.BGGR,
                "GBRG" => BayerPatternEnum.GBRG,
                "GRBG" => BayerPatternEnum.GRBG,
                _ => BayerPatternEnum.None
            };
        }
    }

    // INDI cameras don't surface gain in a standardised property, the
    // CCD_CONTROLS group varies by driver (gain / Gain / GAIN). Plumb a
    // best-effort read here and return 0 when nothing matches.
    public int Gain => (int)_client.GetNumber(DeviceName, "CCD_CONTROLS", "Gain");

    // ISO is not part of the INDI CCD spec, astronomy cameras report
    // analogue gain instead. Empty list signals the UI to hide the ISO
    // dropdown for INDI cameras.
    public IReadOnlyList<int> IsoOptions => Array.Empty<int>();
    public int SelectedIso => 0;

    /// <summary>Per-instance capabilities, SupportsVideoStream gets
    /// recomputed lazily from whether the driver advertises
    /// <c>CCD_VIDEO_STREAM</c> (most ZWO/QHY/gphoto drivers do).
    /// SupportsWhiteBalance flips on when CCD_CONTROLS exposes
    /// <c>WB_R</c> + <c>WB_B</c> elements (typical for ZWO/QHY OSC
    /// cameras, absent on mono).</summary>
    public CameraCapabilities Capabilities {
        get {
            var supportsStream = _client.GetProperty(DeviceName, "CCD_VIDEO_STREAM") != null;
            var ctrl = _client.GetProperty(DeviceName, "CCD_CONTROLS") as IndiNumberProperty;
            var supportsWb = ctrl?.Values.ContainsKey("WB_R") == true
                          && ctrl.Values.ContainsKey("WB_B");
            return CameraCapabilities.Astro with {
                SupportsVideoStream = supportsStream,
                SupportsWhiteBalance = supportsWb
            };
        }
    }

    /// <summary>Live WB_R reading; 50 (driver-typical neutral) when not exposed.</summary>
    public double WhiteBalanceR {
        get {
            var v = _client.GetNumber(DeviceName, "CCD_CONTROLS", "WB_R");
            return v > 0 ? v : 50;
        }
    }
    public double WhiteBalanceB {
        get {
            var v = _client.GetNumber(DeviceName, "CCD_CONTROLS", "WB_B");
            return v > 0 ? v : 50;
        }
    }

    /// <summary>WB range taken from the WB_R element the driver advertises
    /// (R + B share the same range on every OSC driver we've seen).
    /// Falls back to the 0..100 ICamera default if the element or its
    /// min/max aren't published yet.</summary>
    public double WhiteBalanceMin => WbElement()?.Min ?? 0;
    public double WhiteBalanceMax {
        get {
            var el = WbElement();
            return el != null && el.Max > el.Min ? el.Max : 100;
        }
    }

    private IndiNumberElement? WbElement() {
        var ctrl = _client.GetProperty(DeviceName, "CCD_CONTROLS") as IndiNumberProperty;
        if (ctrl != null && ctrl.Values.TryGetValue("WB_R", out var el)) return el;
        return null;
    }

    /// <summary>Gain range from the CCD_CONTROLS gain element (the key
    /// varies by driver: Gain / gain / GAIN). 0/0 when the driver doesn't
    /// publish CCD_CONTROLS or a gain element (e.g. the CCD Simulator).</summary>
    public int GainMin => GainElement() is { } el ? (int)el.Min : 0;
    public int GainMax {
        get { var el = GainElement(); return el != null && el.Max > el.Min ? (int)el.Max : 0; }
    }

    private IndiNumberElement? GainElement() {
        var ctrl = _client.GetProperty(DeviceName, "CCD_CONTROLS") as IndiNumberProperty;
        if (ctrl == null) return null;
        foreach (var k in new[] { "Gain", "gain", "GAIN" })
            if (ctrl.Values.TryGetValue(k, out var el)) return el;
        return null;
    }

    /// <summary>Write gain into CCD_CONTROLS only if the driver actually
    /// advertises that property + a matching element. Some drivers
    /// (notably indi_simulator_ccd) never publish CCD_CONTROLS at all,
    /// and sending it triggers a "Property CCD_CONTROLS is not defined"
    /// dispatch error in indiserver's log. Also handles driver-specific
    /// casing, Gain (most), gain (a few), GAIN (rare).</summary>
    private async Task TrySetGainAsync(int gain, CancellationToken ct) {
        var ctrl = _client.GetProperty(DeviceName, "CCD_CONTROLS") as IndiNumberProperty;
        if (ctrl == null) return;   // driver doesn't expose CCD_CONTROLS (e.g. CCD Simulator)
        string? key = null;
        foreach (var candidate in new[] { "Gain", "gain", "GAIN" }) {
            if (ctrl.Values.ContainsKey(candidate)) { key = candidate; break; }
        }
        if (key == null) return;   // CCD_CONTROLS exists but no gain element
        try {
            await _client.SetNumberAsync(DeviceName, "CCD_CONTROLS",
                new Dictionary<string, double> { [key] = gain }, ct);
        } catch { /* driver rejected the value (out of range?), non-fatal */ }
    }

    /// <summary>Write offset into CCD_CONTROLS only when the driver advertises
    /// it. Same casing tolerance as gain (Offset / offset / OFFSET). Offset is
    /// the sensor bias pedestal — leaving it at 0 pins the background near
    /// black and clips the left of the histogram; most OSC/CMOS rigs want a
    /// small positive offset (per-rig DefaultOffset).</summary>
    private async Task TrySetOffsetAsync(int offset, CancellationToken ct) {
        var ctrl = _client.GetProperty(DeviceName, "CCD_CONTROLS") as IndiNumberProperty;
        if (ctrl == null) return;
        string? key = null;
        foreach (var candidate in new[] { "Offset", "offset", "OFFSET" }) {
            if (ctrl.Values.ContainsKey(candidate)) { key = candidate; break; }
        }
        if (key == null) return;   // driver has no offset element
        try {
            await _client.SetNumberAsync(DeviceName, "CCD_CONTROLS",
                new Dictionary<string, double> { [key] = offset }, ct);
        } catch { /* out of range / rejected — non-fatal */ }
    }

    // ── Capture bit-depth (RAW16) enforcement ──────────────────────────
    // The SVBONY SV405CC (and ASI) INDI drivers expose a switch to pick the
    // frame format. If it gets left on RAW8 — e.g. after a fast video-stream
    // session, or a stale driver default — a 60 s light comes back as 8-bit
    // data stuffed into a 16-bit FITS (pixel max stuck at 255), so the frame
    // looks almost black no matter the stretch. Real capture apps select the
    // 16-bit RAW format for stills; do the same before every capture so a
    // light (or guide frame) is never silently 8-bit. Resolved once, then a
    // cheap "already 16-bit?" check on each capture.
    private string? _formatProp;     // "CCD_CAPTURE_FORMAT" / "CCD_VIDEO_FORMAT", or null
    private string? _raw16Element;   // switch element name of the 16-bit RAW format
    private string? _fitsElement;    // FORMAT_FITS element of CCD_TRANSFER_FORMAT ("Encode")
    private bool _raw16Forced;       // have we written RAW16 at least once this session?

    private async Task EnsureRaw16FormatAsync(CancellationToken ct) {
        // Resolve the format property + 16-bit element. Re-probe each capture
        // until found (the property may not be enumerated yet right after
        // connect); two dictionary lookups, negligible. INDI standardised
        // CCD_CAPTURE_FORMAT (1.9+); older drivers use CCD_VIDEO_FORMAT.
        // Element names are driver-defined (e.g. "SVB_IMG_RAW16",
        // "ASI_IMG_RAW16", "RAW 16-bit"), so match any element carrying "16"
        // that isn't an RGB/colour format.
        if (_raw16Element == null) {
            foreach (var propName in new[] { "CCD_CAPTURE_FORMAT", "CCD_VIDEO_FORMAT" }) {
                if (_client.GetProperty(DeviceName, propName) is IndiSwitchProperty sw && sw.Values.Count > 0) {
                    string? el = null;
                    foreach (var k in sw.Values.Keys) {
                        var u = k.ToUpperInvariant();
                        if (u.Contains("16") && !u.Contains("RGB")) { el = k; break; }
                    }
                    if (el != null) { _formatProp = propName; _raw16Element = el; break; }
                }
            }
        }
        if (_formatProp == null || _raw16Element == null) return;   // 8-bit-only / no such property
        if (_client.GetProperty(DeviceName, _formatProp) is not IndiSwitchProperty cur) return;
        // Already on 16-bit? Normally skip — switching re-inits some drivers
        // and can reset gain/offset, so only write when needed. BUT the SVBONY
        // SV405CC driver REPORTS SVB_IMG_RAW16 as the selected element while
        // still delivering RAW8 frames until the switch is actually written —
        // a state/output desync. So on the first call of each session
        // (_raw16Forced == false) we write it even when it reads as already
        // on, which forces the driver to truly apply 16-bit. Subsequent
        // captures keep the cheap skip-if-on behaviour.
        bool alreadyOn = cur.Values.TryGetValue(_raw16Element, out var on) && on;
        if (alreadyOn && _raw16Forced) return;
        var payload = new Dictionary<string, bool>();
        foreach (var k in cur.Values.Keys) payload[k] = (k == _raw16Element);
        try {
            await _client.SetSwitchAsync(DeviceName, _formatProp, payload, ct);
            _raw16Forced = true;
        } catch { /* driver rejected; non-fatal — manual INDI panel still works */ }
    }

    // Force the transfer/encode format to FITS (CCD_TRANSFER_FORMAT, labelled
    // "Encode" in the INDI panel, elements FORMAT_FITS / FORMAT_NATIVE /
    // FORMAT_XISF). Our BLOB pipeline decodes FITS; if a driver is left on
    // FORMAT_NATIVE the frame arrives in a raw blob we can't parse. Cheap
    // re-probe + "already FITS?" guard, same pattern as the RAW16 selector.
    private async Task EnsureFitsTransferAsync(CancellationToken ct) {
        if (_fitsElement == null) {
            if (_client.GetProperty(DeviceName, "CCD_TRANSFER_FORMAT") is IndiSwitchProperty sw
                    && sw.Values.Count > 0) {
                foreach (var k in sw.Values.Keys) {
                    if (k.ToUpperInvariant().Contains("FITS")) { _fitsElement = k; break; }
                }
            }
        }
        if (_fitsElement == null) return;   // no transfer-format property on this driver
        if (_client.GetProperty(DeviceName, "CCD_TRANSFER_FORMAT") is not IndiSwitchProperty cur) return;
        if (cur.Values.TryGetValue(_fitsElement, out var on) && on) return;   // already FITS
        var payload = new Dictionary<string, bool>();
        foreach (var k in cur.Values.Keys) payload[k] = (k == _fitsElement);
        try {
            await _client.SetSwitchAsync(DeviceName, "CCD_TRANSFER_FORMAT", payload, ct);
        } catch { /* driver rejected; non-fatal */ }
    }

    /// <summary>Writes WB_R and WB_B into CCD_CONTROLS. Silent skip if
    /// the driver doesn't have one of the keys.</summary>
    public async Task SetWhiteBalanceAsync(double red, double blue, CancellationToken ct = default) {
        var ctrl = _client.GetProperty(DeviceName, "CCD_CONTROLS") as IndiNumberProperty;
        if (ctrl == null) return;
        var values = new Dictionary<string, double>();
        if (ctrl.Values.ContainsKey("WB_R")) values["WB_R"] = red;
        if (ctrl.Values.ContainsKey("WB_B")) values["WB_B"] = blue;
        if (values.Count == 0) return;
        await _client.SetNumberAsync(DeviceName, "CCD_CONTROLS", values, ct);
    }

    public bool IsStreaming => _isStreaming;

    public IDisposable SubscribeVideoFrames(Action<IImageData> handler) {
        var id = System.Threading.Interlocked.Increment(ref _nextSubscriberId);
        _streamSubscribers[id] = handler;
        return new StreamSubscription(this, id);
    }

    private sealed class StreamSubscription : IDisposable {
        private readonly IndiCamera _cam;
        private readonly int _id;
        public StreamSubscription(IndiCamera cam, int id) { _cam = cam; _id = id; }
        public void Dispose() => _cam._streamSubscribers.TryRemove(_id, out _);
    }

    /// <summary>Toggle the driver's <c>CCD_VIDEO_STREAM</c> switch ON.
    /// Frame cadence is whatever the driver chooses (often configurable
    /// via <c>STREAMING_EXPOSURE</c> + <c>FPS</c> properties on the device).</summary>
    public async Task StartVideoStreamAsync(VideoStreamOptions? opts = null, CancellationToken ct = default) {
        if (!Capabilities.SupportsVideoStream)
            throw new NotSupportedException(
                $"INDI device {DeviceName} does not expose CCD_VIDEO_STREAM. Use loop mode instead.");

        // Honour optional per-stream overrides where the driver exposes
        // the matching properties. Silently skip when absent, different
        // drivers expose different subset of streaming knobs.
        if (opts?.ExposureSeconds is double exp && exp > 0) {
            try {
                await _client.SetNumberAsync(DeviceName, "STREAMING_EXPOSURE",
                    new Dictionary<string, double> { ["STREAMING_EXPOSURE_VALUE"] = exp }, ct);
            } catch { /* property may not exist on this driver */ }
        }
        if (opts?.Gain is int g) {
            await TrySetGainAsync(g, ct);
        }

        Interlocked.Exchange(ref _emptyStreamFrameCount, 0);
        _isStreaming = true;
        await _client.SetSwitchAsync(DeviceName, "CCD_VIDEO_STREAM",
            new Dictionary<string, bool> { ["STREAM_ON"] = true, ["STREAM_OFF"] = false }, ct);
    }

    public async Task StopVideoStreamAsync(CancellationToken ct = default) {
        if (!_isStreaming) return;
        _isStreaming = false;
        try {
            await _client.SetSwitchAsync(DeviceName, "CCD_VIDEO_STREAM",
                new Dictionary<string, bool> { ["STREAM_ON"] = false, ["STREAM_OFF"] = true }, ct);
        } catch { /* driver may already be torn down; nothing to do */ }
    }

    public IndiCamera(IndiClient client, string deviceName) {
        _client = client;
        DeviceName = deviceName;

        _client.BlobReceived += OnBlobReceived;
        _client.PropertyChanged += OnPropertyChanged;
    }

    public async Task ConnectAsync(CancellationToken ct = default) {
        // New session: force the RAW16 write once even if the driver's
        // format switch already *reports* 16-bit (see _raw16Forced).
        _raw16Forced = false;
        await _client.ConnectDeviceAsync(DeviceName, ct);
        // EnableBLOB is idempotent — INDI just notes the preference for
        // future BLOB delivery. Always re-send after connect so a
        // restarted camera driver still streams FITS frames to us.
        await _client.EnableBlobAsync(DeviceName, ct);
    }

    public Task DisconnectAsync(CancellationToken ct = default)
        => _client.DisconnectDeviceAsync(DeviceName, ct);

    public async Task SetBinningAsync(int binX, int binY, CancellationToken ct = default) {
        await _client.SetNumberAsync(DeviceName, "CCD_BINNING",
            new Dictionary<string, double> { ["HOR_BIN"] = binX, ["VER_BIN"] = binY }, ct);
    }

    public async Task SetTemperatureAsync(double temperature, CancellationToken ct = default) {
        // CCD_TEMPERATURE is read-only on uncooled cameras (ZWO ASI715MC,
        // most planetary CMOS). On those drivers writing it raises a
        // "Cannot set read-only property" dispatch error. Probe the
        // property, if it exists at all on a cooled camera, it's
        // writable; if missing we don't have a cooler to talk to.
        var prop = _client.GetProperty(DeviceName, "CCD_TEMPERATURE") as IndiNumberProperty;
        if (prop == null) return;
        try {
            await _client.SetNumberAsync(DeviceName, "CCD_TEMPERATURE",
                new Dictionary<string, double> { ["CCD_TEMPERATURE_VALUE"] = temperature }, ct);
        } catch { /* read-only or out-of-range on this driver, silent */ }
    }

    public async Task SetCoolerAsync(bool on, CancellationToken ct = default) {
        // CCD_COOLER doesn't exist on uncooled cameras. Without this
        // guard the indiserver log fills with "Property CCD_COOLER is
        // not defined in ZWO CCD ASI715MC" on every UI toggle.
        var prop = _client.GetProperty(DeviceName, "CCD_COOLER") as IndiSwitchProperty;
        if (prop == null) return;
        try {
            await _client.SetSwitchAsync(DeviceName, "CCD_COOLER",
                new Dictionary<string, bool> { ["COOLER_ON"] = on, ["COOLER_OFF"] = !on }, ct);
        } catch { /* driver rejected the switch, silent */ }
    }

    /// <summary>INDI astronomy cameras don't expose ISO. No-op.</summary>
    public Task SetIsoAsync(int iso, CancellationToken ct = default) => Task.CompletedTask;

    public async Task<IImageData> CaptureAsync(double exposureSeconds, CaptureOptions? opts = null, CancellationToken ct = default) {
        // Re-assert BLOB delivery before every capture. INDI's
        // enableBLOB rule is per-connection AND per-device; reconnects
        // or indiserver restarts silently drop it, leaving CCD_EXPOSURE
        // writes that complete on the driver side but never deliver a
        // CCD1 BLOB to us -> OnBlobReceived never fires -> the TCS below
        // hangs forever. Cheap (single small XML packet) so doing it on
        // every capture is fine.
        try { await _client.EnableBlobAsync(DeviceName, ct); } catch { /* best effort */ }

        // BIAS / zero-second requests: at exactly 0 s most INDI drivers
        // (incl. indi_svbony_ccd) never start a readout, so no BLOB is
        // ever delivered and the capture hangs until timeout. Clamp up to
        // the driver's advertised minimum exposure (or a tiny fallback)
        // so a bias frame still produces an image. The caller keeps the
        // logical 0 s for the FITS EXPTIME / metadata.
        if (exposureSeconds <= 0) {
            var minExp = MinExposure;
            exposureSeconds = minExp > 0 ? minExp : 0.0001;
        }

        _exposureTcs = new TaskCompletionSource<IImageData>();
        var localTcs = _exposureTcs;

        using var reg = ct.Register(() => localTcs.TrySetCanceled());

        // Force FITS encode + 16-bit RAW capture format first: a still left
        // on RAW8 (e.g. after a video stream) comes back as 8-bit data in a
        // 16-bit FITS — near-black; and a driver left on FORMAT_NATIVE sends a
        // blob our FITS pipeline can't decode. Done before gain because a
        // format switch can reset the driver's controls on some cameras.
        await EnsureFitsTransferAsync(ct);
        await EnsureRaw16FormatAsync(ct);

        // opts overrides honoured per-capture so the sequencer can set
        // binning + gain inline without a separate round-trip.
        if (opts?.BinX is int bx && opts.BinY is int by) {
            await SetBinningAsync(bx, by, ct);
        }
        if (opts?.Gain is int g) {
            await TrySetGainAsync(g, ct);
        }
        if (opts?.Offset is int off) {
            await TrySetOffsetAsync(off, ct);
        }

        await _client.SetNumberAsync(DeviceName, "CCD_EXPOSURE",
            new Dictionary<string, double> { ["CCD_EXPOSURE_VALUE"] = exposureSeconds }, ct);

        // Server-side deadline so a missing BLOB doesn't hang the
        // request forever (which then drags down the next capture
        // too, since they all share _exposureTcs). Budget = exposure
        // + 60 s for download / parse / metadata read; the 60 s
        // cushion is generous enough for a 50 MB ASI2600 / ASI183 BLOB
        // over USB3 + LAN even on a Pi 4, but short enough that a
        // stuck driver bubbles back as a clear "BLOB never arrived"
        // toast rather than a generic client-side "Request timed out".
        var timeoutMs = (int)Math.Min(int.MaxValue,
            (exposureSeconds * 1000) + 60_000);
        using var timeoutCts = new CancellationTokenSource(timeoutMs);
        using var timeoutReg = timeoutCts.Token.Register(() => localTcs.TrySetException(
            new TimeoutException(
                $"INDI camera {DeviceName} did not deliver a BLOB within " +
                $"{Math.Round((exposureSeconds + 60), 1)} s of starting the exposure. " +
                $"Common causes: BLOB delivery disabled on the indiserver, " +
                $"driver crashed mid-exposure, or CCD_EXPOSURE_VALUE never " +
                $"reached the driver.")));

        try {
            return await localTcs.Task;
        } finally {
            // Clear the field if we're still the active TCS so the
            // next CaptureAsync starts fresh; the new one will replace
            // the field on entry regardless, this is belt+suspenders.
            if (ReferenceEquals(_exposureTcs, localTcs)) _exposureTcs = null;
        }
    }

    public async Task AbortExposureAsync(CancellationToken ct = default) {
        await _client.SetSwitchAsync(DeviceName, "CCD_ABORT_EXPOSURE",
            new Dictionary<string, bool> { ["ABORT"] = true }, ct);
        _exposureTcs?.TrySetCanceled();
    }

    /// <summary>Writes CCD_FRAME (X, Y, WIDTH, HEIGHT). Passing w=0 OR
    /// h=0 resets to the full sensor (Max X/Y).</summary>
    public async Task SetSubframeAsync(int x, int y, int width, int height, CancellationToken ct = default) {
        if (width <= 0 || height <= 0) {
            x = 0; y = 0;
            width = MaxX > 0 ? MaxX : 0;
            height = MaxY > 0 ? MaxY : 0;
        }
        await _client.SetNumberAsync(DeviceName, "CCD_FRAME",
            new Dictionary<string, double> {
                ["X"] = x, ["Y"] = y,
                ["WIDTH"] = width, ["HEIGHT"] = height
            }, ct);
    }

    private void OnBlobReceived(IndiBlobProperty blob) {
        if (blob.Device != DeviceName) return;

        foreach (var (name, element) in blob.Values) {
            if (element.Data == null || element.Data.Length == 0) continue;

            try {
                var imageData = FITSReader.Read(element.Data);

                // Some INDI drivers (notably indi_asi_ccd under
                // CCD_VIDEO_STREAM mode) emit BLOBs that aren't a
                // proper FITS file, just a raw uint16 buffer. The
                // reader doesn't throw on those; it returns a
                // BaseImageData with Width=0 / Height=0 / no pixels.
                // Dispatching that downstream means CameraStreamService
                // happily fires "frames" at 5fps, ImageRelayService
                // broadcasts header-only 24-byte WS messages, and the
                // browser canvas stays black even though every counter
                // says video is working. Treat zero-sized parses as
                // failures so the streaming-fallback logic in
                // CameraStreamService kicks in.
                if (imageData.Properties.Width <= 0
                    || imageData.Properties.Height <= 0
                    || imageData.Data == null
                    || imageData.Data.Length == 0) {
                    if (_isStreaming) {
                        Interlocked.Increment(ref _emptyStreamFrameCount);
                    } else {
                        _exposureTcs?.TrySetException(
                            new InvalidDataException(
                                $"INDI BLOB from {DeviceName} parsed as empty " +
                                $"(driver may not emit FITS under CCD_VIDEO_STREAM)"));
                    }
                    continue;
                }

                imageData.MetaData.Camera.Name = DeviceName;
                imageData.MetaData.Camera.Temperature = Temperature;
                imageData.MetaData.Camera.BinX = (short)BinX;
                imageData.MetaData.Camera.BinY = (short)BinY;
                imageData.MetaData.Camera.PixelSizeX = PixelSizeX;
                imageData.MetaData.Camera.PixelSizeY = PixelSizeY;

                // FIELD5-CFA: INDI drivers typically do NOT put BAYERPAT
                // in the FITS BLOB header (the SV405CC indi_svbony_ccd
                // is a confirmed case). The CFA layout is advertised
                // separately via the INDI CCD_CFA text property. When
                // FITSReader parsed BayerPattern=None (no BAYERPAT in
                // the BLOB) but the driver reports a CFA, inject it so
                // the downstream pipeline (ImageRelayService stream
                // header, FITSWriter save-to-disk, live stack) sees the
                // correct pattern. Without this the shader received
                // bayer=0 (mono) and rendered raw Bayer data as-is,
                // producing the infamous checkerboard.
                var driverBayer = BayerPattern;
                if (driverBayer != BayerPatternEnum.None
                        && imageData.Properties.BayerPattern == BayerPatternEnum.None) {
                    imageData = new BaseImageData(
                        imageData.Data,
                        imageData.Properties with {
                            BayerPattern = driverBayer,
                            IsBayered = true
                        },
                        imageData.MetaData);
                }
                // Also propagate into MetaData so FITSWriter emits
                // the BAYERPAT keyword when saving frames to disk.
                if (driverBayer != BayerPatternEnum.None) {
                    imageData.MetaData.Camera.BayerPattern = driverBayer;
                    imageData.MetaData.Camera.SensorType =
                        driverBayer switch {
                            BayerPatternEnum.RGGB => SensorType.RGGB,
                            BayerPatternEnum.BGGR => SensorType.BGGR,
                            BayerPatternEnum.GBRG => SensorType.GBRG,
                            BayerPatternEnum.GRBG => SensorType.GRBG,
                            _ => SensorType.Monochrome
                        };
                }

                // Native streaming path: when CCD_VIDEO_STREAM is ON the
                // driver fires BLOBs continuously at its native cadence
                // (10-30 fps typical). Fan them out to every subscriber
                // and bypass the exposure-completion TCS so a long-pending
                // CaptureAsync isn't accidentally resolved with a stream
                // frame.
                if (_isStreaming) {
                    foreach (var sub in _streamSubscribers.Values) {
                        try { sub(imageData); } catch { /* one subscriber's bug shouldn't kill the loop */ }
                    }
                } else {
                    _exposureTcs?.TrySetResult(imageData);
                }
            } catch (Exception ex) {
                if (!_isStreaming) _exposureTcs?.TrySetException(ex);
                // While streaming, a bad frame is just a dropped frame,
                // don't poison the whole stream.
            }
        }
    }

    private void OnPropertyChanged(string device, IndiProperty prop) {
        if (device != DeviceName) return;
        // Could raise events for UI updates here
    }
}