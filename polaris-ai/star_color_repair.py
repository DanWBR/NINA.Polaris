#!/usr/bin/env python3
"""
star_color_repair.py — fix the one-sided blue/magenta + dark colour fringe that
OSC cameras (notably SVBony SV605CC / SV405CC) leave on bright stars from their
debayer/CFA, which plain channel alignment can't remove.

Standalone, single file. Part of N.I.N.A. Polaris (https://github.com/DanWBR/NINA.Polaris)
but free to use/modify/redistribute on its own under the MIT terms below.

How it works (pure math, no model):
  1. CHANNEL ALIGN — measure the median per-channel sub-pixel offset over many
     bright stars and shift R/B onto G. Fixes the field-wide lateral colour
     shift (atmospheric dispersion + CFA).
  2. RADIAL STAR SYMMETRY (neighbour-aware) — a star is radially symmetric, the
     fringe is on ONE side. Per bright star, per radius ring, take the MEDIAN
     colour (the clean sides dominate) and rebuild each pixel's colour from it;
     FILL the dark side up to the per-ring median luminance. Neighbour stars are
     masked out of the medians and left untouched, so close pairs are handled.

Usage:
    pip install numpy scipy astropy pillow
    python star_color_repair.py input.fit -o output.fit
    python star_color_repair.py input.fit -o output.fit --aggressiveness 1.0 \
        --neighbor-radius 9 --preview check.png

Input must be a 3-channel (colour/OSC) FITS. The original is never modified.

----------------------------------------------------------------------------
MIT License — Copyright (c) 2026 Daniel Wagner (DanWBR)
Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
of the Software, and to permit persons to whom the Software is furnished to do
so, subject to including the above copyright notice. THE SOFTWARE IS PROVIDED
"AS IS", WITHOUT WARRANTY OF ANY KIND.
----------------------------------------------------------------------------
"""
from __future__ import annotations

import argparse
import numpy as np
from astropy.io import fits
from scipy import ndimage


# --------------------------------------------------------------------------- #
# I/O
# --------------------------------------------------------------------------- #
def load_rgb(path):
    """Return (H,W,3) float64 RGB and the primary header."""
    with fits.open(path, memmap=False) as h:
        data = np.asarray(h[0].data, dtype=np.float64)
        hdr = h[0].header
    if data.ndim != 3 or 3 not in data.shape:
        raise SystemExit(f"Not a 3-channel colour FITS (shape {data.shape}).")
    rgb = np.transpose(data, (1, 2, 0)) if data.shape[0] == 3 else data[..., :3]
    return np.ascontiguousarray(rgb), hdr


def save_rgb(path, rgb, hdr):
    """Write (H,W,3) back as a plane-sequential (3,H,W) FITS, header preserved."""
    out = np.transpose(rgb, (2, 0, 1)).astype(np.float32)
    fits.writeto(path, out, header=hdr, overwrite=True)


# --------------------------------------------------------------------------- #
# Star detection
# --------------------------------------------------------------------------- #
def detect_stars(lum, thr_frac=0.15, sep=24, nmax=4000):
    lsm = ndimage.gaussian_filter(lum, 1.0)
    mx = ndimage.maximum_filter(lsm, size=9)
    H, W = lum.shape
    peaks = (lsm == mx) & (lsm > thr_frac * lsm.max())
    ys, xs = np.where(peaks)
    order = np.argsort(lsm[ys, xs])[::-1]            # brightest first
    out, used = [], []
    M = 16
    for o in order:
        y, x = int(ys[o]), int(xs[o])
        if y < M or x < M or y >= H - M or x >= W - M:
            continue
        if any(abs(y - uy) < sep and abs(x - ux) < sep for uy, ux in used):
            continue
        used.append((y, x)); out.append((x, y))
        if len(out) >= nmax:
            break
    return out                                       # list of (x, y)


