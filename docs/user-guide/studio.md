# STUDIO tab (post-processing)

Polaris's built-in post-processing pipeline. Browse captured frames,
generate masters, calibrate lights, batch-stack, debayer, extract
gradient, sharpen, denoise.

For most things STUDIO replaces a separate PixInsight / Siril workflow.
For wavelet sharpening or specific advanced ops, STUDIO can hand off
to Siril or GraXpert (see [External tools](#external-tools)).

## Frame browser

The main view: SQLite-indexed list of every FITS/XISF under
`{ImageOutputDir}`. Filters by:

- **Type** (Light / Dark / Bias / Flat / Master)
- **Filter** (R / G / B / L / Ha / OIII / ...)
- **Target**
- **Date range**

Grid view shows thumbnails (256px, cached at
`{AppData}/Polaris/studio/thumbs/`). Click → opens single-frame viewer
with manual stretch sliders + multi-format export.

**Rescan** button picks up new files added by the sequence engine or
copied in via FILES.

## Single-frame viewer

OpenSeadragon pan/zoom + side panels:

- **Stretch sliders**, Black point / Mid-tone / White point. Live
  re-render via debounced server-side stretch.
- **Auto Stretch**, MTF-based defaults
- **Histogram**, 256-bin overlay
- **Stats**, Mean / Median / MAD / StdDev / Star count / HFR
- **Star annotations**, circle overlay toggle
- **Export**, JPG / PNG / TIFF 16-bit; lands in
  `{ImageOutputDir}/{Rig}/processed/{Target}/`

## Master generation

Select N frames (Shift-click range, Ctrl-click toggle) → **Create
master** button:

- **Type auto-detection** from FITS `IMAGETYP` header, Bias / Dark /
  Flat
- **Integration method**: Median (robust) / Mean / Sigma-clipped mean
  (3σ, 2 iterations default)
- **Progress bar** + per-frame tick

Output: `calibration/masters/master_{type}_{key}.fits` with FITS
header annotations (`NSUBS=N`, `INTMETH=...`). Auto-indexed in the
library.

## Light calibration

Select calibrated frames + **Calibrate** button:

- Picks the matching **master dark** + **master flat** + **master bias**
  by (Exposure, Gain, Filter), auto-match with override dropdowns
- Pixel math: `(light − dark − bias) / (flat − flatDark) × mean(flat
  − flatDark)`
- Output: `calibrated/{Target}/{Filter}/cal_{originalName}.fits`
- Frames appear in the library with badge "calibrated"

## Batch alignment + integration

Select calibrated lights → **Integrate** button:

- **Alignment**, StarDetector + StarMatcher (same engine as live
  stack) computes per-frame affine transforms relative to the best-HFR
  reference frame
- **Integration method**, Average / Median / Sigma-clipped average /
  Winsorized
- **Normalization**, None / Scale to mean / Multiplicative
- **Outlier rejection**, Cosmetic correction + sigma rejection
- **Weighting**, Optional per-frame HFR weight

Output: `integrated/{Target}/{Filter}/master_{Target}_{Filter}_{N}x{Exp}s.fits`
with `NCOMBINE`, `EXPTOTAL`, `INTMETH`, `REJECT` headers.

## Channel combine (mono LRGB / RGB / PixelMath)

For mono shooters: after per-filter integration leaves you with one
master per filter, select two or more masters and click **Combine**
in the selection bar. The modal has three tabs:

- **RGB**, pack 3 mono masters (R/G/B) into a single RGB FITS.
- **LRGB**, RGB plus a luminance master, combined via Lab swap
  (default, preserves chrominance) or Ratio (classical, faster).
- **PixelMath**, evaluate per-pixel expressions over named channels.
  Useful for narrowband palettes (HOO, SHO) and synthetic luminance.
  Supports +, -, *, /, **, parens, plus min/max/abs/pow/sqrt/exp/
  log/clamp.

Cross-channel star registration is on by default (the per-filter
masters come out of `BatchStackingService` aligned to their own
reference frame, not to each other, so without registration you
get coloured fringes on every star). Per-channel normalize is also
on by default.

Output: `integrated/{Target}/composed/{rgb|lrgb|pm}_{Target}_{stamp}.fits`
with `CHCOMBINE`, `REGISTER`, `REGREF`, `REG_<channel>`, `NORMLIZE`
custom headers describing the recipe.

Full walkthrough in [Mono LRGB workflow](lrgb-mono-workflow.md).

## Debayer + background extraction

Click a frame in the viewer → **Debayer** button (only enabled when
BayerPattern is detected). Splits into R/G/B planes or returns RGB
output. White balance: Gray World (default) or user-multipliers.

**Remove gradient** invokes GraXpert (if installed) for AI-based
background extraction. Output sibling file `_bge.fits`.

## Post-processing toolbox

Within the viewer, optional pipeline steps (drag-drop reorder via
Sortable.js):

- **Noise reduction**, Gaussian blur (configurable radius)
- **Sharpening**, Unsharp mask (amount + radius)
- **Saturation**, RGB → HSV → multiply S → back

Apply → re-render preview. **Save processed** exports the final result.

## External tools

When detected on the host, STUDIO can hand off to:

- **Siril**, "Stack with Siril" toolbar button. Modal lets you pick a
  bundled script (OSC_Preprocessing, Mono_Preprocessing, Extract
  HaOIII, ...) or one from your `~/.siril/scripts` folder. Optional
  "inject GraXpert BGE between calibration and stack" toggle for the
  combined pipeline.
- **GraXpert**, dropdown menu "Process with GraXpert":
  - 🌅 Remove gradient (BGE)
  - ✨ Deconvolution (v3.0+)
  - 🔇 Denoise (v3.0+)

  Each opens a modal with the op's specific sliders. Output lands in
  `{rig}/bge/{target}/`, `{rig}/decon/{target}/`, or
  `{rig}/denoise/{target}/`.

Detection happens at startup; see Settings → External tools to verify
or override paths.

## Common pitfalls

**Rescan misses new files**, frame writer is still flushing. Wait a
few seconds + retry.

**Calibration leaves residual hot pixels**, master dark exposure /
gain doesn't match the lights. Bump the auto-match tolerance or pick
the master manually.

**Integration takes forever**, try smaller batches (50 frames) on
RPi 4. SBC's memory ceiling caps the working set; very large stacks
may swap.

## See also

- [External tools setup](../siril-setup.md), [GraXpert setup](../graxpert-setup.md)
- [Glossary → Bias / Dark / Flat / Stretch](GLOSSARY.md#b)
