"""Repair the one-sided colour fringe (blue/magenta block) on bright star cores in
an OSC FITS -- the SVBony debayer/CFA artifact -- by enforcing RADIAL COLOUR
SYMMETRY per star.

Idea (user's): a star should be radially symmetric in colour; the fringe is on
ONE side only. So for each radius ring around the star, take the MEDIAN colour
over the ring (the 3 clean sides dominate, the bad side is an outlier) and
rebuild every pixel's colour from that ring-median while keeping its original
luminance. The bad edge inherits the colour of the other edges; the luminance
profile (star shape/brightness) is untouched. Simple, and it works where a
per-pixel SCNR cap didn't.

  python fix_star_fringe.py --src aligned.fit --out repaired.fit --preview chk.png
"""
from __future__ import annotations

import argparse
import numpy as np
from astropy.io import fits
from scipy import ndimage


def bright_stars(lum, thr_frac=0.20, sep=24, nmax=4000):
    lsm = ndimage.gaussian_filter(lum, 1.0)
    mx = ndimage.maximum_filter(lsm, size=9)
    H, W = lum.shape
    peaks = (lsm == mx) & (lsm > thr_frac * lsm.max())
    ys, xs = np.where(peaks)
    order = np.argsort(lsm[ys, xs])[::-1]
    out, used = [], []
    for o in order:
        y, x = int(ys[o]), int(xs[o])
        if any(abs(y - yy) < sep and abs(x - xx) < sep for yy, xx in used):
            continue
        used.append((y, x)); out.append((y, x))
        if len(out) >= nmax:
            break
    return out


def repair_symmetry(crop, win):
    """crop: (2win,2win,3). Return colour-symmetrised crop (luminance preserved)."""
    R, G, B = crop[..., 0], crop[..., 1], crop[..., 2]
    L = (R + G + B) / 3.0 + 1e-6
    yy, xx = np.mgrid[-win:win, -win:win].astype(np.float64)
    Lb = np.clip(L - np.median(L), 0, None)
    s = Lb.sum() + 1e-9
    cx = (Lb * xx).sum() / s          # sub-pixel centre
    cy = (Lb * yy).sum() / s
    r = np.sqrt((xx - cx) ** 2 + (yy - cy) ** 2)
    ri = np.clip(np.round(r).astype(int), 0, None)
    rmax = int(ri.max())

    def medprof(ratio):
        m = np.zeros(rmax + 1)
        flat = ratio.ravel(); idx = ri.ravel()
        for rr in range(rmax + 1):
            sel = flat[idx == rr]
            if sel.size:
                m[rr] = np.median(sel)
        return m

    mR, mG, mB = medprof(R / L), medprof(G / L), medprof(B / L)
    sym = np.stack([L * mR[ri], L * mG[ri], L * mB[ri]], axis=-1)
    # feather so the patch blends into the surrounding pixels (no seam)
    w = np.clip((win * 0.85 - r) / (win * 0.2), 0, 1)[..., None]
    return crop * (1 - w) + sym * w


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--src", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--preview", default="")
    ap.add_argument("--win", type=int, default=22, help="half-window per star (px)")
    ap.add_argument("--thr", type=float, default=0.20, help="bright-star peak frac of max")
    args = ap.parse_args()

    with fits.open(args.src, memmap=False) as h:
        data = h[0].data.astype(np.float32); hdr = h[0].header
    rgb = (np.transpose(data, (1, 2, 0)) if data.shape[0] == 3 else data[..., :3]).astype(np.float64)
    H, W = rgb.shape[:2]
    out = rgb.copy()
    win = args.win

    stars = bright_stars(rgb.mean(2), thr_frac=args.thr)
    inb = [(y, x) for (y, x) in stars if win <= y < H - win and win <= x < W - win]
    print(f"repairing {len(inb)} bright stars (radial colour symmetry, win={win})")
    for (y, x) in inb:
        out[y-win:y+win, x-win:x+win] = repair_symmetry(out[y-win:y+win, x-win:x+win], win)

    fits.writeto(args.out, np.transpose(out, (2, 0, 1)).astype(np.float32),
                 header=hdr, overwrite=True)
    print("wrote", args.out)

    if args.preview and inb:
        from PIL import Image
        y, x = inb[0]
        def hard(a):
            bg = np.percentile(a, 50)
            z = np.clip((a - bg) / max(1e-6, np.percentile(a, 99.99) - bg), 0, 1)
            return (np.arcsinh(10000 * z) / np.arcsinh(10000) * 255).astype(np.uint8)
        b = hard(rgb[y-win:y+win, x-win:x+win]); a = hard(out[y-win:y+win, x-win:x+win])
        gap = np.zeros((b.shape[0], 4, 3), np.uint8)
        Image.fromarray(np.kron(np.concatenate([b, gap, a], 1), np.ones((6, 6, 1), np.uint8))).save(args.preview)
        print(f"wrote {args.preview}  (left: before | right: after)")


if __name__ == "__main__":
    main()
