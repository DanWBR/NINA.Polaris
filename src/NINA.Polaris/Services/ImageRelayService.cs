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
using System.Net.WebSockets;
using NINA.Core.Enum;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Polaris.Services.Studio;

namespace NINA.Polaris.Services;

/// <summary>
/// Broadcasts captured frames to every connected
/// <c>/ws/image-stream</c> client. Each client is fanned out
/// independently, so a slow browser doesn't stall the rest.
///
/// FIELD-3: streaming is RAW-only (uint16 pixels + LZ4-compressed
/// header carrying the Bayer pattern, ~5-15 MB per frame, decoded
/// client-side by the WASM pipeline + WebGL2 stretch / debayer).
/// The old JPEG WS path was deleted because it baked AutoStretch
/// into the JPEG server-side, which silently disabled the operator's
/// Stretch / WB controls in the browser. Adaptive bandwidth went
/// with it -- the only downgrade target was JPEG. Slow consumers are
/// handled by per-client SendLock back-pressure (frame skip) instead
/// of format switch.
///
/// <see cref="GetLatestJpeg"/> stays for the static one-shot
/// thumbnail endpoints (gallery preview, livestack preview) -- those
/// want a pre-stretched JPEG and are decoupled from the live WS path.
///
/// Holds the most recent <see cref="ImageBuffer"/> so a freshly-
/// connected client can immediately render the last frame without
/// waiting for the next capture.
/// </summary>
public class ImageRelayService : IDisposable {
    private readonly ConcurrentDictionary<string, ClientEntry> _clients = new();
    private readonly ILogger<ImageRelayService> _logger;
    private readonly ProfileService? _profiles;
    private ImageBuffer? _latestImage;
    private IImageData? _latestImageData;
    private byte[]? _latestJpeg;

    // The live STACK's own picture, kept apart from _latestImage. Those hold
    // the last frame of ANY kind that went past, which is the wrong answer for
    // /api/livestack/preview: after a target change the client pulled the
    // preview and got the previous target's stack painted over the new one
    // (field report 2026-08-08, "the first restack showed and then the old
    // image came back"). Cleared by ClearStack() when the stacker resets, so
    // between a reset and the first new frame the endpoint honestly 404s.
    private IImageData? _stackImage;
    private byte[]? _stackJpeg;
    private readonly object _stackGate = new();

    // Stabilize a transient CCD_CFA dropout: some drivers momentarily report
    // BayerPattern=None mid-session, which (without an operator override) would
    // relay that single frame as mono and flash a grey / raw-mosaic frame on
    // the client-side debayer — the intermittent "frame não debayerado" report.
    // Mirrors LiveStackingService._lastGoodBayer, but capped so a genuinely
    // mono camera (continuous None, e.g. after switching to a mono rig) is not
    // forced into colour: after a short run of None frames we stop substituting
    // and let true mono through. Holds the already override/flip-corrected
    // pattern so it can be reused verbatim.
    private BayerPatternEnum _lastRelayBayer = BayerPatternEnum.None;
    private int _bayerDropoutRun;

    /// <summary>The most recently relayed frame, as a decoded
    /// ushort[] pixel buffer with width/height. Null until the first
    /// capture lands. Consumed by post-processing endpoints (e.g.
    /// /api/focus/bahtinov) that want to analyse the current scene
    /// without forcing a duplicate capture. Lifetime: replaced on
    /// every RelayImageAsync call.</summary>
    public ImageBuffer? LatestImage => _latestImage;

    /// <summary>FIELD4-4: the most recently relayed frame as an
    /// IImageData, so callers (notably the PREVIEW plate-solve
    /// endpoint) can hand it to FITSWriter without round-tripping
    /// through ImageBuffer (which drops metadata). Mirrors the
    /// LatestImage lifetime, replaced on every RelayImageAsync.</summary>
    public IImageData? LatestImageData => _latestImageData;

    // A full-frame raw relay is BIG (a 4144x2822 16-bit sub is ~22 MB raw,
    // ~19 MB after LZ4 — astro data barely compresses). On an SBC uplink that
    // is also carrying the storage push (another ~23 MB per capture), 10 s was
    // not enough to drain one frame: the send timed out, the client was
    // dropped, and the browser kept showing the last frame that made it
    // through — the preview looked frozen on a stale image while LIVE/STUDIO
    // (small server-side JPEGs) stayed fine. Give a frame a realistic window;
    // the per-client "skip if still sending" backpressure already stops a slow
    // consumer from building a backlog, so a long timeout costs nothing.
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(60);
    private const int MaxConsecutiveFailures = 3;

    public ImageRelayService(ILogger<ImageRelayService> logger) {
        _logger = logger;
    }

