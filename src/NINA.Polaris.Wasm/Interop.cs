using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using System.Text.Json.Serialization;
using NINA.Image.Editor;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;

namespace NINA.Polaris.Wasm;

/// <summary>
/// Source-generated JSON metadata for the editor's <see cref="EditParams"/>
/// graph. Required under WASM AOT with full trimming, without this the
/// trimmer strips the property setters/ctors that reflection-based
/// <c>JsonSerializer.Deserialize&lt;EditParams&gt;</c> needs, and slider
/// edits silently deserialise to all-defaults (so the WASM preview shows
/// the unedited image regardless of slider state). The source generator
/// emits the exact metadata at compile time and roots the types from the
/// trimmer's perspective.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(EditParams))]
[JsonSerializable(typeof(WhiteBalanceParams))]
[JsonSerializable(typeof(LightParams))]
[JsonSerializable(typeof(ColorParams))]
[JsonSerializable(typeof(DetailParams))]
[JsonSerializable(typeof(EffectsParams))]
[JsonSerializable(typeof(ToneCurveParams))]
[JsonSerializable(typeof(CurvePoint))]
[JsonSerializable(typeof(CropParams))]
[JsonSerializable(typeof(IReadOnlyList<CurvePoint>))]
[JsonSerializable(typeof(List<CurvePoint>))]
internal partial class EditorJsonContext : JsonSerializerContext { }

/// <summary>
/// JS-callable surface for the browser-side live-stack module.
///
/// The page receives raw uint16 frames over /ws/image-stream (same
/// wire format as before), passes the pixel buffer to <see cref="AddFrame"/>,
/// gets back a metrics struct (frame count, HFR, star count,
/// alignment success), and either reads the accumulated stack via
/// <see cref="GetStackedResult"/> for display, or sends the metrics
/// back to the server via <c>{type:'client-stack-progress'}</c> so
/// the LSTR trigger orchestrator still fires AF/recenter.
///
/// Uses the SAME StarDetector / StarMatcher / AffineTransform /
/// ImageResampler implementations the server runs (referenced from
/// NINA.Image.Portable) so client-side output matches server-side
/// byte-for-byte on the same inputs.
/// </summary>
public static partial class Interop {

    // Per-session state. Lifetime: from Initialize() → Reset() or page
    // unload. Single-threaded by virtue of the JS event loop driving
    // AddFrame calls; no locking needed.
    private static readonly StarDetector _detector = new() { MaxStars = 200 };
    private static float[]? _stackBuffer;
    private static int[]? _countBuffer;
    private static int _width;
    private static int _height;
    private static int _frameCount;
    private static List<DetectedStar>? _referenceStars;

    /// <summary>Smoke-test entry point. Kept from CLST-2, the
    /// 'nina-wasm-ready' event handler in app.js calls this to
    /// confirm the bundle loaded and the [JSExport] marshalling
    /// works. Bump the suffix on protocol-breaking changes so a
    /// stale cached bundle is detectable.</summary>
    [JSExport]
    public static string Ping() => "pong v0.5 (CLST-3 stacker + ED-6 editor + JsonContext + SNR-5)";

    /// <summary>Reset accumulator buffers + reference frame. Called
    /// by the page's "Reset" button + automatically on page load
    /// before the first AddFrame.</summary>
    [JSExport]
    public static void Reset() {
        _stackBuffer = null;
        _countBuffer = null;
        _referenceStars = null;
        _width = 0;
        _height = 0;
        _frameCount = 0;
    }

