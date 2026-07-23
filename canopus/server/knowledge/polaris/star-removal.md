# Star removal + recombine (StarNet++ → Image Blend)

Polaris can split a master into a **starless** image and a **stars-only**
image, let you stretch each independently, then recombine them with a
Screen blend - the classic "process the nebula and the stars separately"
workflow you'd otherwise do in PixInsight (StarNet + the ImageBlend script).

Two pieces:

1. **Remove stars** - StarNet++ runs as an ONNX model in your browser
   (same engine as the GraXpert AI ops), producing `_starless` and
   `_stars` sibling FITS.
2. **[Image Blend](image-blend.md)** - recombine two images with an
   independent blackpoint/midtones/highlights stretch per layer plus a
   Screen blend and opacity.

## Choosing a model

Polaris supports several star-removal models. When more than one is installed
the **Remove stars** dialog shows a **Model** dropdown:

- **nox** (StarNet-like, ~54M params) - **MIT-licensed** (code *and* weights),
  the recommended default. Native colour model (one inference per tile) plus a
  gray model. StarNet-grade quality with a permissive licence.
- **starrem2k13** (pix2pix-style U-Net, ~31M params) - **MIT-licensed**,
  512² tiles processed per channel. (Uses the model from the pinned commit
  `0398ce05`, not the repo main branch's tiny U2NETP, which removes stars poorly.)

Each star-removal model comes in two precisions, following the GraXpert
convention:

- **`1.0.0`** = the original **FP32** model (~218 MB nox/StarNet, ~125 MB
  starrem2k13). Best quality; an optional download for desktops.
- **`1.0.0-fp16`** = the **FP16** quantization (~109 MB nox/StarNet, ~62 MB
  starrem2k13). This is the **default that runs** on every platform - half the
  weights, fits SBCs/phones/tablets via WebGPU/WASM, I/O stays FP32 so accuracy
  is essentially unchanged. It's what ships bundled in the OS images.

The pipeline auto-selects the `-fp16` sibling when it's installed; the FP32
`1.0.0` is there for whoever wants maximum quality. To (re)generate FP16 from a
converted FP32 model, run
`scripts/quantize_onnx_models.py --fp16 --only <family>` (writes a
`{version}-fp16` sibling; add `--replace` to overwrite in place instead).
- **StarNet++** (v1) - high-quality removal, but the weights are
  **CC BY-NC-SA (NonCommercial)**, so it is opt-in and you install it yourself.

Both run as ONNX in your browser through the same pipeline (auto-stretch into
the trained domain, optional 2nd pass, and the mask-guided halo cleanup).

## One-time setup: install a model

### nox (recommended, MIT)

Download `generator_color.h5` + `generator_gray.h5` from the
[nox releases](https://github.com/charvey2718/nox/releases), then:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\convert-nox-onnx.ps1 `
    -WeightsDir C:\path\to\nox\v1.0
```

Writes `model.onnx` into `nox-color-ai-models/1.0.0/` and
`nox-gray-ai-models/1.0.0/`. See
[`scripts/convert-nox-onnx.md`](../../scripts/convert-nox-onnx.md).

### starrem2k13 (MIT)

Install it once on the machine Polaris serves from. Download the trained
weights (a TF checkpoint: `checkpoint` + `weights.index` + `weights.data-*`)
from the project's GitHub Releases, then run the converter (it fetches the
right `model.py`, loads the checkpoint, and exports ONNX in Docker):

```powershell
powershell -ExecutionPolicy Bypass -File scripts\convert-starrem2k13-onnx.ps1 `
    -WeightsDir C:\path\to\weights
```

It writes `model.onnx` into
`wwwroot/graxpert/models/starrem2k13-ai-models/1.0.0/`. See
[`scripts/convert-starrem2k13-onnx.md`](../../scripts/convert-starrem2k13-onnx.md).

### StarNet++ (optional, NonCommercial)

StarNet is **not** bundled by default (the weights are ~207 MB and
NonCommercial - see Licensing below), so you install it once on the machine
that has the file system Polaris serves from:

1. Get the StarNet v1 TensorFlow weights and the
   [`DanWBR/starnet`](https://github.com/DanWBR/starnet) checkout (the
   `wherearemyweights.txt` there links the Dropbox download; extract the
   `model.ckpt.*` files into the checkout).
2. Run the converter (Windows; uses Docker so no local Python 3.7 needed):
   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts\convert-starnet-onnx.ps1 -Docker
   ```
   It freezes the generator graph, converts it to ONNX, and drops
   `model.onnx` into
   `src/NINA.Polaris/wwwroot/graxpert/models/starnet-ai-models/1.0.0/`.
   See [`scripts/convert-starnet-onnx.md`](../../scripts/convert-starnet-onnx.md)
   for details and the no-Docker path.
3. Restart Polaris (or `POST /api/onnx/rescan`). The **🌠 Remove stars**
   button appears in the FILES toolbar once the `starnet` family shows up
   in `GET /api/onnx/manifest`.

### Downloading models on-device

Converting models needs a build machine with Docker; devices and OS images
that ship Polaris **without** the bundled models (or phones/tablets) can pull
ready-made `.onnx` files straight from the app instead.

1. Go to **Settings → AI inference (ONNX) → Download models** and click
   **Refresh catalog**. By default this lists the models hosted in the public
   **Polaris Astro Controller model repository** on SourceForge - no configuration
   needed (the app ships a bundled `models-index.json`).
2. Click **⬇ Download** next to a model. It streams onto this device's
   writable models directory and the registry rescans automatically - no
   browser restart needed. A progress bar tracks the transfer.

The downloaded file lands under the writable target dir resolved from your
profile `OnnxModelsPath` → `/home/polaris/models` (Linux) → the bundled
`wwwroot/graxpert/models` folder.

#### Using a custom bucket instead

Expand **Advanced - use a custom model bucket** to point at your own host
(e.g. a Supabase / S3 bucket). The base URL must serve a `models-index.json`
plus the directory layout `{base}/{family}-ai-models/{version}/model.onnx`.

`models-index.json` is a JSON array, one entry per downloadable model
(this is the same format as the bundled
`src/NINA.Polaris/wwwroot/graxpert/models-index.json`):

```json
[
  {
    "dir": "nox-color-ai-models",
    "version": "1.0.0",
    "bytes": 109241715,
    "label": "nox colour (FP16)",
    "sha256": "(optional, lowercase hex of model.onnx)",
    "url": "(optional absolute override; e.g. a SourceForge download link)"
  }
]
```

- `dir` is the on-disk family directory (the `{family}-ai-models` form).
- `url` is optional; when present it overrides the
  `{base}/{dir}/{version}/model.onnx` convention. The bundled catalogue uses
  this to point each entry at its SourceForge mirror-redirect download URL.
- `sha256` is optional but recommended; when present the download is rejected
  on mismatch (`Get-FileHash model.onnx -Algorithm SHA256`). When absent, a
  size sanity-check against `bytes` guards against a mirror returning an
  error page.
- Endpoints behind this UI: `GET /api/onnx/catalog`,
  `POST /api/onnx/download` `{dir, version}`,
  `GET /api/onnx/download-status`. One download runs at a time.

## Using it

1. Go to **FILES** and select **one** image (a stretched or linear master;
   FITS/XISF).
2. Click **🌠 Remove stars**. In the options dialog you can tune:
   - **Model** - starrem2k13 (MIT, default) or StarNet++ (if installed).
     Only shown when more than one model is installed.
   - **Auto-stretch / Stretch strength** - stretch the linear data into
     the model's trained domain (leave on for linear stacks).
   - **Passes** - a 2nd pass re-runs the starless through the net to clean
     bright-star halos (~2× slower).
   - **Reduce halos** (on by default) - a post-process that removes the
     residual halos and dark rings StarNet leaves around bright stars
     (see below). **Halo strength** controls how wide the cleanup reaches
     around each star.

   On first use you accept the model licence (CC BY-NC-SA, same prompt as
   the GraXpert AI ops).
3. A progress overlay shows the tiled inference. StarNet processes the
   image in 256×256 tiles; on a WebGPU-capable browser this is seconds,
   on plain WASM it can take up to a minute for a large master.
4. When it finishes, two siblings are written next to the source -
   `{name}_starless.fits` and `{name}_stars.fits` - and the **before/after
   comparator** opens with the **original on the left and the starless
   result on the right** (drag the divider to compare).
5. Recombine when you're ready: open **[Image Blend](image-blend.md)** from
   FILES (select `{name}_starless` then `{name}_stars`, or the original
   plus `{name}_stars`), stretch each layer to taste - the stars layer
   usually wants a gentler midtone lift than the starless - keep the mode
   on **Screen**, then **Create new image** to write the recombined result.

The stars-only image is auto-derived as `clamp(original − starless, 0)`,
so it contains exactly what the network removed - no separate star mask
step needed.

## Halo reduction

StarNet v1 removes the star core but tends to leave a soft low-frequency
**halo** - and sometimes a **dark ring** - around the brightest stars in the
starless image. The optional **Reduce halos** step cleans these up after the
network runs, entirely in the browser:

1. It builds a star mask from the removed flux (`original − starless`) and
   **dilates** it to cover the halo radius around each star.
2. Inside that mask it replaces the starless with a **smooth background
   estimate** - the average of the surrounding pixels that lie *outside* the
   star regions - so the halo/ring is filled with plausible background.
3. The mask edge is feathered so there's no visible seam.

**Halo strength** raises the coverage radius and fill window: higher removes
larger halos but can soften faint nebulosity that sits directly under a bright
star. If a target is mostly nebula with few bright stars, a lower strength (or
turning it off) is safer; for star-dense fields with obvious halos, raise it.

The cleanup runs on a downscaled copy (halos are low-frequency) so it stays
fast and memory-safe even on large masters and SBCs. The removed halo flux is
folded back into the `_stars` layer, so a Screen recombine still reconstructs
the original.

## Notes

- **RGB and mono** both work. RGB goes through the network with all three
  channels together; mono is fed to all three input channels and averaged
  back.
- **GPU**: tick "Use GPU" in the AI settings to push inference onto WebGPU
  where available. Access Polaris over **https** (or `localhost`) - Chrome
  blocks WebGPU on plain-HTTP LAN addresses. See [HTTPS setup](https-setup.md).
- You can also run **Image Blend on its own** for any two matching images:
  select exactly two files in FILES (base first, blend second) and click
  the **✨ Image Blend** button.

## Licensing

**nox** is **MIT** - code and trained weights - Copyright © 2023 Christopher
Harvey, <https://github.com/charvey2718/nox> (architecture derives from StarNet
ideas by Nikita Misiura, used under MIT). Notice ships beside each model as
`nox-{color,gray}-ai-models/1.0.0/LICENSE.txt`.

**starrem2k13** is **MIT** - both the code and the trained weights - Copyright
© code2k13 (Ashish Patel), <https://github.com/code2k13/starrem2k13>. MIT
permits commercial and non-commercial use with attribution; the notice ships
beside the model as `starrem2k13-ai-models/1.0.0/LICENSE.txt`.

**StarNet++**: the *code* is MIT, but the *pre-trained weights* (and the
converted `model.onnx`, which embeds them) are
**Creative Commons Attribution-NonCommercial-ShareAlike 4.0** - Copyright
© Nikita Misiura (nekitmm), <https://github.com/nekitmm/starnet>.
NonCommercial use only, with attribution. The full notice ships beside the
model as `starnet-ai-models/1.0.0/LICENSE.txt`.
