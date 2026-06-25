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
using SkiaSharp;

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
    // Last non-None CFA pattern observed on CCD_CFA. An OSC sensor's
    // Bayer layout never changes during a session, so once we've seen it
    // we keep it: the INDI property store can transiently lose CCD_CFA
    // (right after a reconnect / driver property re-publish), which would
    // otherwise make a frame come back BayerPattern=None and save a colour
    // light with no BAYERPAT card (reopens as "mono" in the editor).
    private volatile BayerPatternEnum _lastCfa = BayerPatternEnum.None;
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
            BayerPatternEnum live = BayerPatternEnum.None;
            if (cfaProp != null
                    && cfaProp.Values.TryGetValue("CFA_TYPE", out var cfaType)) {
                live = (cfaType?.Trim().ToUpperInvariant()) switch {
                    "RGGB" => BayerPatternEnum.RGGB,
                    "BGGR" => BayerPatternEnum.BGGR,
                    "GBRG" => BayerPatternEnum.GBRG,
                    "GRBG" => BayerPatternEnum.GRBG,
                    _ => BayerPatternEnum.None
                };
            }
            // Cache the last good read and reuse it when the property is
            // transiently missing, so a single CCD_CFA gap can't drop the
            // pattern for a frame. The layout is fixed per session, so a
            // stale value here is always the correct value.
            if (live != BayerPatternEnum.None) {
                _lastCfa = live;
                return live;
            }
            return _lastCfa;
        }
    }

    // INDI cameras don't surface gain in a standardised property, the
    // CCD_CONTROLS group varies by driver (gain / Gain / GAIN). Plumb a
    // best-effort read here and return 0 when nothing matches.
    public int Gain => (int)_client.GetNumber(DeviceName, "CCD_CONTROLS", "Gain");

    // ISO is not part of the INDI CCD spec; dedicated astronomy cameras report
    // analogue gain instead and never publish CCD_ISO, so IsoOptions stays
    // empty and the UI hides the ISO dropdown. DSLRs via indi_gphoto DO publish
    // a CCD_ISO switch vector whose element labels carry the ISO value
    // ("Auto","100","200",...). We surface those as the ISO list.
    public IReadOnlyList<int> IsoOptions {
        get {
            var sw = _client.GetProperty(DeviceName, "CCD_ISO") as IndiSwitchProperty;
            if (sw == null) return Array.Empty<int>();
            var set = new SortedSet<int>();
            foreach (var name in sw.Values.Keys) {
                var lbl = sw.Labels.TryGetValue(name, out var l) ? l : name;
                if (TryParseIso(lbl, out var iso) || TryParseIso(name, out iso))
                    set.Add(iso);
            }
            return set.Count > 0 ? set.ToArray() : Array.Empty<int>();
        }
    }

    public int SelectedIso {
        get {
            var sw = _client.GetProperty(DeviceName, "CCD_ISO") as IndiSwitchProperty;
            if (sw == null) return 0;
            foreach (var (name, on) in sw.Values) {
                if (!on) continue;
                var lbl = sw.Labels.TryGetValue(name, out var l) ? l : name;
                if (TryParseIso(lbl, out var iso) || TryParseIso(name, out iso))
                    return iso;
            }
            return 0;
        }
    }

    /// <summary>Parse an ISO value from a CCD_ISO element label/name. Strips
    /// any non-digits ("ISO 800" -> 800) and rejects 0 / non-numeric entries
    /// like "Auto" so they never appear as a selectable speed.</summary>
    private static bool TryParseIso(string? s, out int iso) {
        iso = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var digits = new string(s.Where(char.IsDigit).ToArray());
        return digits.Length > 0 && int.TryParse(digits, out iso) && iso > 0;
    }

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
            // DSLRs (indi_gphoto) publish CCD_ISO; astronomy cameras don't.
            var supportsIso = _client.GetProperty(DeviceName, "CCD_ISO") is IndiSwitchProperty;
            return CameraCapabilities.Astro with {
                SupportsVideoStream = supportsStream,
                SupportsWhiteBalance = supportsWb,
                SupportsIso = supportsIso
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

    /// <summary>Tell the driver what kind of frame this is via CCD_FRAME_TYPE
    /// (FRAME_LIGHT / FRAME_BIAS / FRAME_DARK / FRAME_FLAT) so the BLOB the
    /// driver returns is tagged correctly (some drivers stamp the FITS FRAME
    /// keyword from it, and a few engage shutter/closed-loop behaviour). The
    /// AUTORUN/sequencer already routes folders + the IMAGETYP header from the
    /// requested type; this also reflects it on the device. Best-effort: many
    /// drivers don't publish CCD_FRAME_TYPE (or use it), so a miss is a no-op.</summary>
    private async Task TrySetFrameTypeAsync(string? imageType, CancellationToken ct) {
        if (string.IsNullOrWhiteSpace(imageType)) return;
        var member = imageType.Trim().ToUpperInvariant() switch {
            "BIAS" => "FRAME_BIAS",
            "DARK" => "FRAME_DARK",
            "DARKFLAT" => "FRAME_DARK",   // no standard FRAME_DARKFLAT; closest is dark
            "FLAT" => "FRAME_FLAT",
            "LIGHT" => "FRAME_LIGHT",
            _ => "FRAME_LIGHT"
        };
        if (_client.GetProperty(DeviceName, "CCD_FRAME_TYPE") is not IndiSwitchProperty sw
            || !sw.Values.ContainsKey(member)) return;   // driver doesn't expose it / no such member
        // One-of switch: set the chosen member true, the rest false.
        var payload = sw.Values.Keys.ToDictionary(k => k, k => k == member);
        try {
            await _client.SetSwitchAsync(DeviceName, "CCD_FRAME_TYPE", payload, ct);
        } catch { /* driver rejected — non-fatal */ }
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
    private bool _raw16Forced;       // have we written RAW16 at least once this session?

    /// <summary>True when this is a DSLR driven by indi_gphoto. CCD_CAPTURE_TARGET
    /// (RAM / SD Card) is a gphoto-only property, so its presence is a reliable
    /// tell. gphoto's bundled FITS converter wedges on newer bodies (Canon SL2
    /// etc.) — capture fires but no BLOB is ever delivered — so for these we
    /// request the camera-native RAW and decode the embedded JPEG ourselves.</summary>
    private bool IsGphotoNative => _client.GetProperty(DeviceName, "CCD_CAPTURE_TARGET") != null;

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

    // Force the transfer/encode format (CCD_TRANSFER_FORMAT, "Encode" in the
    // INDI panel, elements FORMAT_FITS / FORMAT_NATIVE / FORMAT_XISF).
    // Astro cameras → FITS (our pipeline decodes it directly). gphoto DSLRs →
    // NATIVE, because the driver's FITS converter is unreliable on newer bodies;
    // OnBlobReceived then decodes the embedded JPEG from the native RAW. Cheap
    // re-probe + "already correct?" guard, same pattern as the RAW16 selector.
    private async Task EnsureFitsTransferAsync(CancellationToken ct) {
        if (_client.GetProperty(DeviceName, "CCD_TRANSFER_FORMAT") is not IndiSwitchProperty cur
                || cur.Values.Count == 0)
            return;   // no transfer-format property on this driver
        var want = IsGphotoNative ? "NATIVE" : "FITS";
        string? target = null;
        foreach (var k in cur.Values.Keys) {
            if (k.ToUpperInvariant().Contains(want)) { target = k; break; }
        }
        if (target == null) return;   // requested format not offered by this driver
        if (cur.Values.TryGetValue(target, out var on) && on) return;   // already set
        var payload = new Dictionary<string, bool>();
        foreach (var k in cur.Values.Keys) payload[k] = (k == target);
        try {
            await _client.SetSwitchAsync(DeviceName, "CCD_TRANSFER_FORMAT", payload, ct);
        } catch { /* driver rejected; non-fatal */ }
    }

    /// <summary>indi_gphoto (DSLR) capture-target + upload-mode safety. When the
    /// camera is set to save to the SD card (CCD_CAPTURE_TARGET = SD_CARD), the
    /// exposure fires but the frame lands on the card and NO BLOB is delivered to
    /// us — PREVIEW stays blank and the capture eventually times out. Force the
    /// capture target to internal RAM and the upload mode to client so the frame
    /// streams back to Polaris. Best-effort + idempotent: dedicated astro cameras
    /// don't expose these properties and just no-op, and we only write when the
    /// value isn't already correct (so we don't re-poke the driver every frame).</summary>
    private async Task EnsureClientUploadAsync(CancellationToken ct) {
        // Capture target → RAM (CCD_CAPTURE_TARGET, elements RAM / SD_CARD).
        if (_client.GetProperty(DeviceName, "CCD_CAPTURE_TARGET") is IndiSwitchProperty tgt
                && tgt.Values.Count > 0) {
            string? ram = null;
            foreach (var k in tgt.Values.Keys)
                if (k.ToUpperInvariant().Contains("RAM")) { ram = k; break; }
            if (ram != null && !(tgt.Values.TryGetValue(ram, out var onRam) && onRam)) {
                var payload = new Dictionary<string, bool>();
                foreach (var k in tgt.Values.Keys) payload[k] = (k == ram);
                try { await _client.SetSwitchAsync(DeviceName, "CCD_CAPTURE_TARGET", payload, ct); }
                catch { /* driver rejected; non-fatal */ }
            }
        }
        // Upload mode → client (UPLOAD_MODE, elements UPLOAD_CLIENT/_LOCAL/_BOTH).
        if (_client.GetProperty(DeviceName, "UPLOAD_MODE") is IndiSwitchProperty up
                && up.Values.Count > 0) {
            string? client = null;
            foreach (var k in up.Values.Keys)
                if (k.ToUpperInvariant().Contains("CLIENT")) { client = k; break; }
            if (client != null && !(up.Values.TryGetValue(client, out var onCli) && onCli)) {
                var payload = new Dictionary<string, bool>();
                foreach (var k in up.Values.Keys) payload[k] = (k == client);
                try { await _client.SetSwitchAsync(DeviceName, "UPLOAD_MODE", payload, ct); }
                catch { /* driver rejected; non-fatal */ }
            }
        }
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

        // Same as a still capture: force FITS encode + RAW16 before the
        // stream starts so video frames aren't silently 8-bit (the SVBONY
        // SV405CC otherwise streams RAW8 unless RAW16 is actively written).
        await EnsureFitsTransferAsync(ct);
        await EnsureRaw16FormatAsync(ct);

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
        // DSLR (indi_gphoto): force capture target → RAM and upload → client
        // as soon as the driver's property vectors arrive, so it's correct
        // regardless of which capture path runs first AND visible in the INDI
        // panel right after connect (the field report: "Upload stays
        // UPLOAD_LOCAL"). INDI streams defSwitchVector asynchronously after the
        // connect ack, so poll briefly for the property before writing it.
        for (int i = 0; i < 20; i++) {
            if (_client.GetProperty(DeviceName, "UPLOAD_MODE") != null
                || _client.GetProperty(DeviceName, "CCD_CAPTURE_TARGET") != null) break;
            try { await Task.Delay(100, ct); } catch { break; }
        }
        try { await EnsureClientUploadAsync(ct); } catch { /* best effort */ }
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

    /// <summary>Select an ISO on a DSLR (indi_gphoto CCD_ISO switch). No-op
    /// for astronomy cameras that don't publish CCD_ISO.</summary>
    public async Task SetIsoAsync(int iso, CancellationToken ct = default) {
        var sw = _client.GetProperty(DeviceName, "CCD_ISO") as IndiSwitchProperty;
        if (sw == null) return;
        // Find the element whose label/name parses to the requested ISO, then
        // drive a OneOfMany switch: that element On, all others Off.
        string? target = null;
        foreach (var name in sw.Values.Keys) {
            var lbl = sw.Labels.TryGetValue(name, out var l) ? l : name;
            if ((TryParseIso(lbl, out var v) || TryParseIso(name, out v)) && v == iso) {
                target = name;
                break;
            }
        }
        if (target == null) return;   // requested ISO not offered by the driver
        var states = sw.Values.Keys.ToDictionary(k => k, k => k == target);
        try { await _client.SetSwitchAsync(DeviceName, "CCD_ISO", states, ct); }
        catch { /* driver rejected, silent */ }
    }

    public async Task<IImageData> CaptureAsync(double exposureSeconds, CaptureOptions? opts = null, CancellationToken ct = default) {
        // Re-assert BLOB delivery before every capture. INDI's
        // enableBLOB rule is per-connection AND per-device; reconnects
        // or indiserver restarts silently drop it, leaving CCD_EXPOSURE
        // writes that complete on the driver side but never deliver a
        // CCD1 BLOB to us -> OnBlobReceived never fires -> the TCS below
        // hangs forever. Cheap (single small XML packet) so doing it on
        // every capture is fine.
        try { await _client.EnableBlobAsync(DeviceName, ct); } catch { /* best effort */ }

        // DSLR (indi_gphoto): make sure the frame comes back to us instead of
        // being saved to the SD card. Without this a "Capture Target = SD Card"
        // setting means the shutter fires but no BLOB ever arrives (blank
        // PREVIEW + timeout). No-op on cameras without these properties.
        try { await EnsureClientUploadAsync(ct); } catch { /* best effort */ }

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
        // Reflect the requested frame kind (Light/Bias/Dark/Flat) on the driver
        // so the returned BLOB is tagged correctly. Defaults to LIGHT when the
        // caller didn't specify (e.g. PREVIEW snaps).
        await TrySetFrameTypeAsync(opts?.ImageType ?? "LIGHT", ct);

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

    /// <summary>Force the driver's CCD_INFO pixel size, in micrometres.
    /// Meant for backends that don't report it — notably <c>indi_gphoto</c>
    /// (DSLRs), which leaves CCD_PIXEL_SIZE at 0. Writes the square pixel
    /// element (CCD_PIXEL_SIZE) plus the X/Y pair so the getters above return
    /// the supplied value. Best-effort: drivers that lock CCD_INFO just
    /// ignore the write. Pass um &lt;= 0 to no-op.</summary>
    public async Task TrySetPixelSizeAsync(double um, CancellationToken ct = default) {
        if (um <= 0) return;
        await _client.SetNumberAsync(DeviceName, "CCD_INFO",
            new Dictionary<string, double> {
                ["CCD_PIXEL_SIZE"] = um,
                ["CCD_PIXEL_SIZE_X"] = um,
                ["CCD_PIXEL_SIZE_Y"] = um
            }, ct);
    }

    /// <summary>Writes CCD_FRAME (X, Y, WIDTH, HEIGHT). Passing w=0 OR
    /// h=0 resets to the full sensor (Max X/Y).</summary>
    public async Task SetSubframeAsync(int x, int y, int width, int height, CancellationToken ct = default) {
        if (width <= 0 || height <= 0) {
            x = 0; y = 0;
            width = MaxX > 0 ? MaxX : 0;
            height = MaxY > 0 ? MaxY : 0;
        }
        // If we still don't know the real geometry (e.g. CCD_INFO hasn't
        // arrived yet right after connect), don't write a zero/garbage
        // CCD_FRAME — that would corrupt the ROI instead of resetting it.
        if (width <= 0 || height <= 0) return;
        // Idempotent guard. Writing CCD_FRAME re-allocates the ROI / capture
        // buffer inside many INDI drivers (notably indi_asi_ccd). The native
        // guide + calibration loop resets to full frame before EVERY capture,
        // so without this guard we poked CCD_FRAME on every single guide frame.
        // On a USB2 cam (ASI120MM Mini) that repeated re-init is a way to wedge
        // the driver into dropping a BLOB, especially when it overlaps a mount
        // guide pulse on the shared indiserver connection — which is exactly
        // the deterministic "calibration stalls at RA reversal" failure. PHD2
        // sets the frame once and then just loops exposures; match that by
        // skipping the write when CCD_FRAME already holds the requested
        // geometry. Only applied once we know real dimensions (width/height>0)
        // so the first, genuine configuration still goes through.
        if (width > 0 && height > 0
            && (int)_client.GetNumber(DeviceName, "CCD_FRAME", "X") == x
            && (int)_client.GetNumber(DeviceName, "CCD_FRAME", "Y") == y
            && (int)_client.GetNumber(DeviceName, "CCD_FRAME", "WIDTH") == width
            && (int)_client.GetNumber(DeviceName, "CCD_FRAME", "HEIGHT") == height) {
            return;
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
                // gphoto (FORMAT_NATIVE) delivers a camera-native RAW (.cr2/.nef/
                // .arw) or a JPEG, not FITS — the BLOB's format attribute tells us
                // which. Decode the embedded full-res JPEG for the preview/stats/
                // stack; the untouched RAW rides on IHasRawFile so save-to-disk
                // writes the real .cr2. Anything FITS (or no format hint) stays on
                // the FITS reader.
                var blobFmt = (element.Format ?? "").Trim().ToLowerInvariant();
                bool treatAsRaw = blobFmt.Length > 0 && !blobFmt.Contains("fit");
                IImageData imageData;
                if (treatAsRaw) {
                    var decoded = DecodeRawDslrBlob(element.Data, blobFmt);
                    if (decoded == null) {
                        if (_isStreaming) Interlocked.Increment(ref _emptyStreamFrameCount);
                        else _exposureTcs?.TrySetException(new InvalidDataException(
                            $"INDI BLOB from {DeviceName} (format '{blobFmt}') had no " +
                            $"decodable embedded JPEG"));
                        continue;
                    }
                    imageData = decoded;
                } else {
                    imageData = FITSReader.Read(element.Data);
                }

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
                // the BAYERPAT keyword when saving frames to disk. Use the
                // EFFECTIVE pattern: prefer the driver-advertised CFA, but
                // fall back to whatever the BLOB header itself carried — a
                // driver that DOES embed BAYERPAT in the FITS but has a
                // momentarily-empty CCD_CFA would otherwise save with the
                // pattern in Properties yet a None in MetaData, and the
                // writer reads MetaData (reopens as "mono").
                var effectiveBayer = driverBayer != BayerPatternEnum.None
                    ? driverBayer
                    : imageData.Properties.BayerPattern;
                if (effectiveBayer != BayerPatternEnum.None) {
                    imageData.MetaData.Camera.BayerPattern = effectiveBayer;
                    imageData.MetaData.Camera.SensorType =
                        effectiveBayer switch {
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

    // ---- DSLR native-RAW decode (indi_gphoto FORMAT_NATIVE) ----

    /// <summary>Build an IImageData from a camera-native DSLR BLOB (CR2/NEF/ARW
    /// or JPEG). We don't decode the Bayer RAW itself — we pull out the
    /// embedded full-res JPEG (every CR2/NEF carries one) and use its luminance
    /// plane for the on-screen preview, stats and stacking, exactly like the
    /// vendor-SDK DSLR path. The original RAW bytes are attached via IHasRawFile
    /// so the save-to-disk path writes the real .cr2. Returns null if no JPEG is
    /// embedded / decodable.</summary>
    private IImageData? DecodeRawDslrBlob(byte[] data, string ext) {
        // The whole BLOB might already be a JPEG (gphoto delivering .jpg), else
        // it's a TIFF-based RAW (CR2/NEF/ARW) with one or more embedded JPEGs.
        // Try the candidates largest-first and take the first that actually
        // decodes — robust across Canon/Nikon/Sony (and skips any false SOI/EOI
        // hit inside the compressed raw data, which simply won't decode).
        SKBitmap? bmp = null;
        if (LooksLikeJpeg(data)) {
            try { bmp = SKBitmap.Decode(data); } catch { bmp = null; }
        }
        if (bmp == null || bmp.Width <= 0 || bmp.Height <= 0) {
            bmp?.Dispose(); bmp = null;
            foreach (var cand in EnumerateJpegCandidates(data)) {
                try { bmp = SKBitmap.Decode(cand); } catch { bmp = null; }
                if (bmp != null && bmp.Width > 0 && bmp.Height > 0) break;
                bmp?.Dispose(); bmp = null;
            }
        }
        if (bmp == null || bmp.Width <= 0 || bmp.Height <= 0) { bmp?.Dispose(); return null; }
        int w = bmp.Width, h = bmp.Height;
        var pixels = new ushort[w * h];
        try {
            var cols = bmp.Pixels;   // SKColor[], color-type agnostic
            for (int i = 0; i < pixels.Length; i++) {
                var c = cols[i];
                // Rec.601 luma, 8-bit → 16-bit so the stretch/histogram pipeline
                // behaves the same as a FITS frame.
                double luma = 0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue;
                pixels[i] = (ushort)Math.Clamp(luma * 256, 0, 65535);
            }
        } finally { bmp.Dispose(); }

        var props = new ImageProperties {
            Width = w, Height = h, BitDepth = 16,
            IsBayered = false, BayerPattern = BayerPatternEnum.None
        };
        var meta = new ImageMetaData { CreationTime = DateTime.UtcNow };
        return new BaseImageData(pixels, props, meta) {
            RawFileBytes = data,
            RawFileExtension = ext.StartsWith('.') ? ext : "." + ext
        };
    }

    private static bool LooksLikeJpeg(byte[] d) =>
        d != null && d.Length > 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF;

    /// <summary>Enumerate the complete JPEGs embedded in a container (CR2/NEF/ARW
    /// are TIFF-based and carry one or more), largest first. JPEG byte-stuffing
    /// guarantees an unescaped 0xFFD9 only ends a real JPEG, but a TIFF wrapper /
    /// compressed raw plane can still produce a coincidental SOI..EOI span — so
    /// the caller decodes candidates in turn and keeps the first that's valid.</summary>
    private static IEnumerable<byte[]> EnumerateJpegCandidates(byte[] data) {
        var found = new List<(int off, int len)>();
        int i = 0;
        while (i + 3 < data.Length) {
            if (data[i] == 0xFF && data[i + 1] == 0xD8 && data[i + 2] == 0xFF) {
                int j = i + 2;
                while (j + 1 < data.Length && !(data[j] == 0xFF && data[j + 1] == 0xD9)) j++;
                if (j + 1 < data.Length) {
                    found.Add((i, j + 2 - i));
                    i = j + 2;
                    continue;
                }
                break;   // SOI with no EOI — truncated, stop
            }
            i++;
        }
        foreach (var (off, len) in found.OrderByDescending(f => f.len)) {
            var jpeg = new byte[len];
            Array.Copy(data, off, jpeg, 0, len);
            yield return jpeg;
        }
    }

    private string? _lastLocalFilePath;   // dedupe CCD_FILE_PATH updates

    private void OnPropertyChanged(string device, IndiProperty prop) {
        if (device != DeviceName) return;

        // UPLOAD_LOCAL fallback. Some indi_gphoto builds refuse UPLOAD_CLIENT
        // (the switch reverts to UPLOAD_LOCAL even when set manually), so the
        // captured frame is written to the server's filesystem instead of being
        // delivered as a BLOB — OnBlobReceived never fires and the capture times
        // out. When the driver saves locally it reports the absolute path in the
        // CCD_FILE_PATH text vector; read that file ourselves and complete the
        // pending exposure. Harmless when UPLOAD_CLIENT works (no FILE_PATH is
        // sent, and TrySetResult is idempotent if both arrive).
        if (prop.Name == "CCD_FILE_PATH" && prop is IndiTextProperty t && !_isStreaming) {
            string? path = null;
            if (t.Values.TryGetValue("FILE_PATH", out var p) && !string.IsNullOrWhiteSpace(p)) path = p;
            else foreach (var v in t.Values.Values) { if (!string.IsNullOrWhiteSpace(v)) { path = v; break; } }
            if (string.IsNullOrWhiteSpace(path) || path == _lastLocalFilePath) return;
            var tcs = _exposureTcs;
            if (tcs == null || tcs.Task.IsCompleted) return;
            _lastLocalFilePath = path;
            // Read + decode off the INDI read thread so a big CR2 + JPEG decode
            // doesn't stall property parsing for the rest of the session.
            _ = Task.Run(() => CompleteFromLocalFile(path!, tcs));
        }
    }

    /// <summary>Read a driver-saved capture from disk (UPLOAD_LOCAL path reported
    /// via CCD_FILE_PATH) and resolve the pending exposure with it. The local
    /// copy is a transfer artefact — Polaris keeps the bytes (IHasRawFile) and
    /// writes its own copy when the user asked to save — so we delete it after
    /// reading to keep the driver's upload dir from filling up.</summary>
    private void CompleteFromLocalFile(string path, TaskCompletionSource<IImageData> tcs) {
        try {
            byte[]? bytes = null;
            for (int attempt = 0; attempt < 5 && !tcs.Task.IsCompleted; attempt++) {
                try { bytes = File.ReadAllBytes(path); break; }
                catch (IOException) { Thread.Sleep(150); }   // still being written / locked
            }
            if (bytes == null || bytes.Length == 0) {
                tcs.TrySetException(new InvalidDataException(
                    $"INDI {DeviceName} reported a local file but it couldn't be read: {path}"));
                return;
            }
            var ext = Path.GetExtension(path).ToLowerInvariant();
            IImageData? img = (ext is ".fits" or ".fit" or ".fts")
                ? FITSReader.Read(bytes)
                : DecodeRawDslrBlob(bytes, string.IsNullOrEmpty(ext) ? ".cr2" : ext);
            if (img == null || img.Properties.Width <= 0 || img.Properties.Height <= 0) {
                tcs.TrySetException(new InvalidDataException(
                    $"INDI {DeviceName} local file '{path}' had no decodable image"));
                return;
            }
            img.MetaData.Camera.Name = DeviceName;
            img.MetaData.Camera.BinX = (short)BinX;
            img.MetaData.Camera.BinY = (short)BinY;
            img.MetaData.Camera.PixelSizeX = PixelSizeX;
            img.MetaData.Camera.PixelSizeY = PixelSizeY;
            tcs.TrySetResult(img);
            try { File.Delete(path); } catch { /* best effort cleanup */ }
        } catch (Exception ex) {
            tcs.TrySetException(ex);
        }
    }
}