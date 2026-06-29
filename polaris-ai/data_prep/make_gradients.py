"""Build BGE (background-extraction) training pairs by adding synthetic gradients
to the *clean* (background-free) image.

Ground truth = the user's BGE FITS (``data/own/raw/bge``, already flattened).
Input = clean-bg + a smooth synthetic gradient (light pollution / amp glow /
vignette). The BGE model predicts the **background plane**, on the whole frame
downsampled to 256², in the per-channel MAD-normalized domain Polaris uses
(``onnx-pipelines.js``: ``(v-med)/mad*0.04``, clip ±1). The training **target**
is therefore the smooth background = added gradient + the channel's base level.

  python data_prep/make_gradients.py --per-image 40

Outputs:
  data/own/raw/originals+gradients/<name>.fit   (one gradiented full frame, preview)
  data/own/bge_tiles/{input,target}/*.npy       (synthetic train pairs, [3,256,256])
  data/own/bge_val/{input,target}/*.npy         (real originals->gradient val pairs)
"""
from __future__ import annotations

import argparse
import glob
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C  # noqa: E402

SIZE = 256
CLIP = 1.0  # BGE input clip (onnx-pipelines.js)


def _augment(rgb, rng):
    if rng.random() < 0.5:
        rgb = rgb[:, :, ::-1]
    if rng.random() < 0.5:
        rgb = rgb[:, ::-1, :]
    k = int(rng.integers(0, 4))
    if k:
        rgb = np.rot90(rgb, k, axes=(1, 2))
    return np.ascontiguousarray(rgb)


def _emit(inp_full, bg_full, name, in_dir, tgt_dir, rng, augment=True):
    """Downsample input + background to 256², MAD-normalize with the INPUT's
    per-channel stats, write the pair. ``bg_full`` is the background in source
    brightness space (what the model must predict)."""
    small_in = C.downsample_rgb(inp_full, SIZE)
    small_bg = C.downsample_rgb(bg_full, SIZE)
    if augment:
        # apply the SAME geometric aug to both planes
        seed = int(rng.integers(0, 2**31 - 1))
        small_in = _augment(small_in, np.random.default_rng(seed))
        small_bg = _augment(small_bg, np.random.default_rng(seed))
    stats = [C.median_mad(small_in[c]) for c in range(small_in.shape[0])]
    x, _ = C.normalize_rgb(small_in, stats=stats, clip=CLIP)
    y, _ = C.normalize_rgb(small_bg, stats=stats, clip=None)
    C.save_pair_tile(in_dir, tgt_dir, name, x, y)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", default="data/own/raw")
    ap.add_argument("--out", default="data/own")
    ap.add_argument("--per-image", type=int, default=40,
                    help="synthetic gradient draws per clean image")
    ap.add_argument("--seed", type=int, default=0)
    args = ap.parse_args()

    bge_dir = os.path.join(args.root, "bge")
    orig_dir = os.path.join(args.root, "originals")
    preview_dir = os.path.join(args.root, "originals+gradients")
    in_dir = os.path.join(args.out, "bge_tiles", "input")
    tgt_dir = os.path.join(args.out, "bge_tiles", "target")
    vin_dir = os.path.join(args.out, "bge_val", "input")
    vtgt_dir = os.path.join(args.out, "bge_val", "target")
    rng = np.random.default_rng(args.seed)

    cleans = sorted(glob.glob(os.path.join(bge_dir, "*.fit*")))
    if not cleans:
        raise SystemExit(f"no BGE FITS under {bge_dir}")

    total = 0
    for path in cleans:
        name = C.basename_no_ext(path)
        clean = C.load_fits_rgb(path)                 # background-free
        base = np.array([np.median(clean[c]) for c in range(clean.shape[0])],
                        dtype=np.float32)
        for k in range(args.per_image):
            with_grad, grad = C.add_gradient_rgb(clean, rng)
            bg_full = grad + base[:, None, None]      # full smooth background
            if k == 0:
                C.save_fits_rgb(os.path.join(preview_dir, f"{name}.fit"), with_grad)
            _emit(with_grad, bg_full, f"{name}_k{k:03d}", in_dir, tgt_dir, rng)
            total += 1
        print(f"{name}: {args.per_image} gradient pairs (total {total})")

        # Real validation pair: original -> (original - bge) background plane.
        orig_path = os.path.join(orig_dir, f"{name}.fit")
        if os.path.exists(orig_path):
            orig = C.load_fits_rgb(orig_path)
            if orig.shape == clean.shape:
                real_bg = np.clip(orig - clean, None, None).astype(np.float32) \
                    + base[:, None, None]
                _emit(orig, real_bg, f"{name}_real", vin_dir, vtgt_dir, rng,
                      augment=False)

    print(f"done: {total} BGE train pairs -> {in_dir}")


if __name__ == "__main__":
    main()
