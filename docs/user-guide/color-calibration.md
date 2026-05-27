# Color calibration (Siril-style)

The STUDIO **Color calibration** button gives you three calibrators
that map to the three Siril tools astrophotographers reach for after
stacking + before the editor:

| Mode | What it does | When you use it |
|---|---|---|
| **BG neutralize** | Subtract per-channel offsets so the background is neutral grey. | Quick first pass on any colour-cast master. No setup. |
| **Manual** | BG neutralize + a white-reference patch you pick. | When the BG step alone leaves a tint and you have a known-white target in frame (G2V star, galaxy core). |
| **PCC** | Photometric Color Calibration via bundled APASS DR10 catalog. | The gold standard. Star colours match catalog B-V values. Requires plate-solved source + the catalog populated. |

All three are non-destructive: each writes a sibling FITS next to
the source (`{stem}_bgneu.fits` / `{stem}_ccal.fits` / `{stem}_pcc.fits`)
and indexes it in the frame library. The originals stay untouched
so you can experiment.

## When to run color calibration

Slot it between **channel combine** and **GraXpert AI cleanup** in
the mono workflow:

```
per-filter masters → Combine → Color calibration → BGE → Denoise → Decon → Editor → Export
                              ^^^^^^^^^^^^^^^^^^
                              this page
```

For OSC shooters: drop in between **debayer** and the AI cleanup,
or skip it entirely if your OSC pipeline already produces a neutral
result.

## Workflow

1. Select **one** RGB FITS master in STUDIO (the Combine button's
   output, typically `rgb_*.fits` or `lrgb_*.fits`).
2. Click **🎯 Color calibration** in the selection bar. The modal
   opens.
3. Pick a tab (defaults to BG):
   - **BG neutralize**: leave on Auto unless you have a specific
     dark patch in mind, then click **▶ Calibrate**.
   - **Manual**: keep BG on Auto, type pixel coordinates for the
     white patch (e.g. a small rect around a known G2V star), then
     click **▶ Calibrate**.
   - **PCC**: check the catalog badge at the top of the tab. If it
     shows ✓ APASS DR10, click **▶ Calibrate**. Otherwise see the
     "Setup" section below.
4. Wait. BG/Manual take a few seconds; PCC takes 10-30 s depending
   on input size + matched star count.
5. The toast on completion shows the output path, per-channel
   gains, and (for PCC) matched-star count. Open the sibling FITS
   in the STUDIO viewer to compare against the source.

## Mode details

### BG neutralize

The simplest case: per-channel offsets are subtracted so the
chosen background region becomes neutral grey at the dimmest
channel's level. Overall image brightness is preserved.

Sample modes:
- **Auto** (recommended): the service samples the lowest-luminance
  5% of pixels across the entire frame. Picks the darkest
  background portion automatically, works on any frame with empty
  sky.
- **Patch**: type `x, y, w, h` in pixel coordinates of a rectangle
  covering a dark empty-sky region. Use this when the auto-sample
  picks up unwanted content (very dim nebula, vignette corner,
  amp glow).

Output: `{stem}_bgneu.fits` with `CCAL_MODE=bg` and the three
per-channel offset values in the FITS headers.

### Manual

