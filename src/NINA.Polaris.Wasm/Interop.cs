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
[JsonSerializable(typeof(MaskParams))]
[JsonSerializable(typeof(MaskKind))]
[JsonSerializable(typeof(IReadOnlyList<CurvePoint>))]
[JsonSerializable(typeof(List<CurvePoint>))]
internal partial class EditorJsonContext : JsonSerializerContext { }

/// <summary>
/// JS-callable surface for the browser-side image editor.
///
/// Uses the SAME EditPipeline the server runs (referenced from
/// NINA.Image.Portable) so browser-side output matches server-side
/// byte-for-byte on the same inputs.
///
/// This class also used to host a live-stack accumulator, so the page
/// could integrate frames itself. Integration is server-side now, the
/// only kind there is, and the WASM module is purely a renderer.
/// </summary>
public static partial class Interop {

    /// <summary>Smoke-test entry point. The 'nina-wasm-ready' handler in
    /// app.js calls this to confirm the bundle loaded and the [JSExport]
    /// marshalling works. Bump the suffix on protocol-breaking changes so
    /// a stale cached bundle is detectable.</summary>
    [JSExport]
    public static string Ping() => "pong v0.7 (ED-6 editor + JsonContext, stacker removed)";

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