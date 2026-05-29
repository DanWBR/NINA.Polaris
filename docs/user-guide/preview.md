# PREVIEW tab

Snap test shots + continuous stream for framing / focusing / testing
exposure settings. Decoupled from the AUTORUN sequence engine, use
PREVIEW when you want to look without committing to a save.

## Controls

- **Exp** (seconds), exposure time. Sub-1s for planetary or focus
  tests; 5-30s for DSO framing.
- **Gain**, camera gain (units depend on driver, ZWO uses 0-600
  typical, QHY similar)
- **Bin**, 1×1, 2×2, 4×4
- **Filter** (only when filter wheel is connected), pre-swap before
  capture
- **💾 Save to disk**, toggle. When ON, snaps land in
  `{ImageOutputDir}/{Rig}/snaps/{Filter}_{Date}/`. When OFF (default),
  snaps render in the preview but don't persist, perfect for test
  shots that would otherwise pollute the science folder.

## Buttons

### Polaris Shutter (round capture button)

Snap / Loop / Abort all routed through the big circular shutter
centered in the right sidebar. Same gesture vocabulary as LIVE,
FOCUS Manual, and VIDEO:

- **Tap** when idle, single exposure. Anel preenche em tempo
  real conforme a exposição roda; countdown numérico embaixo.
- **Long-press** (hold ≥600ms) when idle, enters loop mode.
  Each frame waits for the previous to finish + render before
  the next starts; effective fps bounded by exposure + transfer
  + render. Anel âmbar durante o hold confirma o arming.
- **Tap during snap or loop**, aborts.

### Other controls

- **🎥 Stream**, server-side continuous capture, kept as its own
  toggle below the shutter (Stream is a distinct mode, not a
  snap or loop). Auto-picks between:
  - **Native mode** (label: "native · X.X fps") when the camera
    supports INDI's `CCD_VIDEO_STREAM`, driver fires continuous
    BLOBs at 10-30 fps without per-frame round-trips
  - **Loop mode** (label: "loop · X.X fps") fallback, tight
    server-side `CaptureAsync` loop
  Tooltip on the button tells you which mode will run.
- **⛶ View**, opens the full-resolution OpenSeadragon viewer for
  pan/zoom inspection

## Snap vs Loop vs Stream, which to use

- **Snap**, one frame. You want to look + decide. Default for focus
  tests, framing, plate-solve seeding.
- **Loop**, chained snaps. Each one goes through the full pipeline
  (FITS save if enabled, stats calc, relay). Bounded fps. Good when
  you care about stats per frame.
- **Stream**, server-side continuous. Frames bypass save + stats,
  ephemeral display only. Highest fps. Good for live view during
  framing or focusing, planetary preview, polar alignment.

Loop + Stream are mutually exclusive (same camera, can't be in two
modes). The buttons disable each other.

## Stats bar (below the canvas)

Shows the most recent snap's:
- **Stars**, count detected by `StarDetector`
- **HFR**, median half-flux radius (lower = sharper)
- **Mean / Median / StDev**, pixel statistics
- **Min / Max**, clipping detection
- **last:** timestamp of the snap

## Renderer

The canvas uses Polaris's WebGL2 pipeline (when available) for:
- GPU debayering (RGGB / GRBG / GBRG / BGGR per Bayer pattern)
- MTF auto-stretch
- Star-annotation overlay (toggle)
- Crosshair + 3×3 grid (toggle)
- Pixel hover readout (raw ADU + RGB)

Browsers without WebGL2 fall back to server-side JPEG encoding, same
visual result, slightly more CPU on the host.

## Snap save folder convention

When **Save to disk** is on, the path is:

```
{ImageOutputDir}/{Rig}/snaps/{Filter}_{Date}/snap_NNN.fits
```

- `Filter` = current filter name, or `L` when no filter wheel
- `Date` = ISO `yyyy-MM-dd`
- `NNN` = auto-incrementing sequence within the date folder

This deliberately separates test shots from `lights/{Target}/...` so
your STUDIO frame library doesn't get cluttered with focus + framing
test frames you'd just toss.

## Common pitfalls

**Stream button is greyed out**, no camera connected. Connect via
RIGS tab first.

**Loop fps caps at 0.5**, exposure is dominating. Drop exposure to 100ms
or move to Stream mode if you don't need per-frame stats.

**Snap saves don't appear in STUDIO**, STUDIO's frame library only
scans `lights/` + `calibration/` by default. Snaps are in `snaps/`
deliberately. To pull them into STUDIO, move them to `lights/...` or
use the FILES tab.

## See also

- [LIVE](live-stacking.md), same preview canvas but integrating stacks
- [VIDEO](video-planetary.md), same stream, but with SER recording
- [Glossary → MTF / Stretch](GLOSSARY.md#m)
