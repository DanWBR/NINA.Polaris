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

# 2. Train (fp32) on your GPU:
python train.py --tiles data/tiles --epochs 60 --batch 16 --out checkpoints/decon

# 3. Export:
python export.py --ckpt checkpoints/decon/best.pt --out models --size 256
#    -> models/decon_fp32.onnx, models/decon_fp16.onnx

# 4. Quantize for the NPUs:
python quantize.py ptq  --ckpt checkpoints/decon/best.pt --tiles data/tiles --out models   # int16-friendly
python quantize.py qat  --tiles data/tiles --resume checkpoints/decon/best.pt --out checkpoints/decon_qat  # int8, no-compromise
```

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
- **int8**: **QAT** (fake-quant during training) — required to keep stars/edges
  clean. PTQ int8 alone will ring/blur.

Always validate int8 vs int16 vs fp16 vs fp32 side by side: PSNR/SSIM, **stellar
FWHM** before/after, and a visual halo/ring check around bright stars.
