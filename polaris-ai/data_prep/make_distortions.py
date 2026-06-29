"""Build DECONVOLUTION training data by distorting the *sharp* image's stars.

Ground truth = the user's deconvolved FITS (``data/own/raw/decon``, sharp). We
emit **sharp luminance tiles** that the existing ``DeconDataset`` turns into
(blurred+sigma, sharp) pairs on the fly via the ``synth.py`` Moffat-PSF forward
model -- so the sigma condition channel the Polaris decon slider drives stays
exact, and we reuse the proven decon training path. A few full-frame *distorted*
previews (aberrated PSF: elliptical core + spikes + halo, via
``psf.make_aberrated_psf``) are written to ``originals+distortions`` for
inspection.

  python data_prep/make_distortions.py --previews 3

Outputs:
  data/own/raw/originals+distortions/<name>.fit   (distorted luminance previews)
  data/own/decon_tiles/*.npy                       (sharp luminance tiles -> DeconDataset)
  data/own/decon_tiles_val/*.npy                   (held-out image's sharp tiles)
"""
from __future__ import annotations

import argparse
import glob
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C  # noqa: E402
import synth  # noqa: E402  (root on path via common)
import psf  # noqa: E402


def _emit_sharp_tiles(lum, name, out_dir, tile, stride):
    n = 0
    os.makedirs(out_dir, exist_ok=True)
    h, w = lum.shape
    for (y0, x0) in C.tile_coords(h, w, tile, stride):
        t = lum[y0:y0 + tile, x0:x0 + tile]
        if t.shape != (tile, tile) or C.is_mostly_empty(t):
            continue
        np.save(os.path.join(out_dir, f"{name}_{y0:05d}_{x0:05d}.npy"),
                np.ascontiguousarray(t).astype(np.float32))
        n += 1
    return n


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", default="data/own/raw")
    ap.add_argument("--out", default="data/own")
    ap.add_argument("--tile", type=int, default=256)
    ap.add_argument("--stride", type=int, default=192)
    ap.add_argument("--previews", type=int, default=3,
                    help="how many full-frame distorted previews to write")
    ap.add_argument("--val-name", default="",
                    help="basename to hold out for validation (default: last)")
    ap.add_argument("--seed", type=int, default=0)
    args = ap.parse_args()

    sharp_dir = os.path.join(args.root, "decon")
    preview_dir = os.path.join(args.root, "originals+distortions")
    train_dir = os.path.join(args.out, "decon_tiles")
    val_dir = os.path.join(args.out, "decon_tiles_val")
    rng = np.random.default_rng(args.seed)

    sharps = sorted(glob.glob(os.path.join(sharp_dir, "*.fit*")))
    if not sharps:
        raise SystemExit(f"no decon FITS under {sharp_dir}")
    val_name = args.val_name or C.basename_no_ext(sharps[-1])

    total = 0
    for i, path in enumerate(sharps):
        name = C.basename_no_ext(path)
        lum = C.to_luminance(C.load_fits_rgb(path))
        out_dir = val_dir if name == val_name else train_dir
        n = _emit_sharp_tiles(lum, name, out_dir, args.tile, args.stride)
        total += 0 if out_dir is val_dir else n

        if i < args.previews:
            fwhm = synth.sample_fwhm(rng)
            beta = float(rng.uniform(2.2, 4.5))
            kernel = psf.make_aberrated_psf(rng, fwhm, beta=beta)
            distorted = synth.degrade_with_kernel(
                lum, kernel, rng=rng,
                read_noise_e=float(rng.uniform(2.0, 12.0)),
                full_well_scale=float(rng.uniform(20000.0, 80000.0)))
            C.save_fits_rgb(os.path.join(preview_dir, f"{name}.fit"), distorted)
        print(f"{name}: {'VAL' if out_dir is val_dir else 'train'} tiles done "
              f"(train total {total})")

    print(f"done: {total} sharp decon tiles -> {train_dir} (val: {val_name})")
    print("train with: python train.py --tiles data/own/decon_tiles "
          "--out checkpoints/decon")


if __name__ == "__main__":
    main()
