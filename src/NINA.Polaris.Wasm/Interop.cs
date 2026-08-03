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

using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using System.Text.Json.Serialization;
using NINA.Core.Enum;
using NINA.Image.Editor;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;

// This assembly only ever runs in the browser: [JSExport] / [JSImport] are
// themselves browser-only, so declare it once here rather than annotating
// every member of the interop surface. The MSBuild <SupportedOSPlatform>
// property does not emit this attribute; it has to be written in code.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("browser")]

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

    // Colour (OSC) session state. When the first frame carries a real Bayer
    // pattern the session locks to colour: every frame is debayered to R/G/B,
    // each plane is warped by the alignment transform (interpolation stays
    // WITHIN a colour channel -> no CFA smear) and the three planes accumulate
    // separately. Mirrors LiveStackingService's colour path so the browser-
    // offloaded stack matches the server. Warping the raw CFA mosaic (the old
    // behaviour) bilinear-blended R/G/G/B neighbours, which washed the stack to
    // grey and buried faint nebulosity — the "live stack goes black-and-white
    // while the individual frames are fine" field bug.
    private static bool _colorActive;
    private static BayerPatternEnum _bayerPattern = BayerPatternEnum.None;
    private static float[]? _stackR;
    private static float[]? _stackG;
    private static float[]? _stackB;
    // Per-frame debayer + warp scratch, reused across frames like the server's
    // session scratch so a colour session doesn't churn 6x ushort[N] per frame.
    private static ushort[]? _dbR, _dbG, _dbB, _warpR, _warpG, _warpB;

    /// <summary>Smoke-test entry point. Kept from CLST-2, the
    /// 'nina-wasm-ready' event handler in app.js calls this to
    /// confirm the bundle loaded and the [JSExport] marshalling
    /// works. Bump the suffix on protocol-breaking changes so a
    /// stale cached bundle is detectable.</summary>
    [JSExport]
    public static string Ping() => "pong v0.6 (CLST-3 stacker + colour debayer-align + ED-6 editor + JsonContext + SNR-5)";

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
        _colorActive = false;
        _bayerPattern = BayerPatternEnum.None;
        _stackR = _stackG = _stackB = null;
        _dbR = _dbG = _dbB = _warpR = _warpG = _warpB = null;
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
    public static int[] AddFrame(int[] pixelsInt32, int width, int height, int bayerPattern) {
        // JS Uint16Array→Int32Array conversion is the cheapest interop
        // path right now; widen back to ushort[] here. Future work
        // could use JSMarshalAsAttribute(JSType.MemoryView) to share
        // the underlying buffer without a copy.
        var pixels = new ushort[pixelsInt32.Length];
        for (int i = 0; i < pixelsInt32.Length; i++) {
            pixels[i] = (ushort)(pixelsInt32[i] & 0xFFFF);
        }

        var stars = _detector.Detect(pixels, width, height);

        ushort[] alignedData;                     // mono path only
        int alignmentOk = 1;
        int reserved = 0;
        AffineTransform? usedTransform = null;    // null on the reference frame

        if (_frameCount == 0) {
            _width = width;
            _height = height;
            int n = width * height;
            _countBuffer = new int[n];
            _referenceStars = stars;
            // Lock the session's colour mode on the first frame, exactly like
            // LiveStackingService: a real Bayer pattern -> colour (debayer +
            // per-plane warp), otherwise mono (accumulate the CFA/mono frame).
            _bayerPattern = ResolvePattern(bayerPattern);
            _colorActive = IsColourPattern(_bayerPattern);
            if (_colorActive) {
                _stackR = new float[n];
                _stackG = new float[n];
                _stackB = new float[n];
            } else {
                _stackBuffer = new float[n];
            }
            alignedData = pixels;                 // reference: no warp
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
            usedTransform = transform;
            reserved = (int)(transform.Tx * 100);
            // Mono warps the raw frame here; colour defers the warp to the
            // per-plane debayer path below (warping the raw CFA mosaic would
            // bilinear-blend adjacent R/G/G/B pixels and desaturate the stack).
            alignedData = _colorActive
                ? pixels
                : ImageResampler.ApplyTransform(pixels, _width, _height, transform);
        }

        if (_colorActive) {
            // Debayer the ORIGINAL frame to RGB, then warp each plane with the
            // transform that aligned it (null on the reference frame). Because
            // interpolation now stays inside a single colour channel there is
            // no CFA smear. Accumulate per channel, sharing one coverage count.
            int n = _width * _height;
            EnsureScratch(ref _dbR, n);
            EnsureScratch(ref _dbG, n);
            EnsureScratch(ref _dbB, n);
            BayerDebayer.Bilinear(pixels, _width, _height, _bayerPattern, _dbR!, _dbG!, _dbB!);
            ushort[] r = _dbR!, g = _dbG!, b = _dbB!;
            if (usedTransform != null) {
                EnsureScratch(ref _warpR, n);
                EnsureScratch(ref _warpG, n);
                EnsureScratch(ref _warpB, n);
                r = ImageResampler.ApplyTransform(_dbR!, _width, _height, usedTransform, _warpR!);
                g = ImageResampler.ApplyTransform(_dbG!, _width, _height, usedTransform, _warpG!);
                b = ImageResampler.ApplyTransform(_dbB!, _width, _height, usedTransform, _warpB!);
            }
            for (int i = 0; i < n; i++) {
                // Off-canvas after warp is 0 in all three planes.
                if (r[i] > 0 || g[i] > 0 || b[i] > 0) {
                    _stackR![i] += r[i];
                    _stackG![i] += g[i];
                    _stackB![i] += b[i];
                    _countBuffer![i]++;
                }
            }
        } else {
            // Mono: accumulate the aligned CFA/mono frame. Skip zeros (the
            // resampler fills out-of-bounds with 0; don't drag the average).
            for (int i = 0; i < alignedData.Length && i < _stackBuffer!.Length; i++) {
                if (alignedData[i] > 0) {
                    _stackBuffer[i] += alignedData[i];
                    _countBuffer![i]++;
                }
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
        if (_countBuffer != null && _frameCount > 0) {
            // Cumulative SNR on the running-mean stack. Colour collapses the
            // three channels to Rec.601 luminance so the value stays comparable
            // to the mono path and the server's MetricsOnly number.
            int n = _width * _height;
            var snapshot = new ushort[n];
            if (_colorActive && _stackR != null && _stackG != null && _stackB != null) {
                for (int i = 0; i < n; i++) {
                    if (_countBuffer[i] > 0) {
                        double lum = (0.299 * _stackR[i] + 0.587 * _stackG[i] + 0.114 * _stackB[i]) / _countBuffer[i];
                        snapshot[i] = (ushort)Math.Clamp(lum, 0, 65535);
                    }
                }
                cumulativeSnr = ImageStatistics.ComputeBackgroundSnrFromData(snapshot);
            } else if (_stackBuffer != null) {
                for (int i = 0; i < n; i++) {
                    if (_countBuffer[i] > 0) {
                        snapshot[i] = (ushort)Math.Clamp(_stackBuffer[i] / _countBuffer[i], 0, 65535);
                    }
                }
                cumulativeSnr = ImageStatistics.ComputeBackgroundSnrFromData(snapshot);
            }
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
        // Colour session: re-mosaic the running-mean RGB planes back into a CFA
        // frame matching the locked Bayer pattern. The client's WebGL pipeline
        // debayers it for display exactly like an incoming raw frame, so all the
        // 16-bit stretch / white-balance / histogram machinery keeps working
        // unchanged — but the colour is now correct because the alignment warp
        // ran per-plane instead of smearing the raw CFA.
        if (_colorActive && _stackR != null && _stackG != null && _stackB != null && _countBuffer != null) {
            int n = _width * _height;
            var result = new int[n];
            int[] block = ColorBlock(_bayerPattern);
            for (int y = 0; y < _height; y++) {
                int rowBase = (y & 1) << 1;
                int row = y * _width;
                for (int x = 0; x < _width; x++) {
                    int i = row + x;
                    if (_countBuffer[i] <= 0) continue;
                    int colour = block[rowBase + (x & 1)];
                    float sum = colour == 0 ? _stackR[i] : colour == 1 ? _stackG[i] : _stackB[i];
                    result[i] = (int)Math.Clamp(sum / _countBuffer[i], 0, 65535);
                }
            }
            return result;
        }

        if (_stackBuffer == null) return [];
        // int[] not ushort[] because the JSExport marshaller doesn't
        // grok ushort[] directly; JS does (val & 0xFFFF) on the way out.
        var res = new int[_stackBuffer.Length];
        for (int i = 0; i < _stackBuffer.Length; i++) {
            if (_countBuffer![i] > 0) {
                res[i] = (int)Math.Clamp(_stackBuffer[i] / _countBuffer[i], 0, 65535);
            }
        }
        return res;
    }

    // --- Colour helpers (mirror BayerDebayer / LiveStackingService) -----------

    /// <summary>Map the wire Bayer code (the BayerPatternEnum int values:
    /// 0=None, 1=RGGB, 2=BGGR, 3=GBRG, 4=GRBG) to the enum. Unknown / None /
    /// Auto collapse to None, which selects the mono stacking path.</summary>
    private static BayerPatternEnum ResolvePattern(int code) => code switch {
        1 => BayerPatternEnum.RGGB,
        2 => BayerPatternEnum.BGGR,
        3 => BayerPatternEnum.GBRG,
        4 => BayerPatternEnum.GRBG,
        _ => BayerPatternEnum.None
    };

    private static bool IsColourPattern(BayerPatternEnum p) =>
        p == BayerPatternEnum.RGGB || p == BayerPatternEnum.BGGR
        || p == BayerPatternEnum.GBRG || p == BayerPatternEnum.GRBG;

    /// <summary>2x2 colour layout, index = (y&amp;1)*2 + (x&amp;1),
    /// value 0=R / 1=G / 2=B. Matches BayerDebayer.ColorBlockFor so the
    /// re-mosaic lands each channel on the phase the display debayer expects.</summary>
    private static int[] ColorBlock(BayerPatternEnum pattern) => pattern switch {
        BayerPatternEnum.RGGB => new[] { 0, 1, 1, 2 },
        BayerPatternEnum.GRBG => new[] { 1, 0, 2, 1 },
        BayerPatternEnum.GBRG => new[] { 1, 2, 0, 1 },
        BayerPatternEnum.BGGR => new[] { 2, 1, 1, 0 },
        _ => new[] { 1, 1, 1, 1 }
    };

    private static void EnsureScratch(ref ushort[]? buf, int length) {
        if (buf == null || buf.Length != length) buf = new ushort[length];
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