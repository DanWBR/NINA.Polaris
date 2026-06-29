"""Build STAR-HALO-REMOVAL training pairs by adding synthetic halos to the clean
image.

Ground truth (target) = the clean image (default ``data/own/raw/denoised``).
Input = that image + synthetic reflection-style halos (broad glows / thin rings)
of varying size, intensity and colour around the brightest stars. The model
learns to subtract the halo while leaving stars and background intact.

Pairs are emitted RGB in the per-channel MAD-normalized domain (same as the other
RGB tasks), LR=haloed input, target=clean.

  python data_prep/make_halos.py --per-image 4 --clean-dir denoised

Outputs:
  data/own/raw/originals+halos/<name>.fit       (one haloed full frame, preview)
  data/own/halo_tiles/{input,target}/*.npy      (synthetic train pairs, [3,256,256])
  data/own/halo_val/{input,target}/*.npy        (held-out image, synthetic)
"""
from __future__ import annotations

import argparse
import glob
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C  # noqa: E402

CLIP = 10.0


def _emit_pairs(haloed, clean, name, in_dir, tgt_dir, tile, stride):
    stats = [C.median_mad(haloed[c]) for c in range(haloed.shape[0])]
    hin, _ = C.normalize_rgb(haloed, stats=stats, clip=CLIP)
    tgt, _ = C.normalize_rgb(clean, stats=stats, clip=None)
    lum = C.to_luminance(clean)
    n = 0
    for (y0, x0) in C.tile_coords(haloed.shape[1], haloed.shape[2], tile, stride):
        if C.is_mostly_empty(lum[y0:y0 + tile, x0:x0 + tile]):
            continue
        x = hin[:, y0:y0 + tile, x0:x0 + tile]
        y = tgt[:, y0:y0 + tile, x0:x0 + tile]
        if x.shape[1:] != (tile, tile):
            continue
        C.save_pair_tile(in_dir, tgt_dir, f"{name}_{y0:05d}_{x0:05d}", x, y)
        n += 1
    return n


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", default="data/own/raw")
    ap.add_argument("--out", default="data/own")
    ap.add_argument("--clean-dir", default="denoised",
                    help="subfolder of --root used as the clean (halo-free) target")
    ap.add_argument("--per-image", type=int, default=4,
                    help="independent halo draws per clean image")
    ap.add_argument("--tile", type=int, default=256)
    ap.add_argument("--stride", type=int, default=192)
    ap.add_argument("--val-name", default="", help="basename held out (default: last)")
    ap.add_argument("--seed", type=int, default=0)
    args = ap.parse_args()

    clean_dir = os.path.join(args.root, args.clean_dir)
    preview_dir = os.path.join(args.root, "originals+halos")
    in_dir = os.path.join(args.out, "halo_tiles", "input")
    tgt_dir = os.path.join(args.out, "halo_tiles", "target")
    vin_dir = os.path.join(args.out, "halo_val", "input")
    vtgt_dir = os.path.join(args.out, "halo_val", "target")
    rng = np.random.default_rng(args.seed)

    cleans = sorted(glob.glob(os.path.join(clean_dir, "*.fit*")))
    if not cleans:
        raise SystemExit(f"no clean FITS under {clean_dir}")
    val_name = args.val_name or C.basename_no_ext(cleans[-1])

    total = 0
    for path in cleans:
        name = C.basename_no_ext(path)
        clean = C.load_fits_rgb(path)
        is_val = (name == val_name)
        idir, tdir = (vin_dir, vtgt_dir) if is_val else (in_dir, tgt_dir)
        draws = 1 if is_val else args.per_image
        for k in range(draws):
            haloed = C.add_star_halos_rgb(clean, rng)
            if k == 0 and not is_val:
                C.save_fits_rgb(os.path.join(preview_dir, f"{name}.fit"), haloed)
            n = _emit_pairs(haloed, clean, f"{name}_k{k}", idir, tdir,
                            args.tile, args.stride)
            total += 0 if is_val else n
        print(f"{name}: {'VAL' if is_val else 'train'} done (train total {total})")

    print(f"done: {total} halo train tile pairs -> {in_dir} (val: {val_name})")


if __name__ == "__main__":
    main()