    /// <summary>Ingest one raw uint16 frame. Returns a 7-int packed
    /// metrics tuple that the page un-packs and forwards to the
    /// server's trigger orchestrator:
    /// <list type="bullet">
    ///   <item>[0] frameCount AFTER this integration</item>
    ///   <item>[1] medianHfr * 100 (fixed-point, divide by 100 in JS)</item>
    ///   <item>[2] starCount</item>
    ///   <item>[3] alignmentOk (0 / 1), always 1 on frame 1 (reference)</item>
    ///   <item>[4] reserved (transform.Tx * 100)</item>
    ///   <item>[5] lastFrameSnr * 100 (this frame's background SNR)</item>
    ///   <item>[6] cumulativeSnr * 100 (running-mean stack's SNR)</item>
    /// </list>
    /// The packed-int return avoids the per-call marshalling overhead
    /// a struct or string-JSON return would impose; saves ~50us per
    /// frame which adds up at 1 fps × hours. SNRs are returned ×100
    /// (fixed-point with 2 decimal places of precision; SNR rarely
    /// exceeds 200 so int range is plenty).
    /// </summary>
    [JSExport]
    public static int[] AddFrame(int[] pixelsInt32, int width, int height) {
        // JS Uint16Array→Int32Array conversion is the cheapest interop
        // path right now; widen back to ushort[] here. Future work
        // could use JSMarshalAsAttribute(JSType.MemoryView) to share
        // the underlying buffer without a copy.
        var pixels = new ushort[pixelsInt32.Length];
        for (int i = 0; i < pixelsInt32.Length; i++) {
            pixels[i] = (ushort)(pixelsInt32[i] & 0xFFFF);
        }

        var stars = _detector.Detect(pixels, width, height);

        ushort[] alignedData;
        int alignmentOk = 1;
        int reserved = 0;

        if (_frameCount == 0) {
            _width = width;
            _height = height;
            int n = width * height;
            _stackBuffer = new float[n];
            _countBuffer = new int[n];
            _referenceStars = stars;
            alignedData = pixels;
        } else {
            if (width != _width || height != _height) {
                // Frame size mismatch, bail without bumping count. JS
                // sees frameCount==previous and can log a warning.
                return [_frameCount, 0, stars.Count, 0, 0, 0, 0];
            }
            var transform = StarMatcher.Match(_referenceStars!, stars);
            if (transform == null) {
                alignmentOk = 0;
                return [_frameCount, 0, stars.Count, 0, 0, 0, 0];
            }
            alignedData = ImageResampler.ApplyTransform(pixels, _width, _height, transform);
            reserved = (int)(transform.Tx * 100);
        }

        // Accumulate. Skip zeros (the resampler fills out-of-bounds
        // with 0; we don't want those to drag down the average).
        for (int i = 0; i < alignedData.Length && i < _stackBuffer!.Length; i++) {
            if (alignedData[i] > 0) {
                _stackBuffer[i] += alignedData[i];
                _countBuffer![i]++;
            }
        }
        _frameCount++;

        // Median HFR, same calc as the server.
        double medianHfr = 0;
        if (stars.Count > 0) {
            var hfrs = stars.Select(s => s.HFR).Where(h => h > 0).OrderBy(h => h).ToList();
            if (hfrs.Count > 0) medianHfr = hfrs[hfrs.Count / 2];
        }

        // SNR-5: per-frame SNR on the raw incoming pixels (NOT the
        // aligned/resampled buffer — alignment fills out-of-bounds with
        // zero, which would skew the background population). Cumulative
        // SNR on the running-mean of the accumulator, computed lazily
        // (one snapshot ushort[] alloc per frame, acceptable at 1 fps).
        double lastFrameSnr = ImageStatistics.ComputeBackgroundSnrFromData(pixels);
        double cumulativeSnr = 0;
        if (_stackBuffer != null && _countBuffer != null && _frameCount > 0) {
            var snapshot = new ushort[_stackBuffer.Length];
            for (int i = 0; i < _stackBuffer.Length; i++) {
                if (_countBuffer[i] > 0) {
                    snapshot[i] = (ushort)Math.Clamp(_stackBuffer[i] / _countBuffer[i], 0, 65535);
                }
            }
            cumulativeSnr = ImageStatistics.ComputeBackgroundSnrFromData(snapshot);
        }

        return [
            _frameCount,
            (int)(medianHfr * 100),
            stars.Count,
            alignmentOk,
            reserved,
            (int)(lastFrameSnr * 100),
            (int)(cumulativeSnr * 100)
        ];
    }