# --------------------------------------------------------------------------- #
# Stage 1: per-channel lateral alignment
# --------------------------------------------------------------------------- #
def _centroid(p, cx, cy, win):
    sub = p[cy - win:cy + win + 1, cx - win:cx + win + 1]
    w = np.clip(sub - sub.min(), 0, None)
    s = w.sum()
    if s <= 0:
        return 0.0, 0.0
    ax = np.arange(-win, win + 1)
    xx, yy = np.meshgrid(ax, ax)
    return float((w * xx).sum() / s), float((w * yy).sum() / s)


def align_channels(R, G, B, stars, agg, win=12):
    H, W = R.shape
    dR, dB = [], []
    for (x, y) in stars:
        if x < win or y < win or x >= W - win or y >= H - win:
            continue
        gx, gy = _centroid(G, x, y, win)
        rx, ry = _centroid(R, x, y, win)
        bx, by = _centroid(B, x, y, win)
        dR.append((rx - gx, ry - gy))
        dB.append((bx - gx, by - gy))
    if len(dR) < 3:
        return R, B
    oR = np.median(np.array(dR), axis=0) * agg       # (dx, dy)
    oB = np.median(np.array(dB), axis=0) * agg
    # shift content by the negative offset to land R/B on G (ndimage uses (dy,dx))
    R = ndimage.shift(R, (-oR[1], -oR[0]), order=3, mode="nearest")
    B = ndimage.shift(B, (-oB[1], -oB[0]), order=3, mode="nearest")
    return R, B


# --------------------------------------------------------------------------- #
# Stage 2: neighbour-aware radial colour + luminance symmetry
# --------------------------------------------------------------------------- #
def _ring_median(vals, ri, rmax, keep):
    """median per integer radius over kept pixels; valid[r] False if <3 samples."""
    med = np.zeros(rmax + 1)
    ok = np.zeros(rmax + 1, dtype=bool)
    rk = ri[keep]; vk = vals[keep]
    for r in range(rmax + 1):
        sel = vk[rk == r]
        if sel.size >= 3:
            med[r] = np.median(sel); ok[r] = True
    return med, ok


def repair_star(R, G, B, cx, cy, win, agg, all_stars, excl):
    H, W = R.shape
    if cx < win or cy < win or cx >= W - win or cy >= H - win:
        return
    n = 2 * win + 1
    ax = np.arange(-win, win + 1)
    xx, yy = np.meshgrid(ax, ax)                      # local coords, (n,n)

    # neighbour mask (pixels close to ANY other star in the window)
    keep = np.ones((n, n), dtype=bool)
    inc = win + excl
    for (sx, sy) in all_stars:
        if sx == cx and sy == cy:
            continue
        lx, ly = sx - cx, sy - cy
        if abs(lx) <= inc and abs(ly) <= inc:
            keep &= ((xx - lx) ** 2 + (yy - ly) ** 2) >= excl * excl

    lr = R[cy - win:cy + win + 1, cx - win:cx + win + 1]
    lg = G[cy - win:cy + win + 1, cx - win:cx + win + 1]
    lb = B[cy - win:cy + win + 1, cx - win:cx + win + 1]
    L = (lr + lg + lb) / 3.0 + 1e-6

    # sub-pixel centre from the core only (r<=6), neighbours excluded
    core = keep & ((xx ** 2 + yy ** 2) <= 36)
    w = np.clip(L - np.median(L), 0, None) * core
    s = w.sum()
    ccx = float((w * xx).sum() / s) if s > 0 else 0.0
    ccy = float((w * yy).sum() / s) if s > 0 else 0.0

    rad = np.sqrt((xx - ccx) ** 2 + (yy - ccy) ** 2)
    ri = np.round(rad).astype(int)
    rmax = int(ri.max())

    medL, okL = _ring_median(L, ri, rmax, keep)
    mRR, _ = _ring_median(lr / L, ri, rmax, keep)
    mRG, _ = _ring_median(lg / L, ri, rmax, keep)
    mRB, _ = _ring_median(lb / L, ri, rmax, keep)

    lm = medL[ri]
    companion = L > lm * 1.8 + 0.02 * 65535.0         # protect neighbour cores
    lout = np.maximum(L, lm)                          # fill the dark side
    symR = np.where(companion, lr, lout * mRR[ri])
    symG = np.where(companion, lg, lout * mRG[ri])
    symB = np.where(companion, lb, lout * mRB[ri])

    fw = np.clip((win * 0.85 - rad) / (win * 0.2), 0, 1) * agg
    valid = keep & okL[ri]                            # don't touch neighbours / weak rings
    fw = np.where(valid, fw, 0.0)

    R[cy - win:cy + win + 1, cx - win:cx + win + 1] = lr * (1 - fw) + symR * fw
    G[cy - win:cy + win + 1, cx - win:cx + win + 1] = lg * (1 - fw) + symG * fw
    B[cy - win:cy + win + 1, cx - win:cx + win + 1] = lb * (1 - fw) + symB * fw


