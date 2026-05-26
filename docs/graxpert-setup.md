# GraXpert setup for Polaris

[GraXpert](https://www.graxpert.com) is an AI-based image
correction tool used by Polaris for three operations:

- **Background extraction (BGE)**, removes gradients caused by
  light pollution, sky glow, or vignetting. Works on all GraXpert
  versions.
- **Deconvolution**, sharpens a stacked master by reversing the
  PSF blur. Requires GraXpert v3.0+.
- **Denoising**, AI noise reduction on the final master. Requires
  GraXpert v3.0+.

The killer use-case for Polaris users in light-polluted skies:
running BGE on **every captured light** before stacking, either
automatically during an Autorun or as a one-shot batch. That
removes the gradient before integration instead of after, which
produces much cleaner masters.

## Install

### Linux

Download the binary from [graxpert.com/releases](https://www.graxpert.com)
or the GitHub releases at <https://github.com/Steffenhir/GraXpert>.
Extract to `/opt/graxpert/` or your home directory. Polaris
checks the common locations:

```
/usr/bin/graxpert
/usr/local/bin/graxpert
/opt/graxpert/graxpert
/opt/GraXpert/GraXpert
~/GraXpert/GraXpert
~/.local/bin/graxpert
$PATH
```

### Windows

Run the installer from the [official site](https://www.graxpert.com).
The standard install places `GraXpert.exe` under
`C:\Program Files\GraXpert\`, Polaris auto-detects this. Portable
extractions under `%LOCALAPPDATA%\Programs\GraXpert\` are also
detected.

### macOS

Drag `GraXpert.app` into `/Applications`. Polaris finds the CLI
inside the bundle automatically. Homebrew users can also use
`brew install graxpert`.

### AI model files

GraXpert downloads its models the first time you run BGE / Decon /
Denoise. Make sure the machine has internet access on that first
run, or copy the `models/` folder over manually if it's offline.

## Verify the detection

1. Polaris UI → **Settings → External tools**.
2. The **GraXpert** row should show **✓ Detected v3.0.2, BGE +
   Decon + Denoise** (or **BGE only** for v2.x).
3. Not detected? Click **Re-detect**. If still not found, paste
   the absolute path into **Path override**.

## Default tuning

The Settings panel exposes default tuning per operation. These
are used when you trigger an op without overriding them in the
modal:

| Field | Default | Range | Meaning |
|---|---|---|---|
| BGE smoothing | 1.0 | 0–1 | Higher = smoother background model. 1.0 is fine for most DSO targets. |
| BGE correction | Subtraction | Subtraction / Division | Subtraction for linear data (default); Division for cases where the gradient is multiplicative (uncommon). |
| Decon strength | 0.5 | 0–1 | How aggressively to sharpen. 0.3–0.6 is the sweet spot for most masters. |
| Decon PSF size | 4.0 | 0.5–20 | Estimate of star FWHM in pixels. Eyeball with the FITS viewer's pixel readout. |
| Denoise strength | 0.5 | 0–1 | 0.3 is gentle, 0.7+ is heavy. Watch for plastic-looking stars. |

## Running on selected files (FILES tab)

1. Open the **FILES** tab.
2. Navigate to your frames + multi-select them.
3. Three buttons appear in the toolbar (only those the binary
   supports):
   - **🌅 BGE**, opens the BGE modal
   - **✨ Decon**, opens the Decon modal (greyed on v2.x)
   - **🔇 Denoise**, opens the Denoise modal (greyed on v2.x)
4. Each modal lets you override the default tuning per-run. Hit
   **Start** to kick off the batch.

Outputs land next to each input file with a `_bge` / `_decon` /
`_denoise` suffix so multiple operations on the same file don't
collide.

## Auto-run BGE during capture (Autorun tab)

1. Open the **AUTORUN** tab.
2. Expand **End Events**.
3. Tick **"Auto-extract gradient with GraXpert (per frame)"**.

Every saved light during a sequence run is then sent to GraXpert
for BGE in the background. The next exposure starts immediately,
the BGE job runs fire-and-forget. Outputs land next to each
captured light with `_bge.fits` suffix.

**Decon and Denoise intentionally don't auto-run.** They degrade
SNR on individual lights and are useful only on integrated
masters. Run them manually in STUDIO after stacking.

## Combo: GraXpert BGE + Siril stack

The Siril run modal in STUDIO offers an **"Inject GraXpert BGE
per-frame before stacking"** toggle when both binaries are
detected. With it on, Polaris runs a two-phase pipeline:

1. GraXpert BGE on each selected light (~10 s per frame).
2. Siril script on the cleaned `_bge` outputs instead of the
   originals.

Slower than a plain Siril run, but produces a noticeably cleaner
master under heavy light pollution.

## Performance notes

- AI models eat **3–8 GB of RAM** per concurrent process.
  Default concurrency is **1**. Power users on Windows mini-PCs
  can bump it up in the FILES batch modal if their machine has
  the headroom, Raspberry Pi 4 / 5 users should stay at 1.
- A 24 MP frame takes ~10 s for BGE on a desktop GPU/CPU, ~30 s
  on a Pi 5. Decon and Denoise are slower (~30 s and ~60 s
  respectively on the same hardware).
- The first run after install downloads the AI models (~150–500
  MB depending on op).

## Troubleshooting

- **Process completes but no output file**, GraXpert silently
  fell back to the GUI because the `-cli` flag was missing.
  Polaris always passes it, so this usually means an old GraXpert
  build that doesn't recognise `-cli`. Upgrade to v3.0+.
- **"Decon requires GraXpert v3.0+"** error, your installed
  GraXpert is older. Either upgrade or stick to BGE.
- **High RAM usage hangs the Pi**, drop concurrency to 1 and
  consider closing PHD2 + the live preview during heavy GraXpert
  runs.

## License

GraXpert is GPLv3. Polaris invokes it via the CLI only, no
GraXpert code is linked or redistributed. Models ship with the
GraXpert binary; Polaris does not touch them.