    /// <summary>FIELD-2: DI overload that wires the active rig's
    /// <c>BayerPatternOverride</c> into every relayed frame. Kept as a
    /// second constructor so existing test code that builds the service
    /// without a profile still compiles and runs. In Program.cs the
    /// container picks this overload because the greediest matching
    /// constructor wins.</summary>
    public ImageRelayService(ILogger<ImageRelayService> logger,
                              ProfileService profiles) {
        _logger = logger;
        _profiles = profiles;
    }

    /// <summary>FIELD3-2 / FIELD4-3: build a new IImageData with
    /// rows reversed when the active camera's VerticalFlipImage
    /// quirk is true. Returns the original IImageData unchanged
    /// when off / no profile resolved. The flip is a simple
    /// row-by-row reversal of the ushort[] buffer;
    /// ImageProperties + MetaData are shared by reference because
    /// nothing downstream mutates them. Cheap on a modern CPU
    /// (4096x4096 @ 16 bit ~ 32 MB row swap, ~5 ms).</summary>
    private IImageData ApplyVerticalFlipIfEnabled(IImageData source) {
        if (!ActiveVerticalFlip()) return source;
        var w = source.Properties.Width;
        var h = source.Properties.Height;
        if (w <= 0 || h <= 0 || source.Data == null || source.Data.Length != w * h) return source;
        var flipped = new ushort[source.Data.Length];
        for (int y = 0; y < h; y++) {
            var srcRow = y * w;
            var dstRow = (h - 1 - y) * w;
            Array.Copy(source.Data, srcRow, flipped, dstRow, w);
        }
        return new NINA.Image.ImageData.BaseImageData(flipped, source.Properties, source.MetaData);
    }

    /// <summary>FIELD4-3: read the active camera's flip toggle from
    /// the per-camera quirks map, falling back to the legacy per-rig
    /// field while the migration window is still open. Once the
    /// per-rig field is removed (one release out), this collapses
    /// back to just the quirks lookup.</summary>
    private bool ActiveVerticalFlip() {
        if (_profiles == null) return false;
        var q = _profiles.GetActiveCameraQuirks();
        if (q.VerticalFlipImage) return true;
        // Legacy per-rig fallback (kept for one release; the
        // ProfileService Load migrator already copies these into
        // CameraQuirks on the first boot, so this branch is dead
        // code on any second-boot install).
        return _profiles.ActiveEquipmentProfile?.VerticalFlipImage ?? false;
    }

    /// <summary>FIELD4-3: read the active camera's Bayer override
    /// string from the per-camera quirks map, falling back to the
    /// legacy per-rig field. Same migration story as
    /// <see cref="ActiveVerticalFlip"/>.</summary>
    private string? ActiveBayerOverride() {
        if (_profiles == null) return null;
        var q = _profiles.GetActiveCameraQuirks();
        if (!string.IsNullOrWhiteSpace(q.BayerPatternOverride)) return q.BayerPatternOverride;
        return _profiles.ActiveEquipmentProfile?.BayerPatternOverride;
    }

    /// <summary>FIELD4-2: when the buffer's been row-flipped, the
    /// Bayer 2x2 cell pairing also shifts by one row -- the original
    /// row-1 (e.g. G/B in RGGB) is now row-0 of every cell. Remap
    /// the pattern so the wire-side enum stays aligned with the
    /// flipped pixels. Identity for unknown / None / Auto.</summary>
    public static BayerPatternEnum RowShiftBayer(BayerPatternEnum source) {
        return source switch {
            BayerPatternEnum.RGGB => BayerPatternEnum.GBRG,
            BayerPatternEnum.GBRG => BayerPatternEnum.RGGB,
            BayerPatternEnum.BGGR => BayerPatternEnum.GRBG,
            BayerPatternEnum.GRBG => BayerPatternEnum.BGGR,
            _ => source
        };
    }

    /// <summary>Resolve the active camera's Bayer override (if
    /// any) to a concrete enum value, composing it with the
    /// vertical-flip row-shift (FIELD4-2) so the wire-side pattern
    /// matches the actual pixel layout the client receives.
    ///
    /// Returns null when:
    ///   - No ProfileService injected (legacy ctor path / tests)
    ///   - No active rig / no camera quirks
    ///   - Override is null / empty / "Auto" AND vertical flip is off
    ///   - String doesn't parse to a known pattern (graceful fall
    ///     through to the source-reported value)
    /// </summary>
    private BayerPatternEnum? ResolveBayerOverride(BayerPatternEnum? sourcePattern) {
        var raw = ActiveBayerOverride();
        BayerPatternEnum? parsed = null;
        if (!string.IsNullOrWhiteSpace(raw)
                && !string.Equals(raw, "Auto", StringComparison.OrdinalIgnoreCase)
                && Enum.TryParse<BayerPatternEnum>(raw, ignoreCase: true, out var p)
                && p != BayerPatternEnum.None
                && p != BayerPatternEnum.Auto) {
            parsed = p;
        }

        // FIELD4-2: if the buffer is being row-flipped, shift the
        // Bayer enum to match. The shift applies whether the
        // operator picked an explicit override OR we're trusting
        // the source pattern. Without this the WebGL2 debayer
        // shader applies (e.g.) RGGB-formula math to GBRG-aligned
        // pixels and the output paints the classic red/green
        // checkerboard the SV405CC operator reported.
        if (ActiveVerticalFlip()) {
            var basis = parsed ?? sourcePattern;
            if (basis.HasValue) return RowShiftBayer(basis.Value);
        }

        return parsed;
    }

