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

### Drizzle (super-resolution)

After picking the integration method, Integrate asks for a **drizzle scale**
and shows a **recommendation** computed from your data:

- **1x** - native size. The standard resample + combine (with sigma-clip
  rejection). Right for well/over-sampled data.
- **2x / 3x** - drizzle (Fruchter & Hook variable-pixel linear reconstruction).
  Each pixel is forward-projected as a shrunk "drop" onto a finer grid; with
  sub-pixel **dithered** subs this recovers resolution lost to **undersampling**
  and reduces aliasing.

The recommendation samples a few frames' star **FWHM in pixels**: under ~2 px
the data is undersampled and 2x is suggested (with a note if you have few
subs); at ~2.6 px+ it's well sampled and 1x is suggested (drizzle >1x there
just amplifies noise and enlarges the file). Same algorithm as Siril/PixInsight
drizzle - the only difference between those tools is the default scale (Siril
defaults to 1x, PixInsight to 2x); here you pick with the recommendation in
front of you.

Requirements + notes:

- Drizzle **needs dithered subs** (the whole point is sub-pixel diversity); an
  un-dithered set at 2x/3x leaves coverage holes and grid patterns. The output
  header records `DRZEMPTY` (percent of output pixels no drop reached) as a
  coverage sanity check.
- Drizzle keeps the accumulator in RAM (its inherent cost): a 2x integration is
  ~4x the output pixels, 3x is ~9x. A pre-flight RAM guard refuses up front if
  it won't fit; on a small SBC prefer 1x/2x.
- Drizzle mode is a weighted mean (no per-pixel sigma rejection); for
  cosmic-ray / trail rejection use 1x. Output: `master_light_..._drz{N}x_...`
  with `DRIZZLE`, `DRZSCALE`, `DRZFRAC`, `DRZEMPTY` headers.

## Color calibration (Siril-style)

After channel combine produces an RGB master, click **🎯 Color
calibration** in the selection bar (single-frame selection) to
neutralise colour cast and (optionally) fit per-channel gains from
real catalog star photometry.

Three modes in the modal:

- **BG neutralize**, auto or patch-based background sampling +
  per-channel offset subtraction. Output:
  `{stem}_bgneu.fits`.
- **Manual**, BG neutralize + a white-reference patch picker.
  Output: `{stem}_ccal.fits`.
- **PCC (Photometric Color Calibration)**, plate-solve-driven
  catalog lookup (bundled APASS DR10) + per-star B-V → expected
  RGB ratios + median gain fit. Requires WCS in the source FITS +
  the catalog populated via `scripts/download-apass.py`. Output:
  `{stem}_pcc.fits` plus `CCAL_NSTAR` matched-star count.

All output FITS carry the recipe in custom headers (`CCAL_MOD`,
`CCAL_OFR/G/B`, `CCAL_GNR/G/B`, `CCAL_SRC`) for audit in
PixInsight's FITS Header view.

Full walkthrough in [Color calibration](color-calibration.md).

## Channel combine (RGB / LRGB / narrowband / continuum)

For mono shooters: after per-filter integration leaves you with one
master per filter, add two or more masters to the **Lights** slot of
the STUDIO **Stacking** sub-tab, then click **Combine channels** under
**3 - Combine & Colour**. A first prompt picks the mode; the following
prompts collect the role of each file:

- **RGB**, pack 3 mono masters (R/G/B) into a single RGB FITS.
- **LRGB**, RGB plus a luminance master, combined via Lab swap
  (default, preserves chrominance) or Ratio (classical, faster).
- **Narrowband palette**, map Ha / OIII / SII masters to colour by
  palette: **SHO** (SII=R, Ha=G, OIII=B, the "Hubble" palette),
  **HSO**, **HOS**, or **HOO** bicolor (Ha=R, OIII=G+B). Per-channel
  normalize matches the three backgrounds so no single filter dominates
  the colour.
- **Continuum subtraction**, isolate the emission signal in a
  narrowband master by removing a scaled broadband master:
  `NB' = max(0, NB - k*Continuum)`. Assign the **NB** and **C** roles;
  the scale `k` is auto-estimated from the bright star pixels (median
  NB/Continuum where the signal is pure continuum) or entered manually
  in `[0, 4]`. Stars largely cancel while the nebulosity remains.

For richer per-pixel work (synthetic luminance, custom palettes) the
underlying service also has a PixelMath mode; the palette + continuum
modes above cover the common narrowband recipes without writing an
expression.

Cross-channel star registration is on by default (the per-filter
masters come out of `BatchStackingService` aligned to their own
reference frame, not to each other, so without registration you
get coloured fringes on every star). Per-channel normalize is on by
default for RGB / LRGB / narrowband, and **off** for continuum
subtraction so the auto `k` estimate isn't skewed by a pre-scaled
background.

Output: `integrated/{Target}/composed/{rgb|lrgb|nb|cs|pm}_{Target}_{stamp}.fits`
with `CHCOMBINE`, `REGISTER`, `REGREF`, `REG_<channel>`, `NORMLIZE`
custom headers describing the recipe.

