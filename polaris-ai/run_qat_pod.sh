#!/usr/bin/env bash
# run_qat_pod.sh — QAT fine-tune + int8 export for halo and upscale.
# Run this on the pod after fp32 training is complete.
# Assumes checkpoints/halo/best.pt and checkpoints/upscale/best.pt exist.
set -euo pipefail
cd "$(dirname "$0")"

GPU=${1:-0}
export CUDA_DEVICE_ORDER=PCI_BUS_ID
export CUDA_VISIBLE_DEVICES=$GPU

echo "==> Using GPU $GPU"
python -c "import torch; print('device:', torch.cuda.get_device_name(0))"

# ── halo ────────────────────────────────────────────────────────────────────
echo
echo "==> QAT: halo (20 ep, batch auto-scaled from 8)"
python train_task.py \
    --task halo --qat \
    --resume checkpoints/halo/best.pt \
    --lr 5e-5 --epochs 20 --batch 8 \
    --base 96 --blocks 3 \
    --pairs data/own/halo_tiles --val-pairs data/own/halo_val \
    --out checkpoints/halo_qat

echo "==> Export halo (from baked QAT best.pt)"
python export.py \
    --task halo --ckpt checkpoints/halo_qat/best.pt \
    --base 96 --blocks 3 --size 256 --out models

echo "==> Calib + int8 PTQ halo (on QAT-baked weights)"
python quantize.py calib --task halo \
    --out models/calib_halo --pairs data/own/halo_tiles
python quantize.py int8 \
    --onnx models/halo_fp32_256.onnx \
    --calib models/calib_halo \
    --out models/halo_int8_256.onnx

# ── upscale ─────────────────────────────────────────────────────────────────
echo
echo "==> QAT: upscale (20 ep, batch auto-scaled from 8 → 1)"
python train_task.py \
    --task upscale --qat \
    --resume checkpoints/upscale/best.pt \
    --lr 5e-5 --epochs 20 --batch 8 \
    --base 64 --blocks 2 --scale 2 \
    --pairs data/own/upscale_tiles --val-pairs data/own/upscale_val \
    --out checkpoints/upscale_qat

echo "==> Export upscale (from baked QAT best.pt)"
python export.py \
    --task upscale --ckpt checkpoints/upscale_qat/best.pt \
    --base 64 --blocks 2 --scale 2 --size 128 --out models

echo "==> Calib + int8 PTQ upscale (on QAT-baked weights)"
python quantize.py calib --task upscale \
    --out models/calib_upscale --pairs data/own/upscale_tiles
python quantize.py int8 \
    --onnx models/upscale_fp32_128.onnx \
    --calib models/calib_upscale \
    --out models/upscale_int8_128.onnx

echo
echo "Done. int8 models:"
ls -lh models/halo_int8_256.onnx models/upscale_int8_128.onnx