    public void RegisterClient(string id, System.Net.WebSockets.WebSocket ws) {
        _clients[id] = new ClientEntry(ws);
        _logger.LogInformation("Image stream client registered: {Id} (total: {Count})", id, _clients.Count);
    }

    public void UnregisterClient(string id) {
        if (_clients.TryRemove(id, out var entry)) {
            // Do NOT dispose entry.SendLock here. A capture's relay
            // (BroadcastFrameAsync) can be mid-fan-out on another thread
            // and still touch this client's semaphore; disposing it raced
            // that and threw ObjectDisposedException ("System.Threading.
            // SemaphoreSlim"), which propagated out and failed the whole
            // /api/camera/capture with HTTP 500. SemaphoreSlim only needs
            // disposal when AvailableWaitHandle was used (we never do), so
            // letting the GC reclaim it is correct and race-free.
            _logger.LogInformation("Image stream client removed: {Id} (remaining: {Count})", id, _clients.Count);
        }
    }

    /// <summary>
    /// Broadcast a frame to every connected /ws/image-stream client.
    /// The default kind is <see cref="FrameKind.Live"/> — backwards
    /// compatible with every caller that doesn't say otherwise.
    /// </summary>
    public Task RelayImageAsync(IImageData imageData, CancellationToken ct = default)
        => RelayImageAsync(imageData, FrameKind.Live, ct);

    /// <summary>
    /// Legacy bool overload kept so we don't have to touch every
    /// caller in one pass. <paramref name="stackable"/>=false maps to
    /// <see cref="FrameKind.Preview"/>, the closest equivalent of the
    /// old "this is a one-off snap" intent.
    /// </summary>
    public Task RelayImageAsync(IImageData imageData, bool stackable, CancellationToken ct = default)
        => RelayImageAsync(imageData, stackable ? FrameKind.Live : FrameKind.Preview, ct);

