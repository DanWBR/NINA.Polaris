"""Measure and correct lateral colour misregistration (per-channel offset) in an
OSC FITS -- the "blue fringe on one edge of every bright star" caused by
atmospheric dispersion (no ADC) or a per-channel stacking offset.

It finds many bright stars, measures each channel's sub-pixel centroid, takes the
median R-vs-G and B-vs-G shift, then shifts R and B onto G. Saves a corrected
FITS and (optionally) a before/after PNG of the brightest star.

  python fix_channel_align.py --src in.fit --out fixed.fit --preview check.png
"""
from __future__ import annotations

import argparse
import numpy as np
from astropy.io import fits
from scipy import ndimage


def _load_rgb(path):
    with fits.open(path, memmap=False) as h:
        data = h[0].data.astype(np.float32)
        hdr = h[0].header
    if data.ndim != 3 or 3 not in data.shape:
        raise SystemExit(f"not a 3-channel colour FITS (shape {data.shape})")
    rgb = np.transpose(data, (1, 2, 0)) if data.shape[0] == 3 else data[..., :3]
    return rgb, hdr


def _bright_stars(lum, n=80, thr_frac=0.25, sep=24):
    lsm = ndimage.gaussian_filter(lum, 1.0)
    mx = ndimage.maximum_filter(lsm, size=9)
    H, W = lum.shape
    peaks = (lsm == mx) & (lsm > thr_frac * lsm.max())
    ys, xs = np.where(peaks)
    order = np.argsort(lsm[ys, xs])[::-1]
    out, used = [], []
    for o in order:
        y, x = int(ys[o]), int(xs[o])
        if y < 16 or x < 16 or y > H - 16 or x > W - 16:
            continue
        if any(abs(y - yy) < sep and abs(x - xx) < sep for yy, xx in used):
            continue
        # reject hot pixels: core must be wider than 1 px
        if lum[y, x] > 0 and (lsm[y, x] / max(lum[y, x], 1e-6)) < 0.25:
            continue
        used.append((y, x))
        out.append((y, x))
        if len(out) >= n:
            break
    return out


def _centroid(ch, y, x, win=7):
    p = ch[y - win:y + win + 1, x - win:x + win + 1].astype(np.float64)
    p = np.clip(p - np.median(p), 0, None)
    s = p.sum()
    if s <= 0:
        return 0.0, 0.0
    ax = np.arange(-win, win + 1)
    xx, yy = np.meshgrid(ax, ax)
    return float((p * xx).sum() / s), float((p * yy).sum() / s)


def measure(rgb, stars):
    dR, dB = [], []
    for (y, x) in stars:
        gx, gy = _centroid(rgb[..., 1], y, x)
        rx, ry = _centroid(rgb[..., 0], y, x)
        bx, by = _centroid(rgb[..., 2], y, x)
        dR.append((rx - gx, ry - gy))
        dB.append((bx - gx, by - gy))
    dR = np.median(np.array(dR), axis=0)
    dB = np.median(np.array(dB), axis=0)
    return dR, dB     # (dx,dy) of R vs G, B vs G


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--src", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--preview", default="")
    args = ap.parse_args()

    rgb, hdr = _load_rgb(args.src)
    lum = rgb.mean(2)
    stars = _bright_stars(lum)
    print(f"using {len(stars)} bright stars")
    dR, dB = measure(rgb, stars)
    print(f"measured offset (dx,dy)  R vs G: {dR[0]:+.3f},{dR[1]:+.3f}  "
          f"B vs G: {dB[0]:+.3f},{dB[1]:+.3f}  px")

    # shift R and B onto G (ndimage.shift wants (dy,dx); move by the NEGATIVE)
    out = rgb.copy()
    out[..., 0] = ndimage.shift(rgb[..., 0], (-dR[1], -dR[0]), order=3, mode="nearest")
    out[..., 2] = ndimage.shift(rgb[..., 2], (-dB[1], -dB[0]), order=3, mode="nearest")

    res_R, res_B = measure(out, stars)
    print(f"residual after fix       R vs G: {res_R[0]:+.3f},{res_R[1]:+.3f}  "
          f"B vs G: {res_B[0]:+.3f},{res_B[1]:+.3f}  px  (should be ~0)")

    fits.writeto(args.out, np.transpose(out, (2, 0, 1)).astype(np.float32),
                 header=hdr, overwrite=True)
    print("wrote", args.out)

    if args.preview:
        from PIL import Image
        ys, xs = zip(*stars)
        # brightest star
        by, bx = stars[0]
        win = 45

        def stretch(a):
            lo, hi = np.percentile(a, 1), np.percentile(a, 99.99)
            z = np.clip((a - lo) / max(1e-6, hi - lo), 0, 1)
            return (np.arcsinh(10 * z) / np.arcsinh(10) * 255).astype(np.uint8)

        before = stretch(rgb[by - win:by + win, bx - win:bx + win])
        after = stretch(out[by - win:by + win, bx - win:bx + win])
        gap = np.zeros((before.shape[0], 6, 3), np.uint8)
        panel = np.concatenate([before, gap, after], axis=1)
        Image.fromarray(np.kron(panel, np.ones((4, 4, 1), np.uint8))).save(args.preview)
        print(f"wrote {args.preview}  (left: before | right: after)")


if __name__ == "__main__":
    main()