    /// <summary>Get the running-mean accumulated stack as ushort
    /// pixels. Returns empty array when no frame has been added yet.
    /// JS wraps as Uint16Array → feeds into the existing WebGL2
    /// stretch + debayer pipeline that already handles raw frames.</summary>
    [JSExport]
    public static int[] GetStackedResult() {
        if (_stackBuffer == null) return [];
        // int[] not ushort[] because the JSExport marshaller doesn't
        // grok ushort[] directly; JS does (val & 0xFFFF) on the way out.
        var result = new int[_stackBuffer.Length];
        for (int i = 0; i < _stackBuffer.Length; i++) {
            if (_countBuffer![i] > 0) {
                int v = (int)Math.Clamp(_stackBuffer[i] / _countBuffer[i], 0, 65535);
                result[i] = v;
            }
        }
        return result;
    }

    /// <summary>Current accumulator dimensions. Exposed so the page
    /// can size the canvas correctly without round-tripping the WS
    /// status payload. Returns [0, 0] before the first frame.</summary>
    [JSExport]
    public static int[] GetDimensions() => [_width, _height];

    // ───────────────────────────────────────────────────────────────────
    // ED-6: editor pipeline in the browser. Same math as the server's
    // ImageEditService, both reference NINA.Image.Editor.EditPipeline,
    // so a given EditParams produces byte-for-byte identical output
    // whether the user is running WASM-mode or server-mode.
    //
    // Single session in WASM (one buffer, statically held), matches
    // the existing live-stack pattern and keeps lifetime simple. If
    // the user opens a different file the JS calls EditorLoad again
    // and replaces the buffer. The server still owns the long-lived
    // session metadata + sidecar persistence.
    // ───────────────────────────────────────────────────────────────────

    private static byte[]? _editorWorking;
    private static int _editorWidth;
    private static int _editorHeight;
    private static int _editorChannels;

    /// <summary>
    /// Hand the WASM module a decoded working buffer (8-bit pixel space,
    /// same format the server's ImageEditService caches internally).
    /// The byte[] comes from /api/editor/raw which streams the server's
    /// auto-stretched working buffer over HTTP as raw bytes.
    /// <para>
    /// Channels is 1 (mono) or 3 (interleaved RGB). Width × height ×
    /// channels must equal pixels.Length.
    /// </para>
    /// </summary>
    [JSExport]
    public static void EditorLoad(byte[] pixels, int width, int height, int channels) {
        _editorWorking = pixels;
        _editorWidth = width;
        _editorHeight = height;
        _editorChannels = channels;
    }

    /// <summary>
    /// Apply <paramref name="editsJson"/> (an EditParams record serialised
    /// as JSON by app.js) to a downsampled copy of the working buffer and
    /// return raw 8-bit pixel bytes the page can put on a &lt;canvas&gt;
    /// via ImageData. Output length is <c>outWidth × outHeight × channels</c>;
    /// query <see cref="EditorGetOutputDims"/> for the dimensions after
    /// the most recent ApplyEdit.
    /// <para>
    /// maxDim caps the long side (matches the server's preview maxDim
    /// default of 1600); passing 0 disables downscaling.
    /// </para>
    /// </summary>
    [JSExport]
    public static byte[] EditorApplyEdit(string editsJson, int maxDim) {
        if (_editorWorking == null) return Array.Empty<byte>();

        EditParams edits;
        try {
            edits = JsonSerializer.Deserialize(editsJson, EditorJsonContext.Default.EditParams)
                    ?? EditParams.Defaults;
        } catch (Exception ex) {
            Console.WriteLine($"[Polaris.Wasm] EditorApplyEdit: edits deserialise failed: {ex.Message}");
            edits = EditParams.Defaults;
        }

        var working = (byte[])_editorWorking.Clone();
        int w = _editorWidth, h = _editorHeight;

        // Downsample first (same approach as the server), pipeline runs
        // on the smaller buffer when no crop is active.
        if (edits.Crop == null && maxDim > 0 && (w > maxDim || h > maxDim)) {
            double scale = (double)maxDim / Math.Max(w, h);
            int tw = (int)Math.Round(w * scale);
            int th = (int)Math.Round(h * scale);
            var (downscaled, dw, dh) = EditPipeline.ApplyCropResize(
                working, w, h, _editorChannels, null, tw, th);
            working = downscaled; w = dw; h = dh;
        }

        EditPipeline.Apply(working, w, h, _editorChannels, edits);

        if (edits.Crop != null) {
            var (cropped, cw, ch) = EditPipeline.ApplyCropResize(
                working, w, h, _editorChannels, edits.Crop, null, null);
            if (maxDim > 0 && (cw > maxDim || ch > maxDim)) {
                double scale = (double)maxDim / Math.Max(cw, ch);
                int tw = (int)Math.Round(cw * scale);
                int th = (int)Math.Round(ch * scale);
                var (rs, rw, rh) = EditPipeline.ApplyCropResize(
                    cropped, cw, ch, _editorChannels, null, tw, th);
                working = rs; w = rw; h = rh;
            } else {
                working = cropped; w = cw; h = ch;
            }
        }

        _editorOutW = w;
        _editorOutH = h;
        return working;
    }

