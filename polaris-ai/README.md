# polaris-ai — training our own deconvolution model

A from-scratch, quantization-first training pipeline for an astrophotography
**deconvolution** model, designed so the **int8 / int16 / fp16** exports run with
no compromises on the targets Polaris uses (Hexagon HTP, Rockchip NPU, Adreno
GPU, CPU, and the browser via ORT-Web).

This lives in the Polaris repo but is a **standalone Python project** — train it
on a desktop GPU (e.g. an RTX 5070), then drop the exported model into Polaris's
existing ONNX → RKNN/QNN/ORT pipelines.

## Why this design

Deconvolution needs **(sharp, blurred)** training pairs, but every real astro
image is already blurred by seeing — there is no clean ground truth. So we use
**synthetic degradation**: take sharp targets, convolve with a *known* PSF, add
realistic noise, and train the net to invert it. We control the PSF, so we get
perfect pairs in unlimited quantity.

The architecture is deliberately **NPU/quantization-friendly** (lessons from the
GraXpert NPU work):

- **No LayerNorm** (the Hexagon V68 rejects it — needs V73+). Uses BatchNorm,
  which folds into the preceding conv at inference (zero runtime cost).
- **No `Inverse` / `ReduceSumSquare`** or other ops that broke the QNN/RKNN
  converters on GraXpert v3 / StarNet.
- **Nearest-upsample + conv** instead of `ConvTranspose` (no checkerboard, clean
  quantization).
- **Single input tensor** (image + condition channel concatenated) instead of
  GraXpert's multi-input (image + sigma + strength) — avoids the multi-input NPU
  binding pain that kept GraXpert decon CLI-only.
- Well-behaved activation ranges → int8 quantizes without artifacts.

Result: one model that lowers cleanly to **Hexagon (int8/int16)**, **RKNN
(fp16)**, **Adreno (`libQnnGpu`, fp16/fp32)**, **CPU**, and **ORT-Web**.

## Layout

```
polaris-ai/
  requirements.txt
  psf.py        # PSF kernels (Moffat / Gaussian / Airy)
  synth.py      # forward model: PSF ⊛ image + Poisson/read noise
  model.py      # ConditionedUNet (HTP-friendly residual U-Net)
  dataset.py    # PyTorch dataset: builds (blurred+sigma, sharp) pairs on the fly
  download.py   # data prep: synthetic star fields + SkyView/survey cutouts → tiles
  train.py      # training loop (Charbonnier + gradient + star-protect loss)
  quantize.py   # QAT (int8) / PTQ (int16) helpers
  export.py     # ONNX fp32 + fp16 export
```

## Quickstart

```bash
cd polaris-ai
python -m venv .venv && source .venv/bin/activate   # (Windows: .venv\Scripts\activate)
pip install -r requirements.txt

# 1. Make training tiles. Synthetic gives PERFECT ground truth:
python download.py synth --out data/tiles --count 4000 --size 256
#    (optional) add real diffraction-limited cutouts as extra "objects":
python download.py skyview --out data/tiles --survey "DSS2 Red" --count 500 --size 256
#    (optional) slice YOUR hand-picked real images (Hubble etc; FITS or bitmaps)
#    into tiles -- lands under data/tiles/real so training picks them up:
python download.py ingest --src ~/hubble_picks --out data/tiles/real --size 256 --stride 192

# 2. Train (fp32) on your GPU:
python train.py --tiles data/tiles --epochs 60 --batch 16 --out checkpoints/decon

# 3. Export:
python export.py --ckpt checkpoints/decon/best.pt --out models --size 256
#    -> models/decon_fp32.onnx, models/decon_fp16.onnx

# 4. Quantize for the NPUs:
python quantize.py ptq  --ckpt checkpoints/decon/best.pt --tiles data/tiles --out models   # int16-friendly
python quantize.py qat  --tiles data/tiles --resume checkpoints/decon/best.pt --out checkpoints/decon_qat  # int8, no-compromise
```

## Model size

Capacity is set by `--base` / `--depth` / `--blocks` (residual blocks per stage
are the main dial). Probe any config before a long train:

```bash
python model.py --base 96 --blocks 3      # prints params + fp32/fp16/int8 MB
```