BG neutralization plus a white-reference rescale. After the BG
step, the service computes per-channel gains that bring the white
patch to neutral. The BG step uses zero-background offsets (each
channel's full median subtracted) in this mode so the subsequent
gain does not pull background away from neutrality.

Good white-reference targets:
- G2V star (Sun-like, B-V ≈ 0.65). Visible all over the sky.
- Galaxy core for spiral galaxies (M31, M81). Usually neutral
  except for very dust-heavy cores.
- Neutral region of a nebula (where ionization is balanced).

Output: `{stem}_ccal.fits` with `CCAL_MODE=manual` plus offsets +
gains.

### PCC (Photometric Color Calibration)

The science-grade tool, modelled after Siril's PCC. Requires a
plate-solved source + the bundled APASS DR10 catalog.

Pipeline:
1. Read the WCS coords from the source FITS headers (must be
   plate-solved, see Setup).
2. Detect stars on the green plane (highest SNR).
3. Measure per-channel flux for each detected star via aperture
   photometry (`StarPhotometer`, 2 × HFR aperture with annulus
   background subtraction).
4. Query APASS for catalog stars in the field of view.
5. Match catalog (RA, Dec) to detected (X, Y) within 3 pixels.
   Drop saturated stars and catalog entries without B-V data.
6. For each matched star, compute expected R/G and B/G ratios from
   the B-V index (log-linear fit anchored at G2V). Divide observed
   by expected, take the median across all matched stars to get
   per-channel gains.
7. Apply BG neutralization (zero-background) so the output is both
   star-colour-calibrated and background-neutral.

Output: `{stem}_pcc.fits` with `CCAL_MODE=pcc`, the per-channel
gains, and `MatchedStars` reported in the toast. Requires at least
5 matched stars; PCC fails loudly with an actionable error if
fewer.

## Setup (PCC only)

PCC needs two ingredients the other modes do not:

### 1. Plate-solved source

The source FITS must have WCS headers (CRVAL/CRPIX/CD matrix).
ASTAP writes these automatically when you run STUDIO → Solve;
they survive integration via `BatchStackingService` and channel
combine via `ChannelCombineService` because Polaris carries WCS
through the reference frame.

If PCC fails with "source FITS has no WCS", run STUDIO → Solve on
the master first, then retry.

### 2. APASS catalog

Bundled APASS DR10 lives at `wwwroot/catalogs/apass/apass.db`
(~80 MB). The file is **not committed to the repo** because of
size + the catalog's CC-BY attribution requirement. Populate it
once on the deployment host:

```bash
python scripts/download-apass.py
```

That fetches the AAVSO APASS DR10 ASCII data, filters to stars
with V mag ≤ 13, and builds the SQLite database with an R*tree
spatial index. ~10 min wall-clock on a Pi 5, less on a desktop.

The PCC tab's catalog badge tells you whether the file is in
place. If you see "⚠ not installed", run the script and reload
the modal.

### Alternative catalogs

The `ApassCatalog` service reads a generic schema (`stars` table
with `ra`, `dec`, `mag_v`, `mag_b`, `b_v`, `source` + a `stars_idx`
R*tree). If you have a Gaia DR3 or Tycho-2 subset in the same
schema, drop it in at the bundled path as `apass.db` and Polaris
uses it transparently.

## Worked example: M81 LRGB end-to-end with PCC

Pi 5 host, ZWO ASI2600MM Pro, ASKAR FRA600, LRGB filter set,
APASS catalog already populated.

| Step | Time | Output |
|---|---|---|
| Capture: 30L + 15R + 15G + 15B at 180s each (5 hours unattended) | 5 h | 75 raw lights |
| STUDIO: calibrate + integrate per filter | 10 min | 4 per-filter masters |
| STUDIO: Combine (LRGB tab, Lab swap) | 25 s | `lrgb_M81_*.fits` |
| STUDIO: Solve (if not solved already) | 5 s | WCS in FITS headers |
| **STUDIO: Color calibration → PCC** | **20 s** | **`lrgb_M81_*_pcc.fits`** |
| FILES: GraXpert BGE | 30 s | `_bge.fits` |
| FILES: GraXpert Denoise | 90 s | `_denoise.fits` |
| Editor: tone work | 8 min | sidecar saved |
| Export JPG quality 92 | 3 s | final image |

The PCC step is fast (the heavy work is the cone-search + match,
which is fast on SQLite R*tree) and produces an output where star
colours match what a published catalog predicts. No more "is this
green cast really there or just my filter mix"; PCC fits it from
real photometry data.

## Output FITS headers (recipe audit)

Every output FITS carries the recipe in its headers so PixInsight's
**FITS Header** view (or any FITS inspector) can show what
happened:

```
CCAL_MOD = 'bg' / 'manual' / 'pcc'
CCAL_OFR = 32.5     (per-channel offset applied to R)
CCAL_OFG = 0.0
CCAL_OFB = 18.1
CCAL_GNR = 1.124    (per-channel gain applied to R)
CCAL_GNG = 1.000    (anchor channel, always 1)
CCAL_GNB = 0.953
CCAL_SRC = 'lrgb_M81_2026.fits'   (source file name)
```

Useful for "why does this look weird" debugging months later.

## Common pitfalls

- **PCC says "only 3 matched stars"**: either the field has very
  few catalog stars (planetary close-ups, dark molecular clouds)
  or plate-solve accuracy is too low for the 3-pixel match radius.
  Re-solve with a better hint, or use Manual mode.
- **PCC output looks tinted**: the linear B-V → RGB fit is
  approximate (~5% accuracy). For exact colour fidelity use Manual
  mode with a measured G2V star reference, or wait for a future
  SPCC-style upgrade with full spectral integration.
- **BG output still has a cast**: the auto-sample picked up faint
  nebulosity. Switch to Patch mode and pick a darker corner.
- **Manual output has neutral BG but tinted white patch**: the
  patch coordinates landed on something that isn't actually neutral
  (a coloured star, a nebula edge). Pick a different patch and
  re-run.
- **Catalog file is missing after deploy**: the .db is gitignored.
  Run `python scripts/download-apass.py` on the deploy host. The
  publish output picks it up via wwwroot/** so a re-deploy is not
  needed.

## See also

- [Mono workflow](lrgb-mono-workflow.md), where color calibration
  fits in the full mono pipeline.
- [End-to-end workflow](end-to-end-workflow.md), OSC + mono
  pipelines from capture to export.
- [STUDIO guide](studio.md), the full STUDIO toolkit reference.

## Attribution

APASS data is provided by the AAVSO under a CC-BY 4.0 license. If
you publish images calibrated with PCC, please credit:

> Henden, A. A., Levine, S., Terrell, D., Welch, D. L., Munari, U.,
> & Kloppenborg, B. K. (2018). "The APASS Data Release 10." VizieR
> On-line Data Catalog: II/336.
