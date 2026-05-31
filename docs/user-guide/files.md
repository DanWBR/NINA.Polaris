# FILES tab (browser + Stack + Edit)

The FILES tab is the unified workbench for everything that lives on
disk. It replaces the old separate STUDIO and EDITOR tabs: the same
file browser on the left, with two sub-tabs on the right -- **Stack**
for batch processing (master frames, calibration, integration,
combine, color calibration) and **Edit** for individual frame
adjustments (sliders, AI cleanup, crop, export).

## Why it is unified

The three old tabs (FILES, STUDIO, EDITOR) were three views of the
same universe: FITS files on disk. Splitting them forced the user to
jump tabs in the middle of a workflow ("pick lights -> stack ->
edit master"). Now the file browser is always there on the left,
and the right side switches between Stack (batch) and Edit (single
frame) without losing your selection or your place in the tree.

## Layout

```
+-- FILES ------------------------------- [Stack][Edit] --+
|  ROOT v  /home/dan/astro/M31/L      ↻  [ ] FITS meta   |
|--------------------------------------------------------|
|  +-----------------------+ +------------------------+  |
|  | home > dan > astro    | | Stack sub-tab          |  |
|  | -------------------   | | +--------------------+ |  |
|  | [ ] light_001.fits    | | | Lights (12)    +   | |  |
|  | [ ] light_002.fits    | | |   light_001 [x]    | |  |
|  | [ ] master_d.fits     | | | Darks (5)      +   | |  |
|  | [ ] ...               | | | Flats (10)     +   | |  |
|  |                       | | | Biases (50)    +   | |  |
|  | [Copy][Cut][Delete]   | | |                    | |  |
|  | [Compare][Crop]       | | | Actions:           | |  |
|  | [Set as Studio root]  | | | [Master][Calibrate]| |  |
|  | [GraXpert: BGE...]    | | | [Integrate][Combine]|  |
|  |                       | | | [ColorCal][Siril]  | |  |
|  +-----------------------+ +--------------------+   |  |
+---------------------------------------------------------+
```

Browser on the left is fixed; the right column switches between
**Stack** and **Edit** with the two pills at the top of the panel.

## The browser (left column)

Same browser the old FILES tab had:

- **Drive picker**: Windows shows lettered drives; Linux shows `/`,
  `/home`, `/mnt`, `/media`, and `~`
- **Path crumbs**: click any segment to jump up
- **Toolbar**: New folder, Upload, Download, Cut, Copy, Paste,
  Rename, Delete, Compare, Crop, ⭐ Set as Studio root, GraXpert
- **Listing**: checkbox-select rows with name / size / modified /
  type columns; double-click a folder to enter, double-click a file
  to preview
- **Selection bar**: "2 files · 124 MB" plus the current Studio
  root indicator

### FITS metadata toggle (new)

Above the listing there is a **Show FITS metadata** checkbox. Default
is off (keeps the listing fast in folders with thousands of files).
When you flip it on:

- Four extra columns appear in the table: **Type**, **Filter**,
  **Target**, **Exposure (s)**
- The values come from the SQLite frame library cache (single batch
  lookup, sub-100 ms even with hundreds of rows in view)
- Files not yet indexed show "--" in those columns; click Rescan
  in the toolbar to refresh
- Your preference is remembered across sessions (localStorage)

Use this when you need to scan a folder of mixed lights / darks /
flats and don't want to open each file to check the header.

### Preview

Double-click a file:

- **FITS / XISF / TIFF / PNG / JPG**: opens in the OpenSeadragon
  viewer with auto-stretch for FITS
- **TXT / LOG / JSON / MD**: modal showing the first 32 KB of text

### Mutations

Standard semantics:

- Cut + Paste = move (cross-volume handled by copy + delete)
- Copy + Paste = duplicate
- Delete = confirm prompt + server log line
- Rename = inline edit on the row
- Multi-download = single streaming ZIP straight to the browser

### Set as Studio root

Navigate to a folder + click ⭐ → `profile.ImageOutputDir` updates
and the frame library re-indexes against the new root. Canonical way
to switch storage targets between sessions.

## Stack sub-tab

The Stack sub-tab is where you turn a pile of lights / darks / flats
/ biases into a clean integrated master.

### Slot model

Four slot cards on top: **Lights**, **Darks**, **Flats**, **Biases**.
Each card shows the count plus the filenames already added, with a
small "x" to remove individual entries and a "Clear" button to empty
the slot.

Workflow:

1. Navigate in the browser to a folder of lights
2. Multi-select the lights you want (Ctrl-click / Shift-click)
3. Click **+ Add to Lights** on the Lights slot card
4. The selected paths get pushed into the slot (deduped) and the
   browser selection clears
5. Repeat for darks, flats, biases by browsing to those folders

Slots survive page refresh and server restart (persisted to
localStorage). Use **Clear all slots** when starting a new project.

The Stack pipeline operates on **paths**, not on database IDs. You
don't need the files to be indexed in the frame library before you
can stack them -- pick them in the browser and the actions just
work.

### Actions

Once you've populated the relevant slots, the buttons below run the
corresponding pipeline:

| Button | Slot it consumes | What it does |
|---|---|---|
| **Master Dark** | Darks | Sigma-clipped mean of N darks per (exposure, gain) group |
| **Master Flat** | Flats | Sigma-clipped mean of N flats per (filter, gain) group |
| **Master Bias** | Biases | Sigma-clipped mean of N biases per (gain) group |
| **Calibrate** | Lights + nearest masters | Subtracts dark, divides flat, optionally subtracts bias for each light |
| **Integrate** | Lights (typically calibrated) | Aligns and stacks via average / median / sigma-clipped average |
| **Combine** | Lights grouped by filter | RGB or LRGB channel combination; prompts for the assignment |
| **Color Calibrate** | First light in slot | Background neutralization on the integrated master |
| **Siril** | Lights + optional darks/flats | Delegates to a Siril script if installed |

Each action runs as a background job. Progress shows below the
toolbar ("Master in progress... 3/10 darks processed"). When the
job finishes you get a toast with the output path; click it to jump
to the file in the browser.

### Tips

- **Auto-match for Calibrate**: if you populated the Lights slot but
  not the master-dark/flat/bias slots, Calibrate will look for the
  nearest match in the frame library (same exposure / gain / filter
  / temperature window). To force specific masters, add them to the
  Master Dark / Flat / Bias slots manually.
- **Integration order**: master darks/flats/biases first, then
  calibrate, then integrate. The buttons are independent -- you can
  re-run Integrate on the same lights with a different method
  without re-calibrating.
- **Channel Combine** opens a small prompt asking which slot path
  maps to R, G, B (and L for LRGB). Variable assignment is one path
  per channel.

## Edit sub-tab

Edit is the single-frame Lightroom-style editor for the final
master (or any individual FITS, XISF, PNG, JPG, TIFF).

### Opening a frame

Two ways:

1. **From the browser**: single-select a file, then click **Open in
   editor** in the toolbar (or just switch to the Edit sub-tab; the
   first selected file opens automatically)
2. **From the Stack workflow**: when an Integrate or Master job
   completes, the success toast has an **Open in editor** link

### Layout

- **Viewer (center)**: OpenSeadragon with the working buffer,
  auto-stretched on first load
- **Controls (right)**: collapsible sections (Light, Color, Curve,
  Detail, Effects, Geometry, AI, Export). Each section has sliders
  bound to a non-destructive edit pipeline.
- **Sidecar persistence**: edits are saved alongside the source as
  `{source}.edit.json`. Re-opening the same file restores the
  sliders.

### Sections

- **Light**: Exposure, Contrast, Highlights, Shadows, Whites, Blacks
- **Color**: White Balance (temp/tint), Vibrance, Saturation, Hue
- **Curve**: spline tone curve (RGB master + per-channel R/G/B)
- **Detail**: Sharpening, Noise reduction
- **Effects**: Texture, Clarity, Dehaze, Vignette
- **Geometry**: Crop, Rotate, Resize
- **AI**: GraXpert Background Extraction, Denoise, Deconvolution
  (browser-side via ONNX or server-side via CLI subprocess; both
  work)
- **Auto**: one button that computes reasonable values for Light +
  Color sliders from frame statistics (Lightroom-style)

### Export

The Export button opens a modal:

- **Format**: JPEG (with quality 0-100), PNG (8 or 16 bit), TIFF
  (16 bit)
- **Resize**: pixels or percent of original
- **Output**: defaults to `processed/{target}/edited/` inside the
  current Studio root

Exported files appear in the browser after a rescan.

## Common pitfalls

**Slot has paths but Action says "no files"**: the files were
deleted or moved on disk after you added them to the slot. Click
the "x" next to each missing entry, or **Clear all slots** and
re-add.

**FITS metadata toggle adds blank columns**: those files aren't in
the frame library yet. Click **Rescan** in the browser toolbar to
index them, or open the STUDIO tab once (which also rescans).

**Edit sub-tab is blank**: no file is loaded. Select a single FITS
in the browser and click **Open in editor**, or pick a file in the
Stack toast.

**Long ZIP downloads time out**: your reverse proxy (if any) needs
a long read timeout. Direct LAN access via port 5000 is fine.

## Security model

Polaris assumes a trusted LAN. The browser exposes the **entire
filesystem** of the host (within the user account running the
server). A blocklist covers obvious traps (`/proc`, `/sys`,
`/dev/shm`, `/etc/shadow`, `~/.ssh`, Windows registry hives), and
destructive ops require double confirmation, but the surface is
wide.

**Don't expose Polaris directly to the internet** without the Relay
server (which has tokens, TLS, and per-tenant rate limits). See the
[Relay guide](relay.md).

## See also

- [Live stacking](live-stacking.md): real-time stacking during
  capture, separate from the offline Stack workflow here
- [Editor reference](editor.md): deeper coverage of each Edit
  slider and the non-destructive pipeline
- [Siril setup](siril-setup.md): how to install Siril so the Siril
  button in Stack lights up
- [GraXpert / ONNX](onnx-inference.md): browser-side AI cleanup
- [Relay](relay.md): remote access with auth
