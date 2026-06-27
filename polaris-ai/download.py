"""Data preparation for polaris-ai.

Two sources of *sharp* ground truth tiles (saved as float32 .npy in ~[0,1]):

  synth   -- fully synthetic star fields + Sersic galaxies. PERFECT ground truth
             (the stars are literal point sources before any PSF), so this is the
             backbone of the dataset.
  skyview -- diffraction-limited / survey cutouts via astroquery.SkyView (DSS,
             etc). Adds realistic extended structure. NOTE: survey images are
             themselves seeing-limited, so treat them as "objects" examples, not
             as truly sharp point sources.

Usage:
  python download.py synth   --out data/tiles --count 4000 --size 256
  python download.py skyview --out data/tiles --survey "DSS2 Red" --count 500 --size 256
"""
from __future__ import annotations

import argparse
import os

import numpy as np


# --------------------------------------------------------------------------- #
# Synthetic star fields + galaxies (perfect sharp ground truth)
# --------------------------------------------------------------------------- #
def _sersic(size, cx, cy, amp, re, n, ell, theta, rng):
    yy, xx = np.mgrid[0:size, 0:size].astype(np.float32)
    xr = (xx - cx) * np.cos(theta) + (yy - cy) * np.sin(theta)
    yr = -(xx - cx) * np.sin(theta) + (yy - cy) * np.cos(theta)
    r = np.sqrt(xr ** 2 + (yr / max(1e-3, 1 - ell)) ** 2) + 1e-3
    bn = 2.0 * n - 0.327
    return amp * np.exp(-bn * ((r / re) ** (1.0 / n) - 1.0))


def synth_tile(size: int, rng: np.random.Generator) -> np.ndarray:
    img = np.zeros((size, size), dtype=np.float32)

    # faint background gradient
    gy, gx = np.mgrid[0:size, 0:size].astype(np.float32) / size
    img += rng.uniform(0.0, 0.05) * (gx * rng.uniform(-1, 1) + gy * rng.uniform(-1, 1))

    # a few extended objects
    for _ in range(int(rng.integers(0, 3))):
        img += _sersic(
            size, rng.uniform(0, size), rng.uniform(0, size),
            amp=rng.uniform(0.05, 0.4), re=rng.uniform(size * 0.05, size * 0.25),
            n=rng.uniform(0.7, 4.0), ell=rng.uniform(0.0, 0.6),
            theta=rng.uniform(0, np.pi), rng=rng,
        )

    # stars as sub-pixel point sources (the true sharp signal)
    nstars = int(rng.integers(30, 400))
    xs = rng.uniform(0, size - 1, nstars)
    ys = rng.uniform(0, size - 1, nstars)
    # magnitude-like distribution: many faint, few bright
    mags = rng.power(0.6, nstars)
    for x, y, m in zip(xs, ys, mags):
        ix, iy = int(x), int(y)
        fx, fy = x - ix, y - iy
        amp = (0.02 + 0.98 * m) * rng.uniform(0.5, 1.0)
        # bilinear splat -> sub-pixel point source
        img[iy, ix] += amp * (1 - fx) * (1 - fy)
        if ix + 1 < size: img[iy, ix + 1] += amp * fx * (1 - fy)
        if iy + 1 < size: img[iy + 1, ix] += amp * (1 - fx) * fy
        if ix + 1 < size and iy + 1 < size: img[iy + 1, ix + 1] += amp * fx * fy

    img = np.clip(img, 0.0, None)
    mx = img.max()
    if mx > 0:
        img /= mx
    return img.astype(np.float32)


def cmd_synth(args):
    os.makedirs(args.out, exist_ok=True)
    rng = np.random.default_rng(args.seed)
    for i in range(args.count):
        np.save(os.path.join(args.out, f"synth_{i:06d}.npy"),
                synth_tile(args.size, rng))
        if (i + 1) % 500 == 0:
            print(f"  {i + 1}/{args.count}")
    print(f"wrote {args.count} synthetic tiles -> {args.out}")


# --------------------------------------------------------------------------- #
# Real survey cutouts via astroquery SkyView
# --------------------------------------------------------------------------- #
def cmd_skyview(args):
    from astropy.coordinates import SkyCoord
    from astropy import units as u
    from astroquery.skyview import SkyView

    os.makedirs(args.out, exist_ok=True)
    rng = np.random.default_rng(args.seed)
    written = 0
    attempts = 0
    px = args.size
    while written < args.count and attempts < args.count * 4:
        attempts += 1
        # random galactic-off-plane field to avoid crowding/saturation
        ra = float(rng.uniform(0, 360))
        dec = float(rng.uniform(-40, 70))
        try:
            imgs = SkyView.get_images(
                position=SkyCoord(ra, dec, unit="deg"),
                survey=[args.survey], pixels=str(px),
                width=args.fov * u.arcmin, height=args.fov * u.arcmin,
            )
        except Exception as e:  # noqa: BLE001 -- network/coverage gaps are expected
            continue
        if not imgs:
            continue
        data = np.asarray(imgs[0][0].data, dtype=np.float32)
        if data.size == 0 or not np.isfinite(data).any():
            continue
        data = np.nan_to_num(data)
        lo, hi = np.percentile(data, 1.0), np.percentile(data, 99.9)
        if hi <= lo:
            continue
        norm = np.clip((data - lo) / (hi - lo), 0.0, 1.0).astype(np.float32)
        np.save(os.path.join(args.out, f"sky_{written:06d}.npy"), norm)
        written += 1
        if written % 50 == 0:
            print(f"  {written}/{args.count}")
    print(f"wrote {written} survey tiles -> {args.out} ({attempts} attempts)")


def main():
    ap = argparse.ArgumentParser(description="polaris-ai data prep")
    sub = ap.add_subparsers(dest="cmd", required=True)

    s = sub.add_parser("synth", help="generate synthetic star-field tiles")
    s.add_argument("--out", required=True)
    s.add_argument("--count", type=int, default=4000)
    s.add_argument("--size", type=int, default=256)
    s.add_argument("--seed", type=int, default=0)
    s.set_defaults(func=cmd_synth)

    k = sub.add_parser("skyview", help="download survey cutouts via SkyView")
    k.add_argument("--out", required=True)
    k.add_argument("--survey", default="DSS2 Red")
    k.add_argument("--count", type=int, default=500)
    k.add_argument("--size", type=int, default=256)
    k.add_argument("--fov", type=float, default=10.0, help="field of view in arcmin")
    k.add_argument("--seed", type=int, default=1)
    k.set_defaults(func=cmd_skyview)

    args = ap.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