Defaults (`base=96 depth=4 blocks=3`) land in GraXpert-class territory
(~hundreds of MB fp32). Bump `--blocks`/`--base` for more capacity, lower them
for a lean SBC/NPU variant. **Keep `--base`/`--depth`/`--blocks` identical
between `train.py` and `export.py`** or the checkpoint won't load. A big model is
fine for GPU/CPU/ORT-Web; for the Hexagon NPU you'll likely want a smaller
variant (VTCM + context-binary limits), so consider training two sizes.

## Data sources (ground truth)

Prefer **diffraction-limited** (little/no atmosphere) or **synthetic**:

| Source | Access | License |
|---|---|---|
| Synthetic star fields + Sérsic galaxies | `download.py synth` | ours (perfect GT) |
| HST / JWST (MAST) | `astroquery.mast` | public domain |
| DSS / surveys (SkyView) | `download.py skyview` | free (cite) |
| Legacy Surveys / Pan-STARRS / SDSS | `astroquery` | open (cite) |
| Your own best-seeing stacks | drop `.npy`/`.fits` in `data/tiles` | yours |

Synthetic is the backbone (true sharp GT); real cutouts add realistic structure.

## The model input contract

- **Input**: `[B, 2, H, W]` float32 — channel 0 = image (linear, ~[0,1]),
  channel 1 = a constant **sigma map** (the PSF FWHM the user wants to undo,
  normalized by `SIGMA_NORM` in `synth.py`).
- **Output**: `[B, 1, H, W]` float32 — the deconvolved image (the net learns a
  residual added to the input image).

Polaris builds channel 1 from the decon strength/sigma slider and tiles the frame
(256², overlap-add) exactly like the existing GraXpert pipelines.

## Quantization notes ("no compromises")

- **fp16**: train fp32 → `export.py` converts. Near-lossless. (GPU/CPU/ORT-Web;
  not the Hexagon HTP, which is int-only.)
- **int16**: PTQ with a calibration set ≈ fp16 quality → production NPU path.
- **int8**: train it from scratch with in-house QAT (`train_task.py --qat`, STE
  fake-quant) so the 8-bit model keeps fp16-class quality, then ORT QDQ for
  ORT/CPU or the vendor toolchain's calibrated int8 on-device (RKNN/QNN). Plain
  PTQ int8 (no QAT) can ring on residual-heavy decon — use `--qat` or int16
  there. (See the BGE/denoise/decon section below.)

Always validate int8 vs int16 vs fp16 vs fp32 side by side: PSNR/SSIM, **stellar
FWHM** before/after, and a visual halo/ring check around bright stars.

---

# Three Polaris models from our own data (BGE / denoise / decon)

Beyond the synthetic decon pipeline above, we train **our own** background
extraction, denoise and deconvolution models from the hand-curated linear RGB
FITS in `data/own/raw/` — so Polaris ships models it fully owns. The originals
(`originals/`) plus the processed outputs (`bge/`, `decon/`, `denoised/`) are
real ground-truth pairs; we degrade the *clean* output to synthesize many more.

## Model contracts (drop-in for `onnx-pipelines.js`)

| Task    | Layout | In→Out | Domain | Predicts |
|---------|--------|--------|--------|----------|
| BGE     | NHWC `[1,256,256,3]` | 3→3 | per-channel MAD `(v−med)/mad×0.04`, clip ±1 | the **background plane** (whole frame, 256² downsample) |
| Denoise | NHWC `[1,256,256,3]` | 3→3 | per-channel MAD, clip ±10 | the **clean** image (tiled 256², 64-px margin) |
| Decon   | NCHW `[1,2,H,W]` | 2→1 | per-tile 1–99.9% linear + sigma channel | residual `out = img + delta` (unchanged) |

The same `ConditionedUNet` backs all three (`--out-ch`/`--in-ch`); BGE/denoise are
exported through an NHWC permute wrapper so the existing JS/C# pipelines run them
unchanged.

## 1. Build the datasets (degrade the clean image)

```bash
python data_prep/make_noise.py       --per-image 3     # denoise: denoised/ + noise
python data_prep/make_gradients.py   --per-image 40    # bge:     bge/ + synthetic gradient
python data_prep/make_distortions.py --previews 3      # decon:   sharp tiles for DeconDataset
```