# --------------------------------------------------------------------------- #
# Preview montage (brightest stars, before | after)
# --------------------------------------------------------------------------- #
def _stretch(a):
    lo, hi = np.percentile(a, 1), np.percentile(a, 99.9)
    z = np.clip((a - lo) / max(1e-6, hi - lo), 0, 1)
    return (np.arcsinh(10 * z) / np.arcsinh(10) * 255).astype(np.uint8)


def write_preview(orig, fixed, stars, path, n=8, cw=48):
    from PIL import Image
    H, W = orig.shape[:2]; half = cw // 2
    picks = [(x, y) for (x, y) in stars if half <= x < W - half and half <= y < H - half][:n]
    if not picks:
        return
    rows = []
    for (x, y) in picks:
        b = _stretch(orig[y - half:y + half, x - half:x + half])
        a = _stretch(fixed[y - half:y + half, x - half:x + half])
        gap = np.zeros((cw, 4, 3), np.uint8)
        rows.append(np.concatenate([b, gap, a], axis=1))
    sep = np.zeros((4, rows[0].shape[1], 3), np.uint8)
    panel = rows[0]
    for r in rows[1:]:
        panel = np.concatenate([panel, sep, r], axis=0)
    big = np.kron(panel, np.ones((3, 3, 1), np.uint8))
    Image.fromarray(big).save(path)
    print(f"wrote {path}  (each row: before | after)")


# --------------------------------------------------------------------------- #
def main():
    ap = argparse.ArgumentParser(description="Repair OSC bright-star colour/dark fringe (SVBony etc.)")
    ap.add_argument("src", help="input colour FITS")
    ap.add_argument("-o", "--out", required=True, help="output FITS")
    ap.add_argument("--aggressiveness", type=float, default=1.0, help="0..1 (default 1.0)")
    ap.add_argument("--neighbor-radius", type=float, default=9.0,
                    help="px; how close a neighbour star is masked off (default 9)")
    ap.add_argument("--win", type=int, default=22, help="per-star half-window px (default 22)")
    ap.add_argument("--no-align", action="store_true", help="skip channel alignment")
    ap.add_argument("--no-fringe", action="store_true", help="skip star fringe repair")
    ap.add_argument("--preview", default="", help="write a before/after PNG of the brightest stars")
    args = ap.parse_args()

    agg = float(np.clip(args.aggressiveness, 0.0, 1.0))
    excl = float(np.clip(args.neighbor_radius, 3.0, 20.0))

    rgb, hdr = load_rgb(args.src)
    orig = rgb.copy() if args.preview else None
    R, G, B = rgb[..., 0].copy(), rgb[..., 1].copy(), rgb[..., 2].copy()

    stars = detect_stars((R + G + B) / 3.0)
    print(f"{len(stars)} bright stars; aggressiveness={agg}, neighbor-radius={excl:.0f}px")

    if not args.no_align and agg > 0:
        R, B = align_channels(R, G, B, stars, agg)
    if not args.no_fringe and agg > 0:
        for (x, y) in stars:
            repair_star(R, G, B, x, y, args.win, agg, stars, excl)

    out = np.stack([np.clip(R, 0, 65535), np.clip(G, 0, 65535), np.clip(B, 0, 65535)], axis=-1)
    save_rgb(args.out, out, hdr)
    print(f"wrote {args.out}")
    if args.preview and orig is not None:
        write_preview(orig, out, stars, args.preview)


if __name__ == "__main__":
    main()
