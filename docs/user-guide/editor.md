# Editor — Lightroom-style post-processing

The **EDITOR** tab is the last step of the Polaris workflow. After STUDIO
has produced a master integrated image (or you've imported a TIFF/PNG
from Siril/PixInsight), the editor gives you the same kind of slider-
driven, non-destructive tone work you'd otherwise do in Lightroom — but
inside the browser, against the file on the Polaris host.

## Workflow at a glance

1. **Open a source.** Three ways:
   - **EDITOR → Open file dropzone** — drag a `.fits` / `.xisf` / `.png`
     / `.jpg` / `.tif` from your desktop, or click to pick from disk.
   - **EDITOR → From server path** — paste an absolute path of a file
     already on the Polaris host (handy when you SSH'd a file in).
   - **STUDIO → select a single master → 🎨 Open in editor** in the
     selection bar, or **FILES → select one file → 🎨 Edit** in the
     toolbar.
2. **Drag sliders.** Light, Color, Effects and Detail sections each
   collapse and expand independently. Changes preview in ~80 ms
   (debounced) so dragging stays smooth even on the Raspberry Pi.
3. **Compare.** Hold **👁 Hold to compare** to flip back to the
   unedited reference. Release to return.
4. **💾 Save edits** writes a `{source}.edit.json` sidecar next to the
   source. Reopening that source later in the editor hydrates every
   slider to the saved state — fully non-destructive.
5. **↓ Export…** opens the export dialog (format, JPEG quality, resize).
   The exported file lands in `{sourceDir}/edited/{stem}__edited_{stamp}.{ext}`
   and the FILES index rescans automatically.

## Sections

### Light

| Slider | What it does |
|---|---|
| **Exposure** | Multiplies brightness in stops (-5 to +5). +1 ≈ 2× brighter. |
| **Contrast** | Lerps toward a smoothstep S-curve (endpoint-preserving). Positive pulls below-mid down + above-mid up; negative flattens. |
| **Highlights** | Pulls or pushes pixels above 0.5. Negative recovers blown highlights, positive emphasises them. |
| **Shadows** | Mirror of Highlights for the lower half. Positive lifts shadows; negative crushes them. |
| **Whites** | Specifically targets the top end (≥ 0.7) of the range. Lift to push details near white; cut to add headroom. |
| **Blacks** | Mirror of Whites at the bottom (≤ 0.3). |

### Color

| Slider | What it does |
|---|---|
| **Temp** (K) | White-balance temperature, 2000K (warm/red) ↔ 15000K (cool/blue). Defaults at 6500K (D65 neutral). |
| **Tint** | Magenta ↔ green balance. Negative = more magenta, positive = greener. |
| **Vibrance** | Per-pixel saturation boost scaled by `(1 - currentSaturation)` — protects already-saturated pixels so portraits / nebula cores don't go neon. |
| **Saturation** | Flat HSL scale of every pixel's S channel. -1 = full grayscale. |
| **Hue** | Rotates the whole colour wheel by -180° to +180°. |

### Effects

| Slider | What it does |
|---|---|
| **Texture** | Mid-radius (~3 px) unsharp-mask. Adds bite to fine structure without affecting overall tonal balance. |
| **Clarity** | Large-radius (auto: image-width / 80) USM on luminance only. Adds punch to broad structures; doesn't shift colour. |
| **Dehaze** | Per-channel auto-stretch toward black with mild saturation lift. Approximation of He et al. 2009 — fine for editor UX, won't match a research-grade dehaze algorithm. |
| **Vignette** | Radial multiply. Negative darkens corners; positive brightens. Feather controls falloff softness. |

### Detail

| Slider | What it does |
|---|---|
| **Sharpen** (amount/radius) | Unsharp-mask on luminance. Amount 0–1, radius 0.5–5 px. |
| **Noise reduce** | Conservative 3×3 median on luminance, blended back at the slider's strength. For heavy NR run GraXpert Denoise on the FITS first — this is just polish. |

## Pipeline order

Each step short-circuits when its section is at defaults, so a slider
you haven't touched costs zero work. The fixed order is:

1. White balance → 2. Exposure → 3. Contrast → 4. Highlights →
5. Shadows → 6. Whites/Blacks → 7. Tone curves → 8. Vibrance/Saturation
→ 9. Hue → 10. Clarity → 11. Dehaze → 12. Texture → 13. Sharpen →
14. Noise reduce → 15. Vignette → 16. Crop + Resize (export only).

This is deterministic so the server-side and (future) WASM client-side
implementations produce byte-identical output for the same edits.

## Sidecar (`.edit.json`)

When you click **Save edits**, the editor writes a small JSON file next
to the source:

```jsonc
{
  "version": 1,
  "source": "M31_master.fits",
  "savedAt": "2026-05-24T22:30:00Z",
  "edits": {
    "light": { "exposure": 0.2, "contrast": 0.15, "highlights": -0.3 },
    "color": { "vibrance": 0.4 },
    "effects": { "clarity": 0.2, "vignetteAmount": -0.4 }
  }
}
```

Sections you didn't touch are omitted entirely. If the source's
directory isn't writable (read-only mount, SMB without write access),
the sidecar falls back to
`%LOCALAPPDATA%\Polaris\sidecars\{md5-of-path}.edit.json` on the Polaris
host.

The exported file (in `edited/`) doesn't carry a sidecar — re-opening
the export in the editor will start from defaults. The sidecar always
lives with the **source**, not the export.

## Compute model

Today every edit runs on the Polaris **server**: each slider tick POSTs
to `/api/editor/preview`, the server applies the pipeline + encodes a
JPEG, the browser swaps the `<img>`'s blob URL. The decoded source
stays in memory in a session keyed by Guid (auto-evicted after 30 min
idle), so only the first `/preview` call after `/load` pays for the FITS
decode + AutoStretch.

A future build (ED-6) will move the pipeline into the WASM bundle when
the browser is capable, eliminating the network round-trip and dropping
slider latency to roughly the time it takes the canvas to repaint. The
math is identical, so output stays byte-for-byte the same; the toggle
will live in Settings.

## Known limitations

- **XISF** is supported as source via FITS-like header decode — but
  PixInsight projects that pack multiple frames into one XISF aren't
  parsed (only the primary image). Export it as TIFF/PNG out of
  PixInsight if in doubt.
- **CR2 / NEF / ARW (raw)** aren't supported as editor input; the
  decoder is FITS + Skia (PNG/JPG/TIFF). Export the demosaiced TIFF
  first.
- **Local adjustments** (brush, gradient, radial) are out of scope for
  this version. Crop and resize live in the export dialog; everything
  else is global.
- The editor opens **one frame per session**. Batch editing across
  many files is best done by saving sliders to a sidecar on one frame,
  then copying that `.edit.json` next to each sibling file (manual
  step today).

## API reference

All endpoints live under `/api/editor`:

| Method + Path | Body | Returns |
|---|---|---|
| `POST /load` | `{ path }` | `{ sessionId, sourcePath, width, height, channels, edits }` (edits null when no sidecar) |
| `POST /preview` | `{ sessionId, edits, maxDim, quality }` | JPEG bytes |
| `POST /histogram` | `{ sessionId, edits }` | `int[256]` (mono) or `int[768]` (RGB) |
| `POST /export` | `{ sessionId, edits, format, quality, targetWidth, targetHeight, outputPath }` | `{ path }` |
| `GET /sidecar?path=…` | — | `{ exists, edits }` |
| `PUT /sidecar` | `{ path, edits }` | `{ sidecarPath }` |
| `POST /upload` | multipart `file` | `{ path }` |
| `POST /release` | `{ sessionId }` | `{ released: true }` |
| `GET /sessions` | — | array of active session info (debug) |

Sessions auto-evict after 30 min of inactivity. `POST /release` frees
one early.
