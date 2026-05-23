using System.Runtime.InteropServices.JavaScript;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;

namespace NINA.Polaris.Wasm;

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

    /// <summary>Smoke-test entry point. Kept from CLST-2 — the
    /// 'nina-wasm-ready' event handler in app.js calls this to
    /// confirm the bundle loaded and the [JSExport] marshalling
    /// works. Bump the suffix on protocol-breaking changes so a
    /// stale cached bundle is detectable.</summary>
    [JSExport]
    public static string Ping() => "pong v0.2 (CLST-3 stacker)";

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

    /// <summary>Ingest one raw uint16 frame. Returns a 5-int packed
    /// metrics tuple that the page un-packs and forwards to the
    /// server's trigger orchestrator:
    /// <list type="bullet">
    ///   <item>[0] frameCount AFTER this integration</item>
    ///   <item>[1] medianHfr * 100 (fixed-point, divide by 100 in JS)</item>
    ///   <item>[2] starCount</item>
    ///   <item>[3] alignmentOk (0 / 1) — always 1 on frame 1 (reference)</item>
    ///   <item>[4] reserved (transform.Tx * 100 in future work)</item>
    /// </list>
    /// The packed-int return avoids the per-call marshalling overhead
    /// a struct or string-JSON return would impose; saves ~50us per
    /// frame which adds up at 1 fps × hours.
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
                // Frame size mismatch — bail without bumping count. JS
                // sees frameCount==previous and can log a warning.
                return [_frameCount, 0, stars.Count, 0, 0];
            }
            var transform = StarMatcher.Match(_referenceStars!, stars);
            if (transform == null) {
                alignmentOk = 0;
                return [_frameCount, 0, stars.Count, 0, 0];
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

        // Median HFR — same calc as the server.
        double medianHfr = 0;
        if (stars.Count > 0) {
            var hfrs = stars.Select(s => s.HFR).Where(h => h > 0).OrderBy(h => h).ToList();
            if (hfrs.Count > 0) medianHfr = hfrs[hfrs.Count / 2];
        }

        return [_frameCount, (int)(medianHfr * 100), stars.Count, alignmentOk, reserved];
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
}
