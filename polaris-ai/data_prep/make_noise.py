"""Build DENOISE training pairs by degrading the *clean* image.

Ground truth = the user's denoised FITS (``data/own/raw/denoised``). Input = that
clean image + synthetic sensor noise (Poisson shot + Gaussian read, no blur).
Tiles are emitted in the same per-channel MAD-normalized domain Polaris feeds the
denoise model at inference (``onnx-pipelines.js``: ``(v-med)/mad*0.04``, clip ±10).

  python data_prep/make_noise.py --per-image 3

Outputs:
  data/own/raw/originals+noise/<name>.fit          (one noisy full frame, preview)
  data/own/denoise_tiles/{input,target}/*.npy      (synthetic train pairs, [3,256,256])
  data/own/denoise_val/{input,target}/*.npy        (real noisy->clean val pairs)
"""
from __future__ import annotations

import argparse
import glob
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C  # noqa: E402

CLIP = 10.0  # denoise v2 input clip (onnx-pipelines.js)


def _emit_pairs(noisy, clean, name, in_dir, tgt_dir, tile, stride, rng):
    """Tile a (noisy, clean) RGB pair, MAD-normalize with the NOISY image's
    per-channel stats (matching inference), and write tile pairs. Returns count."""
    stats = [C.median_mad(noisy[c]) for c in range(noisy.shape[0])]
    noisy_n, _ = C.normalize_rgb(noisy, stats=stats, clip=CLIP)
    clean_n, _ = C.normalize_rgb(clean, stats=stats, clip=None)
    lum = C.to_luminance(clean)
    n = 0
    for (y0, x0) in C.tile_coords(noisy.shape[1], noisy.shape[2], tile, stride):
        if C.is_mostly_empty(lum[y0:y0 + tile, x0:x0 + tile]):
            continue
        x = noisy_n[:, y0:y0 + tile, x0:x0 + tile]
        y = clean_n[:, y0:y0 + tile, x0:x0 + tile]
        if x.shape[1:] != (tile, tile):
            continue
        C.save_pair_tile(in_dir, tgt_dir, f"{name}_{y0:05d}_{x0:05d}", x, y)
        n += 1
    return n


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", default="data/own/raw")
    ap.add_argument("--out", default="data/own")
    ap.add_argument("--per-image", type=int, default=3,
                    help="independent noise draws per clean image")
    ap.add_argument("--tile", type=int, default=256)
    ap.add_argument("--stride", type=int, default=192)
    ap.add_argument("--seed", type=int, default=0)
    args = ap.parse_args()

    clean_dir = os.path.join(args.root, "denoised")
    orig_dir = os.path.join(args.root, "originals")
    preview_dir = os.path.join(args.root, "originals+noise")
    in_dir = os.path.join(args.out, "denoise_tiles", "input")
    tgt_dir = os.path.join(args.out, "denoise_tiles", "target")
    vin_dir = os.path.join(args.out, "denoise_val", "input")
    vtgt_dir = os.path.join(args.out, "denoise_val", "target")
    rng = np.random.default_rng(args.seed)

    cleans = sorted(glob.glob(os.path.join(clean_dir, "*.fit*")))
    if not cleans:
        raise SystemExit(f"no clean FITS under {clean_dir}")

    total = 0
    for path in cleans:
        name = C.basename_no_ext(path)
        clean = C.load_fits_rgb(path)
        for k in range(args.per_image):
            noisy = C.add_sensor_noise_rgb(clean, rng)
            if k == 0:
                C.save_fits_rgb(os.path.join(preview_dir, f"{name}.fit"), noisy)
            n = _emit_pairs(noisy, clean, f"{name}_k{k}", in_dir, tgt_dir,
                            args.tile, args.stride, rng)
            total += n
        print(f"{name}: emitted train tiles (running total {total})")

        # Real validation pair: original (noisy) -> denoised (clean).
        orig_path = os.path.join(orig_dir, f"{name}.fit")
        if os.path.exists(orig_path):
            orig = C.load_fits_rgb(orig_path)
            if orig.shape == clean.shape:
                _emit_pairs(orig, clean, f"{name}_real", vin_dir, vtgt_dir,
                            args.tile, args.stride, rng)

    print(f"done: {total} denoise train tile pairs -> {in_dir}")


if __name__ == "__main__":
    main()