    public Task RelayImageAsync(IImageData imageData, FrameKind kind, CancellationToken ct = default) {
        var frameKind = (int)kind;
        // FIELD3-2: optional vertical flip. Some camera drivers
        // (SV405CC indi_svbony_ccd notably) deliver TOP-DOWN buffers
        // without the corresponding FITS-spec axis flip; our reader
        // loads sequentially, so the downstream Bayer pattern is
        // offset by one row -> red/green checkerboard after debayer.
        // The per-rig VerticalFlipImage profile toggle (FIELD3-2)
        // mirrors the buffer Y-direction here so RGGB stays RGGB
        // through the rest of the pipeline (live preview AND server-
        // side accumulator, since LiveStackingService's integrated
        // output re-enters this method).
        var sourceData = ApplyVerticalFlipIfEnabled(imageData);
        // FIELD-2 + FIELD4-2: compose the operator's Bayer override
        // (if any) with the automatic row-shift remap that kicks in
        // when VerticalFlipImage is on. Drivers that emit a wrong /
        // missing BAYERPAT (SVBONY indi_svbony_ccd notably) would
        // otherwise feed mono into the client-side debayer; drivers
        // that deliver row-flipped buffers (also SVBONY's SV405CC)
        // would paint a red/green checkerboard because the WebGL2
        // shader cell pairing shifts under the flip. The composed
        // resolver hands a single final enum to the wire side so
        // the shader stays untouched.
        var sourcePattern = sourceData.Properties.BayerPattern;
        var resolved = ResolveBayerOverride(sourcePattern);
        // Reuse the last good Bayer pattern across a transient CCD_CFA dropout
        // so one frame whose pattern came back None/Auto doesn't flash mono on
        // the client. effectiveBayer is what the wire would otherwise carry
        // (override/flip-corrected when set, else the source pattern).
        var effectiveBayer = resolved ?? sourcePattern;
        if (effectiveBayer != BayerPatternEnum.None && effectiveBayer != BayerPatternEnum.Auto) {
            _lastRelayBayer = effectiveBayer;
            _bayerDropoutRun = 0;
        } else if (_lastRelayBayer != BayerPatternEnum.None) {
            // Source reported Bayer=None but we've already locked a real pattern
            // this session, so this is a CCD_CFA dropout (some INDI OSC drivers
            // only publish the pattern intermittently, not every frame) — keep
            // colouring. Reuse is UNBOUNDED on purpose: the previous 5-frame cap
            // meant a driver that dropped the pattern for >5 subs flashed the
            // LIVE view mono a few seconds after each stack (field report). A
            // genuine OSC→mono change only happens on a camera reconnect, which
            // rebuilds this session and resets _lastRelayBayer to None.
            _bayerDropoutRun++;
            resolved = _lastRelayBayer;
            if (_bayerDropoutRun == 1 || _bayerDropoutRun % 50 == 0)
                _logger.LogDebug(
                    "Relay: source reported Bayer=None; reusing last good {Pattern} (dropout run {N})",
                    _lastRelayBayer, _bayerDropoutRun);
        }
        var buffer = ImageBuffer.FromImageData(sourceData, resolved);
        _latestImage = buffer;
        _latestImageData = sourceData;
        _latestJpeg = null;
        if (frameKind == (int)FrameKind.LiveStack) {
            lock (_stackGate) { _stackImage = sourceData; _stackJpeg = null; }
        }

        if (_clients.IsEmpty) return Task.CompletedTask;

        // FIELD-3: streaming is RAW-only. The JPEG WS path was
        // deleted (it baked AutoStretch into the JPEG server-side,
        // which silently neutered the operator's Stretch / WB
        // sliders -- the user reported this from the field). The
        // one-shot JPEG endpoints (/api/image/latest/preview,
        // /api/livestack/preview) still call GetLatestJpeg() to
        // serve gallery thumbnails, that's a different consumer
        // that wants a static stretched image. Per-frame WS goes
        // RAW + LZ4 + client-side WebGL stretch every time.
        // Tag calibration frames (BIAS/DARK/FLAT) so the client renders them
        // with a neutral global stretch instead of the OSC per-channel
        // sky-neutralising stretch — on a flat noise frame the per-channel path
        // amplifies tiny channel offset differences into a strong colour cast
        // (the "bias is all pink under auto-stretch" report).
        var itype = (sourceData.MetaData?.Exposure?.ImageType ?? "").Trim().ToUpperInvariant();
        int calibration = (itype is "BIAS" or "DARK" or "FLAT" or "DARKFLAT") ? 1 : 0;
        var header = buffer.GetStreamHeader(frameKind, calibration);
        // MEMOPT: the payload stays in a POOLED (oversized) buffer — only the
        // first compressedLen bytes are real. Returned to the pool below, so the
        // per-frame ~20 MB Large Object Heap allocation is gone entirely.
        var compressed = buffer.RentLz4Compressed(out int compressedLen);

        _logger.LogInformation(
            "Relaying image {W}x{H} ({BitDepth}-bit): {RawMB:F1}MB raw -> {CompMB:F1}MB LZ4 ({Ratio:F1}x) to {Count} clients",
            buffer.Width, buffer.Height, buffer.BitDepth,
            (double)imageData.Data.Length * 2 / (1024 * 1024),
            (double)compressedLen / (1024 * 1024),
            (double)imageData.Data.Length * 2 / Math.Max(compressedLen, 1),
            _clients.Count);

        // MEMOPT: send the envelope as TWO WebSocket fragments instead of
        // building one combined array. Concatenating used to allocate a second
        // ~20 MB Large Object Heap block per frame purely to memcpy the payload
        // into it, so 40 MB was live to deliver 20 MB — the exact kind of LOH
        // churn that fragments the heap on a 1 GB SBC. The browser reassembles
        // fragments into a single message event, so the client is unchanged.
        var prefix = new byte[4 + header.Length];
        BitConverter.GetBytes(header.Length).CopyTo(prefix, 0);
        header.CopyTo(prefix, 4);

        // WSDRAIN: the fan-out is FIRE-AND-FORGET. Awaiting it charged the full
        // raw-frame drain to every capture: a ~19 MB SV405CC sub takes 10-20 s
        // to push over an SBC WiFi uplink (measured lastSendMs on a snap loop),
        // and in a SEQUENTIAL capture loop the previous send has always
        // finished before the next frame, so the per-client skip-if-busy
        // backpressure (SendLock.Wait(0)) never fired — the exposure cadence was
        // gated by the browser's download speed, not the camera. This is why the
        // SV405CC "took 30-42 s/frame and degraded on the OPi5Pro" while the
        // smaller SV605CC and every NINA-desktop rig (no raw-over-WiFi relay in
        // the capture path) were fine. Detaching it makes a slow client simply
        // SKIP frames instead of throttling capture. The synchronous work above
        // (LatestImage cache + LZ4) already ran, so /api/image/latest and
        // GetLatestJpeg see this frame immediately. CancellationToken.None on
        // purpose: the drain outlives the caller's request scope, and a caller
        // CTS disposed after return must never fault a healthy in-flight send.
        _ = BroadcastFrameAsync(prefix, compressed, compressedLen, CancellationToken.None)
            .ContinueWith(t => {
                System.Buffers.ArrayPool<byte>.Shared.Return(compressed);
                if (t.IsFaulted)
                    _logger.LogWarning(t.Exception, "Background image relay drain faulted");
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Efficient video-stream path: instead of the full RAW buffer, send a
    /// downscaled, auto-stretched JPEG (tagged <see cref="FrameKind.Video"/>)
    /// over the same <c>/ws/image-stream</c> envelope. The browser already
    /// decodes headered-JPEG frames and routes them to videoCaptureCanvas,
    /// so this is a server-only change. The full-resolution RAW frame is
    /// still handed to recording subscribers untouched (SER stays raw).
    ///
    /// <para>Bounded by a single in-flight render guard: if the previous
    /// frame is still encoding, this frame is dropped rather than queued —
    /// the stream stays smooth at the rate the Pi can actually encode +
    /// the link can carry, instead of building an unbounded backlog.</para>
    /// </summary>
    /// <returns>true when a JPEG was actually rendered and broadcast to
    /// clients; false when skipped (no clients, a render already in
    /// flight, or render failure). The caller uses this to count the
    /// real transmission rate vs the capture rate.</returns>
    public async Task<bool> RelayVideoJpegAsync(IImageData imageData,
                                          int maxDim = 1280, int quality = 70,
                                          FrameKind kind = FrameKind.Video,
                                          CancellationToken ct = default) {
        if (_clients.IsEmpty) return false;
        // Drop-if-busy: keep CPU + latency bounded under fast frame rates.
        if (Interlocked.CompareExchange(ref _videoRenderInFlight, 1, 0) != 0) return false;
        try {
            var src = ApplyVerticalFlipIfEnabled(imageData);
            var resolved = ResolveBayerOverride(src.Properties.BayerPattern);

            byte[] jpeg;
            try {
                jpeg = await Task.Run(() =>
                    FitsThumbnailer.RenderJpegFromImageData(src, maxDim, quality, resolved), ct);
            } catch (Exception ex) {
                _logger.LogDebug(ex, "Video JPEG render failed (skipping frame)");
                return false;
            }
            if (jpeg == null || jpeg.Length == 0) return false;

            // Reuse the stream-header envelope (so the browser's headered-
            // JPEG path picks up the FrameKind and routes to the video
            // canvas). The W/H/bayer fields describe the source frame but
            // the client ignores them for JPEG payloads.
            var buffer = ImageBuffer.FromImageData(src, resolved);
            // Record the latest frame so annotate / plate-solve / crop (which
            // read LatestImageData) work off the live video frame. The video
            // stream path never set this, so in a LIVE/PREVIEW view fed by the
            // stream, annotate reported "No image available". Reuses the buffer
            // already built for the wire, so no extra cost.
            _latestImage = buffer;
            _latestImageData = src;
            var header = buffer.GetStreamHeader((int)kind);
            var frame = new byte[4 + header.Length + jpeg.Length];
            BitConverter.GetBytes(header.Length).CopyTo(frame, 0);
            header.CopyTo(frame, 4);
            jpeg.CopyTo(frame, 4 + header.Length);

            await BroadcastFrameAsync(frame, ct);
            return true;
        } finally {
            Interlocked.Exchange(ref _videoRenderInFlight, 0);
        }
    }

    /// <summary>
    /// Broadcast a 3-plane (planar R,G,B) RGB image as a downscaled,
    /// per-channel auto-stretched JPEG tagged <paramref name="kind"/>.
    /// Used by the colour live-stacker so the LIVE canvas shows the
    /// debayered RGB stack without a client-side RGB-raw render path —
    /// the browser decodes the colour JPEG and the existing headered-JPEG
    /// route draws it on the frame's canvas. Same drop-if-busy guard as
    /// <see cref="RelayVideoJpegAsync"/>.
    /// </summary>
    public async Task<bool> RelayRgbJpegAsync(IImageData rgb,
                                        int maxDim = 1280, int quality = 80,
                                        FrameKind kind = FrameKind.Live,
                                        CancellationToken ct = default) {
        if (_clients.IsEmpty) return false;
        if (Interlocked.CompareExchange(ref _videoRenderInFlight, 1, 0) != 0) return false;
        try {
            byte[] jpeg;
            try {
                jpeg = await Task.Run(() => FitsThumbnailer.RenderJpegFromRgbPlanes(
                    rgb.Data, rgb.Properties.Width, rgb.Properties.Height,
                    rgb.Properties.BitDepth, maxDim, quality), ct);
            } catch (Exception ex) {
                _logger.LogDebug(ex, "RGB JPEG render failed (skipping frame)");
                return false;
            }
            if (jpeg == null || jpeg.Length == 0) return false;

            var buffer = ImageBuffer.FromImageData(rgb);
            // Record the latest frame for annotate / plate-solve / crop, same
            // as RelayVideoJpegAsync. The colour live-stacker's RGB stack is
            // what's on the LIVE canvas, so annotate should target it.
            _latestImage = buffer;
            _latestImageData = rgb;
            // THE "colour frame flips to B&W" BUG (field, 2026-07-16).
            //
            // This used to set _latestJpeg = null, reasoning that invalidating the
            // cache would make /api/livestack/preview "re-encode from THIS colour
            // stack". It does re-encode — as GREYSCALE. GetLatestJpeg falls back to
            // ImageBuffer.ToJpeg(), whose only encoder is JpegHelper.EncodeGrayscale;
            // there is no colour path through it, because ImageBuffer carries a
            // single plane. So the sequence the user kept seeing was:
            //   1. this method renders the RGB JPEG and broadcasts it  → colour
            //   2. ...and then throws that exact JPEG away
            //   3. the client pulls /api/livestack/preview             → greyscale
            //   4. the greyscale preview paints over the colour frame  → B&W
            // Confirmed by LIVE-TRACE: every frame logged
            // `out{branch=COLOUR(debayer-per-plane -> RGB JPEG)} ch=3`, i.e. the
            // server ALWAYS sent colour — the flip was never on the stacking side,
            // which is why chasing _colorActive / CCD_CFA dropouts never found it.
            // The histograms of the colour and B&W screenshots were identical
            // (MAX 59206, MIN 223) precisely because the DATA never changed: same
            // _latestImage, different encoder.
            //
            // So: cache the colour JPEG we just rendered. The preview then serves
            // the exact image that's on the canvas, and skips a redundant re-encode
            // (that fallback took 2.7 s on the SBC for a 4144x2822 frame).
            // ToJpeg()'s greyscale stays correct for the mono/raw path in
            // RelayImageAsync, which still nulls the cache on purpose.
            _latestJpeg = jpeg;
            if (kind == FrameKind.LiveStack) {
                // Exactly the picture the LIVE canvas is showing, so the
                // preview endpoint serves it verbatim instead of re-encoding
                // (and, before the split above, instead of re-encoding it
                // greyscale).
                lock (_stackGate) { _stackImage = rgb; _stackJpeg = jpeg; }
            }
            var header = buffer.GetStreamHeader((int)kind);
            var frame = new byte[4 + header.Length + jpeg.Length];
            BitConverter.GetBytes(header.Length).CopyTo(frame, 0);
            header.CopyTo(frame, 4);
            jpeg.CopyTo(frame, 4 + header.Length);
            await BroadcastFrameAsync(frame, ct);
            return true;
        } finally {
            Interlocked.Exchange(ref _videoRenderInFlight, 0);
        }
    }

    private int _videoRenderInFlight;

    /// <summary>Fan a pre-built binary frame out to every connected
    /// /ws/image-stream client, with per-client back-pressure (skip a
    /// client still sending the previous frame) and dead-client reaping.
    /// Shared by the RAW (LIVE/PREVIEW) and JPEG (video) paths.</summary>
    private Task BroadcastFrameAsync(byte[] frame, CancellationToken ct)
        => BroadcastFrameAsync(frame, null, 0, ct);

    /// <summary>Fan a frame out to every client. When <paramref name="payload"/>
    /// is non-null the frame is sent as TWO WebSocket fragments (prefix then
    /// payload) so we never have to concatenate a second multi-MB buffer just
    /// to hand it to SendAsync. Receivers see one reassembled message either
    /// way. The per-client SendLock is held across both fragments, so they can
    /// never interleave with another frame.</summary>
    private async Task BroadcastFrameAsync(byte[] frame, byte[]? payload, int payloadLength,
                                           CancellationToken ct) {
        var deadClients = new System.Collections.Concurrent.ConcurrentBag<string>();

        // Fan out CONCURRENTLY. This used to await each client in turn, so one
        // slow consumer (a tablet on WiFi pulling a ~19 MB raw frame) delayed
        // every other client AND the capture request that awaits this relay -
        // a snap POST could sit for tens of seconds after the exposure was
        // already done. Each client has its own SendLock + skip-if-busy
        // backpressure, so they are independent by construction.
        await Task.WhenAll(_clients.Select(kv => SendToClientAsync(kv.Key, kv.Value, frame, payload, payloadLength, deadClients, ct)));

        foreach (var id in deadClients) {
            _logger.LogInformation("Removing dead client: {Id}", id);
            UnregisterClient(id);
        }
    }

    private async Task SendToClientAsync(string id, ClientEntry entry, byte[] frame, byte[]? payload,
                                         int payloadLength,
                                         System.Collections.Concurrent.ConcurrentBag<string> deadClients,
                                         CancellationToken ct) {
            if (entry.Ws.State != WebSocketState.Open) {
                deadClients.Add(id);
                return;
            }

            // Skip clients that are still sending the previous frame
            // (backpressure). Guard the whole semaphore interaction: a
            // client can be unregistered concurrently, and even though we
            // no longer dispose its SendLock, any per-client fault here
            // must only kill THAT client — never propagate out and fail
            // the capture that triggered this relay.
            bool acquired;
            try {
                acquired = entry.SendLock.Wait(0);
            } catch (ObjectDisposedException) {
                deadClients.Add(id);
                return;
            }
            if (!acquired) {
                entry.SkippedFrames++;
                if (entry.SkippedFrames % 10 == 0) {
                    _logger.LogWarning("Client {Id} skipped {Count} frames (slow consumer)", id, entry.SkippedFrames);
                }
                return;
            }

            try {
                using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                sendCts.CancelAfter(SendTimeout);
                var sendStart = DateTime.UtcNow;
                if (payload == null) {
                    await entry.Ws.SendAsync(frame, WebSocketMessageType.Binary, true, sendCts.Token);
                } else {
                    // Fragmented: header first (endOfMessage:false), then the
                    // payload. The peer reassembles them into one message.
                    await entry.Ws.SendAsync(frame, WebSocketMessageType.Binary, false, sendCts.Token);
                    // payload is a POOLED, oversized buffer: send only the real bytes.
                    await entry.Ws.SendAsync(payload.AsMemory(0, payloadLength),
                                             WebSocketMessageType.Binary, true, sendCts.Token);
                }
                entry.LastSendDuration = DateTime.UtcNow - sendStart;
                entry.ConsecutiveFailures = 0;
                entry.SkippedFrames = 0;
            } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
                entry.ConsecutiveFailures++;
                _logger.LogWarning("Send to client {Id} timed out (failure {N}/{Max})",
                    id, entry.ConsecutiveFailures, MaxConsecutiveFailures);
                if (entry.ConsecutiveFailures >= MaxConsecutiveFailures)
                    deadClients.Add(id);
            } catch (WebSocketException ex) {
                entry.ConsecutiveFailures++;
                _logger.LogWarning("WebSocket error for client {Id}: {Msg} (failure {N}/{Max})",
                    id, ex.Message, entry.ConsecutiveFailures, MaxConsecutiveFailures);
                if (entry.ConsecutiveFailures >= MaxConsecutiveFailures)
                    deadClients.Add(id);
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Unexpected error sending to client {Id}", id);
                deadClients.Add(id);
            } finally {
                // Release can't throw for a non-disposed semaphore, but
                // guard anyway so a teardown race never escapes the relay.
                try { entry.SendLock.Release(); } catch (ObjectDisposedException) { }
            }
    }

    /// <summary>The current live stack as a JPEG, or null when there is no
    /// stack. Distinct from <see cref="GetLatestJpeg"/>, which answers with the
    /// most recent frame of any kind: a preview snap, an autofocus exposure or
    /// the previous target's stack all qualify there and none of them is the
    /// stack the caller asked for.</summary>
    public byte[]? GetStackJpeg(int quality = 85) {
        IImageData? img;
        lock (_stackGate) {
            if (_stackJpeg != null) return _stackJpeg;
            img = _stackImage;
        }
        if (img == null || img.Properties.Width <= 0 || img.Properties.Height <= 0) return null;
        try {
            var w = img.Properties.Width;
            var h = img.Properties.Height;
            // A colour stack arrives as three planar channels in one array.
            var jpeg = img.Data.Length >= w * h * 3
                ? FitsThumbnailer.RenderJpegFromRgbPlanes(
                      img.Data, w, h, img.Properties.BitDepth, Math.Max(w, h), quality)
                : ImageBuffer.FromImageData(img).ToJpeg(quality);
            lock (_stackGate) { if (ReferenceEquals(_stackImage, img)) _stackJpeg = jpeg; }
            return jpeg;
        } catch (Exception ex) {
            _logger.LogWarning(ex, "JPEG encode of the {W}x{H} stack failed",
                               img.Properties.Width, img.Properties.Height);
            return null;
        }
    }

    /// <summary>Forget the stack picture. Called when the stacker resets, so a
    /// preview pulled during the gap before the first new frame cannot answer
    /// with the previous stack.</summary>
    public void ClearStack() {
        lock (_stackGate) { _stackImage = null; _stackJpeg = null; }
    }

    public byte[]? GetLatestJpeg(int quality = 85) {
        var img = _latestImage;
        // Skip the encode when no real frame is buffered yet, the
        // initial state has a 0x0 ImageBuffer (placeholder), and
        // JpegHelper rightly refuses it. Surfacing null lets the
        // endpoint return 404 instead of crashing the request.
        if (img == null || img.Width <= 0 || img.Height <= 0) return null;
        try {
            if (_latestJpeg != null) return _latestJpeg;

            // FIELD8-1: an OSC frame has to be DEBAYERED here, not handed to
            // ImageBuffer.ToJpeg().
            //
            // ToJpeg is greyscale by construction (one plane in, one plane
            // out), which is correct for the WS path: there the raw CFA
            // buffer travels with its pattern in the header and the browser
            // debayers it. These one-shot JPEG endpoints have no such second
            // half. Serving the mosaic through a grey encoder bakes the
            // checkerboard into the picture: on the tablet the LIVE canvas
            // showed a grey mesh with almost no stars, which reads as a
            // broken camera (field report, 2026-07-31, ASI585MC on the Q6A).
            // Measured on that frame: neighbouring pixels differed 2.15x more
            // than pixels two columns apart, the signature of an
            // un-demosaiced CFA, and the histogram carried one hump per
            // colour instead of one.
            var pattern = img.BayerPattern;
            if (pattern != BayerPatternEnum.None && pattern != BayerPatternEnum.Auto) {
                // The buffer is array-backed (ImageBuffer wraps a ushort[]),
                // so this is a view, not a 16 MB copy of an 8 MP frame. The
                // ToArray fallback is only there to keep a future
                // non-array-backed buffer working.
                var cfa = System.Runtime.InteropServices.MemoryMarshal
                              .TryGetArray(img.PixelData, out var seg) && seg.Array != null
                              && seg.Offset == 0 && seg.Count == seg.Array.Length
                          ? seg.Array
                          : img.PixelData.ToArray();
                var ch = NINA.Image.ImageAnalysis.BayerDebayer.Bilinear(
                    cfa, img.Width, img.Height, pattern);
                var planes = new ushort[img.Width * img.Height * 3];
                ch.R.CopyTo(planes, 0);
                ch.G.CopyTo(planes, img.Width * img.Height);
                ch.B.CopyTo(planes, img.Width * img.Height * 2);
                // Native size, matching what the greyscale path served: this
                // is the LIVE canvas image, not a gallery thumbnail. maxDim is
                // a bound, not a target, and it is NOT optional: 0 makes the
                // renderer scale by 0/longest and hand back a 1x1 pixel.
                return _latestJpeg = FitsThumbnailer.RenderJpegFromRgbPlanes(
                    planes, img.Width, img.Height, img.BitDepth,
                    Math.Max(img.Width, img.Height), quality);
            }
            return _latestJpeg = img.ToJpeg(quality);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "JPEG encode of {W}x{H} frame failed", img.Width, img.Height);
            return null;
        }
    }

    public ImageBuffer? GetLatestImage() => _latestImage;

    public int ClientCount => _clients.Count;

    public void Dispose() {
        foreach (var (_, entry) in _clients) {
            try { entry.Ws.Dispose(); } catch { }
            // SendLock intentionally not disposed — see UnregisterClient.
        }
        _clients.Clear();
    }

    /// <summary>FIELD-3: only <see cref="StreamMode.Raw"/> is used
    /// on the WS path now. The enum + Jpeg value stay for binary
    /// back-compat with any callers that haven't been re-built yet
    /// (the legacy SetClientMode silently rejects requests for Jpeg).
    /// </summary>
    public enum StreamMode { Raw, Jpeg }

    private class ClientEntry {
        public System.Net.WebSockets.WebSocket Ws { get; }
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        public int ConsecutiveFailures { get; set; }
        public int SkippedFrames { get; set; }
        // FIELD-3: every client is RAW now. Field kept so the existing
        // GetClientStats() payload stays shape-compatible.
        public StreamMode Mode { get; set; } = StreamMode.Raw;
        public StreamMode RequestedMode { get; set; } = StreamMode.Raw;
        public TimeSpan LastSendDuration { get; set; }

        public ClientEntry(System.Net.WebSockets.WebSocket ws) => Ws = ws;
    }

    /// <summary>FIELD-3: legacy compat shim. JPEG mode is gone, so a
    /// Jpeg request is silently coerced to Raw with a debug log. The
    /// method stays because ImageStreamHandler still calls it on the
    /// handshake message.</summary>
    public void SetClientMode(string id, StreamMode mode) {
        if (_clients.TryGetValue(id, out var entry)) {
            if (mode == StreamMode.Jpeg) {
                _logger.LogDebug(
                    "Client {Id} requested JPEG stream; coerced to Raw (JPEG WS streaming removed)", id);
            }
            entry.Mode = StreamMode.Raw;
            entry.RequestedMode = StreamMode.Raw;
        }
    }

    /// <summary>Diagnostics endpoint. FIELD-3: adaptive-bandwidth
    /// counters removed; the kept fields are the ones that still
    /// matter for slow-consumer triage (skipped frames + WS send
    /// failures).</summary>
    public IEnumerable<object> GetClientStats() {
        return _clients.Select(kv => new {
            id = kv.Key,
            currentMode = kv.Value.Mode.ToString(),
            lastSendMs = (int)kv.Value.LastSendDuration.TotalMilliseconds,
            skipped = kv.Value.SkippedFrames,
            failures = kv.Value.ConsecutiveFailures
        });
    }
}