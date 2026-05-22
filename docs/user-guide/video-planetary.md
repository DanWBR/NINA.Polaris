# VIDEO tab (planetary capture + lucky imaging)

Two-pane workflow for planetary imaging: **Capture** continuous video
to SER files, **Process** them with the built-in lucky-imaging stack
pipeline.

## Capture sub-tab

- **Live preview canvas** — fills available area
- **Exposure** (s) — typically 5-50ms for planetary
- **Gain** — depends on camera (planetary loves high gain)
- **Bin** — 1×1 usually; 2×2 if you're undersampled
- **Target name** — pasta name (Jupiter / Saturn / Moon / ...)
- **Max duration** (s) — auto-stop after N seconds (0 = no cap)
- **White balance R / B sliders** — only visible for OSC color cameras
  that expose INDI's `WB_R + WB_B` (ZWO/QHY). Mono cameras hide the
  row.

### Buttons

- **🎥 Start Stream** — opens camera stream (native CCD_VIDEO_STREAM
  if supported, else server-loop fallback). Label shows live fps.
- **⏺ Record** — only enabled when streaming. Begins writing every
  frame to a SER file at
  `{ImageOutputDir}/{Rig}/planetary/{target}/{ISO-timestamp}.ser`.
  Counter shows frames + bytes + dropped-frame warning if the writer
  can't keep up.

### Common Capture settings (saved per rig)

For Jupiter / Saturn on an SCT 8":
- Exposure 8-12ms
- Gain ~350 (ZWO IMX462 / IMX664)
- Max duration 120s (good seeing) or 90s (rotation cap)

For the Moon at high mag:
- Exposure 1-2ms
- Gain low (0-100)
- Max duration 30-60s per region; mosaic later

## Process sub-tab

Lucky imaging stack pipeline. Drives the `PlanetaryStackerService`.

1. **SER file** dropdown — populated from `{ImageOutputDir}/planetary/`
   recursively. ⟳ Refresh button.
2. **Keep top X%** — quality cutoff (default 50%). Picks the sharpest
   X% of frames for stacking. Smaller = sharper but noisier.
3. **Output name** — base filename for the stacked result
4. **▶ Stack** — kicks the job

### 7-phase pipeline

Status bar shows phase live:

1. **Reading** — opens the SER, lists frames
2. **Analyzing** — Laplacian variance per frame (parallelizable;
   typical RPi 4: 1000 frames × 800×600 × 16-bit ≈ 30s)
3. **Ranking** — sort by quality desc, take top KeepPercent
4. **Aligning** — brightest-region centroid + parabolic sub-pixel
   refinement per kept frame
5. **Stacking** — mean stack with per-pixel count, output uint16
6. **Writing** — FITS to `planetary/{target}/stacked/`
7. **Ok / Fail**

**Abort** button cancels mid-job.

## Quality metric (Laplacian variance)

Variance of the 3×3 Laplacian filter applied to the centred ROI of
each frame. Standard sharpness metric (Pertuz et al. 2013).

Higher = sharper. The cutoff slider lets you discard atmospheric-blur
frames; pick aggressive (top 20%) for best detail, or lenient (top
80%) for low-noise but slightly softer output.

## Alignment

Brightest-pixel centroid + parabolic refinement on a 5×5 neighborhood.
Works for bright targets (Moon, Jupiter, Mars, Saturn body).

**Limitation**: Saturn rings or other extended sources don't have a
single brightness peak — alignment shifts can drift. Workaround: stack
with smaller batches per processing pass; future versions will add
thresholded centroid + better alignment for extended objects.

## Output

`{ImageOutputDir}/{Rig}/planetary/{target}/stacked/{outputName}_{ts}.fits`

16-bit mono FITS. Open in PixInsight / Siril / Photoshop. Apply
wavelet sharpening + saturation in your favorite tool.

## Common pitfalls

**fps caps at ~5 even though camera supports native stream** — check
that `CCD_VIDEO_STREAM` is actually exposed by your driver. Some INDI
drivers default to `OFF` and need to be enabled in indiserver
parameters.

**Dropped frames warning** — writer can't keep up with stream cadence.
Reduce ROI (smaller frames write faster), lower binning, or lower
target fps via the driver's `STREAMING_DELAY` property.

**Stack output is dark** — frames were mostly cropped to a small
brightness region. Try wider ROI or increase Keep% to include more
frames.

**Storage explodes** — 60s @ 30fps × 800×600 × uint16 ≈ 1.7 GB.
Watch your disk; planetary captures fill SD cards fast.

## See also

- [Glossary → SER / Lucky imaging / Laplacian variance](GLOSSARY.md#l)
