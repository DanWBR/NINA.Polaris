# Image Blend (recombine two images with independent stretch)

Image Blend recombines two same-size images with an **independent
non-linear stretch on each** and a blend mode + opacity. It's the
finishing step of the starless workflow — put the stretched stars back
onto a separately-stretched starless nebula — but works for any two
matching images (e.g. an Ha layer screened onto an OSC RGB).

It mirrors PixInsight's ImageBlend script: per-image
Blackpoint / Midtones / Highlights, a blend mode (Screen by default),
and an opacity slider, with a live preview.

## Opening it

Two ways:

- **From star removal** — after **🌠 Remove stars** (see
  [Star removal](star-removal.md)) the modal opens automatically with the
  starless as **base** and the stars as **blend**.
- **Manually** — in **FILES**, select **exactly two** files (the **base**
  first, the **blend** second), then click **✨ Image Blend**.

The server loads both images into a short-lived session (idle-evicted
after 30 min) and renders a downscaled JPEG preview as you drag sliders.

## Controls

Each of the two panels (**Base** and **Blend**) has:

- **Blackpoint / Midtones / Highlights** — a Midtones Transfer Function
  (MTF) stretch, identical to the editor's manual stretch. Lower the
  midtones to lift faint signal; raise the blackpoint to clip background.
- **Auto** — a sensible non-linear starting point (the base gets a
  stronger shadow lift than the stars layer).
- **Reset** — back to linear (0 / 0.5 / 1).

Global controls:

- **Mode** — `Screen` (default, `1−(1−a)·(1−b)`), `Add`, or `Lighten`
  (per-pixel max). Screen is the right choice for adding stars back: it
  brightens without clipping the nebula.
- **Opacity** — mixes the blended result against the base:
  `base·(1−op) + blended·op`. Lower it to make the stars subtler.

## Output

**Create new image** renders the recombination at full resolution in
float (quantized to 16-bit only on write) and saves a `_blend.fits`
sibling, carrying the base image's metadata/WCS plus the blend
parameters as FITS keywords. The FILES list refreshes onto the result.

## Notes

- Both images must have the **same width, height, and channel count**
  (the loader rejects mismatches). RGB is processed per plane.
- The preview uses a fast 8-bit path; the rendered FITS uses the
  full-precision float stretch, so the final file is cleaner than the
  preview implies.
