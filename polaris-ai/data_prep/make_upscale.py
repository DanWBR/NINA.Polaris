"""Build SUPER-RESOLUTION training pairs from the *clean* high-res master.

Ground truth (HR) = the user's clean FITS (default ``data/own/raw/denoised``).
Input (LR) = that HR tile anti-alias-blurred and downscaled by ``--scale`` (+ a
little sensor noise), so the model learns to recover real detail from a
lower-res, undersampled capture -- NOT to hallucinate it (the failure mode of
generic photo upscalers on astro data).

Pairs are emitted in the per-channel MAD-normalized domain (same as denoise/bge),
LR as the model input, HR as the target.

  python data_prep/make_upscale.py --scale 2 --hr-dir denoised

Outputs:
  data/own/upscale_tiles/{input,target}/*.npy   ([3,Lr,Lr] -> [3,Hr,Hr])
  data/own/upscale_val/{input,target}/*.npy     (held-out image)
"""
from __future__ import annotations

import argparse
import glob
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C  # noqa: E402

CLIP = 10.0  # LR input clip in the normalized domain


def downscale_rgb(hr: np.ndarray, scale: int, rng) -> np.ndarray:
    """Anti-alias blur + bilinear downscale a ``(3,H,W)`` tile by ``scale``,
    plus a little sensor noise so the LR looks like a real lower-res frame."""
    from scipy.ndimage import gaussian_filter, zoom

    c, h, w = hr.shape
    out = np.empty((c, h // scale, w // scale), dtype=np.float32)
    for ch in range(c):
        blur = gaussian_filter(hr[ch], sigma=0.5 * scale)
        out[ch] = zoom(blur, 1.0 / scale, order=1).astype(np.float32)
    out = C.add_sensor_noise_rgb(np.clip(out, 0.0, 1.0), rng)
    return out


def _emit(hr_tile, lr_tile, name, in_dir, tgt_dir):
    """MAD-normalize with the LR's per-channel stats (matches inference, where
    only the LR is available), then write the (LR input, HR target) pair."""
    stats = [C.median_mad(lr_tile[c]) for c in range(lr_tile.shape[0])]
    x, _ = C.normalize_rgb(lr_tile, stats=stats, clip=CLIP)
    y, _ = C.normalize_rgb(hr_tile, stats=stats, clip=None)
    C.save_pair_tile(in_dir, tgt_dir, name, x, y)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", default="data/own/raw")
    ap.add_argument("--out", default="data/own")
    ap.add_argument("--hr-dir", default="denoised",
                    help="subfolder of --root to use as the clean HR target")
    ap.add_argument("--scale", type=int, default=2, choices=[2, 3, 4])
    ap.add_argument("--tile", type=int, default=256, help="HR tile size")
    ap.add_argument("--stride", type=int, default=192)
    ap.add_argument("--val-name", default="", help="basename held out (default: last)")
    ap.add_argument("--seed", type=int, default=0)
    args = ap.parse_args()

    hr_dir = os.path.join(args.root, args.hr_dir)
    in_dir = os.path.join(args.out, "upscale_tiles", "input")
    tgt_dir = os.path.join(args.out, "upscale_tiles", "target")
    vin_dir = os.path.join(args.out, "upscale_val", "input")
    vtgt_dir = os.path.join(args.out, "upscale_val", "target")
    rng = np.random.default_rng(args.seed)

    srcs = sorted(glob.glob(os.path.join(hr_dir, "*.fit*")))
    if not srcs:
        raise SystemExit(f"no HR FITS under {hr_dir}")
    val_name = args.val_name or C.basename_no_ext(srcs[-1])
    # snap HR tile size to a multiple of scale so the LR is an integer size
    tile = (args.tile // args.scale) * args.scale

    total = 0
    for path in srcs:
        name = C.basename_no_ext(path)
        hr = C.load_fits_rgb(path)
        lum = C.to_luminance(hr)
        is_val = (name == val_name)
        idir, tdir = (vin_dir, vtgt_dir) if is_val else (in_dir, tgt_dir)
        n = 0
        for (y0, x0) in C.tile_coords(hr.shape[1], hr.shape[2], tile, args.stride):
            hr_tile = hr[:, y0:y0 + tile, x0:x0 + tile]
            if hr_tile.shape[1:] != (tile, tile):
                continue
            if C.is_mostly_empty(lum[y0:y0 + tile, x0:x0 + tile]):
                continue
            lr_tile = downscale_rgb(hr_tile, args.scale, rng)
            _emit(hr_tile, lr_tile, f"{name}_{y0:05d}_{x0:05d}", idir, tdir)
            n += 1
        total += 0 if is_val else n
        print(f"{name}: {'VAL' if is_val else 'train'} {n} pairs (train total {total})")

    print(f"done: {total} upscale x{args.scale} train pairs -> {in_dir} (val: {val_name})")


if __name__ == "__main__":
    main()