Writes preview FITS to `data/own/raw/originals+{noise,distortions,gradients}/`,
train tiles to `data/own/{denoise,bge,decon}_tiles/`, and held-out **real** val
pairs to `data/own/{denoise,bge}_val/` + `data/own/decon_tiles_val/`.

## 2. Train (from scratch, on this PC's NVIDIA GPU)

```bash
python train_denoise.py --pairs data/own/denoise_tiles --val-pairs data/own/denoise_val --epochs 80
python train_bge.py     --pairs data/own/bge_tiles     --val-pairs data/own/bge_val     --epochs 120
python train.py         --tiles data/own/decon_tiles                                    --epochs 60
# (train_task.py --task ... is the shared engine behind the wrappers)
```

## 3. Export fp16 + int16 + int8

```bash
for T in denoise bge decon; do
  python export.py   --task $T --ckpt checkpoints/$T/best.pt --out models           # fp32 + fp16
  python quantize.py calib --task $T --pairs data/own/${T}_tiles --out models/calib_$T   # (decon: --tiles)
  python quantize.py int16 --onnx models/${T}_fp32_256.onnx --calib models/calib_$T \
         --out models/${T}_int16_256.onnx                                            # int16 PTQ ≈ fp16
  python quantize.py int8  --onnx models/${T}_fp32_256.onnx --calib models/calib_$T \
         --out models/${T}_int8_256.onnx                                             # int8 PTQ (QDQ)
done
```

`fp16` is a near-lossless cast; `int16` PTQ already ≈ fp16. For **int8**, full-image
models (BGE, denoise) tolerate PTQ well; the residual-learning **decon** is the one
that can ring under int8 — prefer **int16 for decon** (lossless), and measure int8
per task with `eval_models.py` before shipping it.

### int8 from scratch — in-house QAT (`--qat`)

We DO train int8/int16 "from scratch", via our own straight-through-estimator
fake-quant (`quant_layers.py`) — not torch.ao FX QAT (whose graph doesn't export
to ONNX on this torch build: `fused_moving_avg_obs_fake_quant` / `quantized::conv2d`
don't lower through the legacy or dynamo exporter).

How it works: `--qat` inserts per-channel symmetric weight fake-quant +
per-tensor activation fake-quant on every conv (STE backward, so gradients pass
through). The network learns weights that sit on the int grid. After training we
**bake** the rounded weights into plain `Conv2d.weight`, drop the observers, and
save a clean fp32 `best.pt` — so `export.py` is unchanged and `quantize.py int8`
(ORT QDQ) reproduces those grid points near-losslessly.

```bash
# fine-tune from the fp32 best (recommended), then export + int8:
python train_task.py --task decon --tiles data/own/decon_tiles \
    --qat --resume checkpoints/decon/best.pt --lr 5e-5 --epochs 15 \
    --out checkpoints/decon_qat
python export.py   --task decon --ckpt checkpoints/decon_qat/best.pt --out models
python quantize.py int8 --onnx models/decon_fp32_256.onnx \
    --calib models/calib_decon --out models/decon_int8_256.onnx
python eval_models.py --task decon --models models --tiles-val data/own/decon_tiles_val
```

`--qat-bits 16` does the same on the int16 grid (rarely needed — int16 PTQ is
already ≈ fp16). On-device 8-bit is still produced by the **vendor toolchain**
(RKNN `build(do_quantization=True, dataset=models/calib_*/list.txt)` or Qualcomm
AI Hub `w8a16`) from the same fp32 — the QAT-baked weights make that lossless too.

## 4. Measure "no quantization degradation"

```bash
python eval_models.py --task denoise --models models --val-pairs data/own/denoise_val
python eval_models.py --task bge     --models models --val-pairs data/own/bge_val
python eval_models.py --task decon   --models models --tiles-val data/own/decon_tiles_val
```

Prints mean PSNR/SSIM per precision; int8-QAT should land within a small delta of
fp16/fp32. Deploy fp16 to RKNN/ncnn (`scripts/convert_rknn_models.py`,
`ncnn/deploy_ncnn_models.py`); int8/int16 to ORT and the NPU vendor toolchains
with `models/calib_*`.