    private static int _editorOutW;
    private static int _editorOutH;

    /// <summary>
    /// Dimensions of the most recent EditorApplyEdit output. Returned as
    /// [width, height, channels] so the page can size its ImageData /
    /// canvas correctly. Returns [0,0,0] before the first ApplyEdit.
    /// </summary>
    [JSExport]
    public static int[] EditorGetOutputDims() => [_editorOutW, _editorOutH, _editorChannels];

    /// <summary>
    /// Apply edits then compute a 256-bin histogram per channel. Returns
    /// length 256 (mono) or 768 (RGB; R[0..255]|G[256..511]|B[512..767]),
    /// matching the server's <c>/api/editor/histogram</c> contract exactly
    /// so the JS chart code is mode-agnostic.
    /// </summary>
    [JSExport]
    public static int[] EditorComputeHistogram(string editsJson) {
        if (_editorWorking == null) return Array.Empty<int>();

        EditParams edits;
        try {
            edits = JsonSerializer.Deserialize(editsJson, EditorJsonContext.Default.EditParams)
                    ?? EditParams.Defaults;
        } catch (Exception ex) {
            Console.WriteLine($"[Polaris.Wasm] EditorComputeHistogram: edits deserialise failed: {ex.Message}");
            edits = EditParams.Defaults;
        }

        var working = (byte[])_editorWorking.Clone();
        int w = _editorWidth, h = _editorHeight;
        // Same 512px downsample as the server, statistically equivalent
        // for chart purposes + ~50x faster.
        if (w > 512 || h > 512) {
            double scale = 512.0 / Math.Max(w, h);
            int tw = (int)Math.Round(w * scale);
            int th = (int)Math.Round(h * scale);
            var (down, dw, dh) = EditPipeline.ApplyCropResize(
                working, w, h, _editorChannels, null, tw, th);
            working = down; w = dw; h = dh;
        }
        EditPipeline.Apply(working, w, h, _editorChannels, edits);

        if (_editorChannels == 1) {
            var hist = new int[256];
            for (int i = 0; i < working.Length; i++) hist[working[i]]++;
            return hist;
        } else {
            var hist = new int[768];
            for (int i = 0; i < working.Length; i += 3) {
                hist[working[i]]++;
                hist[256 + working[i + 1]]++;
                hist[512 + working[i + 2]]++;
            }
            return hist;
        }
    }

    /// <summary>Free the editor working buffer. Called when the user
    /// closes the editor / switches sources, so the WASM heap doesn't
    /// hold a 200MB master across overnight sessions.</summary>
    [JSExport]
    public static void EditorRelease() {
        _editorWorking = null;
        _editorWidth = 0;
        _editorHeight = 0;
        _editorChannels = 0;
        _editorOutW = 0;
        _editorOutH = 0;
    }
}