The narrowband palette + continuum math is implemented from scratch
(`NarrowbandCombine` / `ContinuumSubtraction`), inspired by the
narrowband tools in SASpro / PixInsight.

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

## Auto Workflow

The third STUDIO sub-tab (**Files / Stacking / Auto Workflow**) is a
**saveable, linear post-processing pipeline** applied to a source image and
re-runnable as a **batch** over many files. Build it like the advanced
sequencer: pick steps from the palette on the left, they append to the
sequence in the middle, and selecting a step edits its parameters on the
right. Each step's output feeds the next; a per-step preview shows the
intermediate result. Intermediates are deleted by default (toggle "keep
intermediates").

**Getting started fast:**

- A built-in **"Standard"** workflow is seeded on first run (auto-crop → BGE →
  decon → denoise → auto-stretch → the Lightroom-style light/colour/detail
  adjustments → JPG 90). Load it from the **Load** list and tweak. Delete it
  and it stays deleted.
- Three **combine presets** are also seeded: **Mono LRGB**, **Mono SHO**, and
  **OSC dual-band SHO**. Each opens with a Combine source-stage (see below) and
  then runs the same colour post pipeline. Load one, assign a file to each role,
  and Run.
- **Recommended preset** button builds a safe default (auto-crop → BGE →
  denoise → detail → auto-stretch → contrast → saturation → export).
- **Auto all** enables the "auto" option on every capable step.

**Combine source-stage (mono LRGB / mono SHO / OSC dual-band SHO):**

At the top of the builder, the **Combine source** dropdown lets a workflow
*produce* its source by composing several per-filter masters instead of taking
a file from the Sources list:

- **Mono LRGB** - assign R / G / B / L masters; composed via Lab luminance.
- **Mono SHO** - assign Ha / OIII / SII masters; composed as the SHO palette.
- **OSC dual-band SHO** - assign the two debayered RGB masters (Ha+OIII and
  SII+OIII filters). Ha/SII are extracted from red, OIII from green+blue (both
  masters' OIII are averaged for SNR), the two are star-registered, and packed
  as SHO.

Assign each role with **Set** (uses the FITS currently selected in the Files
tab). When a combine mode is active the plain Sources list is hidden: the
composed image becomes the single source the linear pipeline runs on. Combine
runs once at the start (not per file), reusing the same cross-channel star
registration as the manual [Channel combine](#channel-combine-rgb--lrgb--narrowband--continuum).
The combine choice + role map is saved inside the workflow, so a saved LRGB/SHO
workflow reloads ready to go (roles cleared for you to reassign).

**Tools (single-image, server-side FITS→FITS):**

- **Auto Crop (stacking borders)** - detects the largest fully-covered inner
  rectangle and removes the black/ragged registration borders that stacking
  leaves on slightly-misaligned subs. No ROI to draw.
- **Crop** - manual rectangular crop (fractions).
- **SCNR** - remove the residual green cast (average/maximum-neutral,
  masked variants). Ported from Siril.
- **Stretch (GHS / asinh)** - Generalized Hyperbolic / arc-sinh non-linear
  stretch; "auto" picks the amount from the image median. Ported from Siril.
- **Cosmetic (hot/cold pixels)** - sigma-based hot/cold pixel removal (CFA
  option for undebayered OSC). Ported from Siril.
- **Star Reduction** - morphological shrink/dim of stars (detected-star mask +
  grayscale erosion), with core protection.
- **Wavelet Sharpen** - multiscale (à-trous) detail boost + optional
  denoise, on luminance so colour is preserved. Subsumes frequency
  separation. SASpro/PixInsight-inspired.
- **Multiscale HDR (recover cores)** - compress the large-scale luminance so
  blown galaxy/nebula/star cores come down toward the background while fine
  detail is kept.
- **CLAHE (local contrast)** - contrast-limited adaptive histogram
  equalization. Best placed **after** the stretch.
- **Highlight Recovery** - soft-knee compression of blown highlights above a
  knee point.

**AI Tools (browser ONNX, need the models installed - see
[ONNX inference](onnx-inference.md)):** Background Extraction, Denoise,
Detail/Sharpen, Halo Removal, Upscale, Star Removal.

**Decon / stars:** Richardson-Lucy deconvolution; Blend Stars Back (needs a
prior Star Removal step).

**Editor adjustments:** every Lightroom-style slider (exposure, contrast,
black/white points, temperature, tint, vibrance, saturation, texture,
clarity, dehaze, noise reduction, sharpen, vignette) is a step; all enabled
edit-items are collected into one editor pass at the final **Export bitmap**
step (PNG/JPG/TIF).

**Save / Load / batch:** name and **Save** the workflow, add multiple source
files from the Files-tab selection ("+ Add selected"), and **Run** to apply
the same pipeline to every source. From the editor you can also press
**→ Workflow** to send your current edits straight into a new workflow to
name, save, and batch-apply.

Licensing note: the classical filters (SCNR, GHS/asinh, cosmetic, star
reduction) and the multiscale/tonal ops (wavelets, HDR, CLAHE, highlight
recovery) are re-implemented from scratch from published algorithms (Siril /
Starck à-trous / HDRMT / Zuiderveld CLAHE); no third-party code is bundled.

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
