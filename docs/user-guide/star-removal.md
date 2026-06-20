# Star removal + recombine (StarNet++ → Image Blend)

Polaris can split a master into a **starless** image and a **stars-only**
image, let you stretch each independently, then recombine them with a
Screen blend — the classic "process the nebula and the stars separately"
workflow you'd otherwise do in PixInsight (StarNet + the ImageBlend script).

Two pieces:

1. **Remove stars** — StarNet++ runs as an ONNX model in your browser
   (same engine as the GraXpert AI ops), producing `_starless` and
   `_stars` sibling FITS.
2. **[Image Blend](image-blend.md)** — recombine two images with an
   independent blackpoint/midtones/highlights stretch per layer plus a
   Screen blend and opacity.

## One-time setup: install the model

Star removal needs the converted StarNet model. It is **not** bundled by
default (the weights are ~207 MB and NonCommercial — see Licensing below),
so you install it once on the machine that has the file system Polaris
serves from:

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

## Using it

1. Go to **FILES** and select **one** image (a stretched or linear master;
   FITS/XISF).
2. Click **🌠 Remove stars**. On first use you accept the model licence
   (CC BY-NC-SA, same prompt as the GraXpert AI ops).
3. A progress overlay shows the tiled inference. StarNet processes the
   image in 256×256 tiles; on a WebGPU-capable browser this is seconds,
   on plain WASM it can take up to a minute for a large master.
4. When it finishes, two siblings are written next to the source —
   `{name}_starless.fits` and `{name}_stars.fits` — and the
   **[Image Blend](image-blend.md)** tool opens automatically, pre-filled
   with the starless as the **base** and the stars as the **blend**.
5. Stretch each layer to taste (the stars layer usually wants a gentler
   midtone lift than the starless), keep the mode on **Screen**, then
   **Create new image** to write the recombined result.

The stars-only image is auto-derived as `clamp(original − starless, 0)`,
so it contains exactly what the network removed — no separate star mask
step needed.

## Notes

- **RGB and mono** both work. RGB goes through the network with all three
  channels together; mono is fed to all three input channels and averaged
  back.
- **GPU**: tick "Use GPU" in the AI settings to push inference onto WebGPU
  where available. Access Polaris over **https** (or `localhost`) — Chrome
  blocks WebGPU on plain-HTTP LAN addresses. See [HTTPS setup](https-setup.md).
- You can also run **Image Blend on its own** for any two matching images:
  select exactly two files in FILES (base first, blend second) and click
  the **✨ Image Blend** button.

## Licensing

StarNet *code* is MIT, but the *pre-trained weights* (and the converted
`model.onnx`, which embeds them) are
**Creative Commons Attribution-NonCommercial-ShareAlike 4.0** — Copyright
© Nikita Misiura (nekitmm), <https://github.com/nekitmm/starnet>.
NonCommercial use only, with attribution. The full notice ships beside the
model as `starnet-ai-models/1.0.0/LICENSE.txt`.
