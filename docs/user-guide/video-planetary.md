# VIDEO tab (planetary capture + lucky imaging)

Two-pane workflow for planetary imaging: **Capture** continuous video
to SER files, **Process** them with the built-in lucky-imaging stack
pipeline.

## Capture sub-tab

- **Live preview canvas**, fills available area
- **Exposure** (s), typically 5-50ms for planetary
- **Gain**, depends on camera (planetary loves high gain)
- **Bin**, 1×1 usually; 2×2 if you're undersampled
- **Target name**, pasta name (Jupiter / Saturn / Moon / ...)
- **Max duration** (s), auto-stop after N seconds (0 = no cap)
- **White balance R / B sliders**, only visible for OSC color cameras
  that expose INDI's `WB_R + WB_B` (ZWO/QHY). Mono cameras hide the
  row.

### Buttons

- **🎥 Start Stream**, opens camera stream (native CCD_VIDEO_STREAM
  if supported, else server-loop fallback). Label shows live fps.
- **⏺ Record**, only enabled when streaming. Begins writing every
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

Lucky imaging stack, the same two choices as ASIVideoStack.

1. **SER file**: recordings under `{ImageOutputDir}/planetary/`, or any
   folder you put in *Scan folder*. Picking a clip shows one frame of it.
2. **Stack type**
   - **Planet**: a disc surrounded by sky (Jupiter, Saturn, Mars, the whole
     Moon in a wide field). Frames are registered on the disc's centroid.
   - **Moon and Sun surface**: a close-up with surface running off the
     edges. A green box appears on the preview: drag it onto a feature
     with clear detail (a crater, the terminator) and every frame is
     registered on that box by phase correlation. The box is 512 px on
     the frame, or the largest power of two that fits a smaller clip.
   Pick the wrong one and the stack comes out smeared along the drift
   direction: the centroid of a frame full of surface never moves, so
   nothing gets aligned.
3. **Stack percent**: the sharpest X% of frames go into the stack.
   Lower is sharper and noisier.
4. **Output name**, then **Stack**. **Abort** cancels mid-job.

The choice of stack type is remembered on this browser.

### Pipeline

The status bar shows the phase live: **Reading**, **Analyzing** (sharpness
per frame), **Ranking**, **Aligning**, **Stacking**, **Writing**, then
**Ok** or **Fail**. Sharpness is the variance of the Laplacian on the
blurred luminance of each frame, so it ranks seeing rather than noise or
the Bayer pattern.

After the global registration the stack is refined on a mesh of
alignment points (PlanetarySystemStacker style): each point keeps its own
best frames and its own local shift, which is what follows the seeing
across a large disc or a lunar surface. On a target too small for a mesh
the single global registration is used. The API exposes the mesh settings
(`/api/video/stack/start`: `alignmentPoints`, `apBoxSize`, `apFramePercent`
and friends); the tab keeps the defaults.

## Output

`{ImageOutputDir}/{Rig}/planetary/{target}/stacked/{outputName}_{ts}.fits`

16-bit mono FITS. Open in PixInsight / Siril / Photoshop. Apply
wavelet sharpening + saturation in your favorite tool.

## Common pitfalls

**fps caps at ~5 even though camera supports native stream**, check
that `CCD_VIDEO_STREAM` is actually exposed by your driver. Some INDI
drivers default to `OFF` and need to be enabled in indiserver
parameters.

**Dropped frames warning**, writer can't keep up with stream cadence.
Reduce ROI (smaller frames write faster), lower binning, or lower
target fps via the driver's `STREAMING_DELAY` property.

**Stack output is dark**, frames were mostly cropped to a small
brightness region. Try wider ROI or increase Keep% to include more
frames.

**Storage explodes**, 60s @ 30fps × 800×600 × uint16 ≈ 1.7 GB.
Watch your disk; planetary captures fill SD cards fast.

## See also

- [Glossary → SER / Lucky imaging / Laplacian variance](GLOSSARY.md#l)
