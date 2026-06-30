#!/usr/bin/env python
"""Quick-look a training tile (.npy) as a PNG.

The dataset tiles are raw NumPy arrays in the MAD-normalized domain (roughly
+/-10, not 0..1), so they can't be opened in an image viewer directly. This
applies a robust percentile stretch and writes a PNG you can download/eyeball.

Usage:
  python peek_npy.py tile.npy                       # -> tile.png
  python peek_npy.py input/x.npy target/x.npy       # each -> .png
  python peek_npy.py input/x.npy target/x.npy -o cmp.png --montage
      # side-by-side strip (great for input-vs-target / noisy-vs-clean checks)

Shapes handled: [3,H,W], [H,W,3], [2,H,W] (decon: image+sigma -> image only),
[H,W]. Output is 8-bit; mono is written as grayscale, 3-ch as RGB.
"""
import argparse
import os
import numpy as np


def to_hwc(a):
    a = np.asarray(a, dtype=np.float32)
    if a.ndim == 2:
        return a[..., None]                 # [H,W] -> [H,W,1]
    if a.ndim == 3:
        # decon stores [2,H,W] = image + sigma plane; keep the image only
        if a.shape[0] in (1, 2, 3) and a.shape[0] < a.shape[-1]:
            if a.shape[0] == 2:
                a = a[:1]
            return np.transpose(a, (1, 2, 0))   # CHW -> HWC
        return a                                  # already HWC
    raise ValueError(f"unsupported shape {a.shape}")


def stretch(img, lo=0.5, hi=99.5):
    """Per-image percentile stretch to 0..255 uint8."""
    out = np.empty_like(img, dtype=np.float32)
    for c in range(img.shape[-1]):
        ch = img[..., c]
        a, b = np.percentile(ch, [lo, hi])
        if b - a < 1e-9:
            b = a + 1e-9
        out[..., c] = np.clip((ch - a) / (b - a), 0, 1)
    return (out * 255.0 + 0.5).astype(np.uint8)


def save_png(arr8, path):
    try:
        from PIL import Image
    except ImportError:
        raise SystemExit("Pillow needed: pip install pillow")
    a = arr8[..., 0] if arr8.shape[-1] == 1 else arr8[..., :3]
    Image.fromarray(a).save(path)
    print(f"wrote {path}  ({a.shape})")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("npy", nargs="+", help="one or more .npy tiles")
    ap.add_argument("-o", "--out", help="output PNG (montage or single)")
    ap.add_argument("--montage", action="store_true",
                    help="concatenate inputs horizontally into one PNG")
    ap.add_argument("--lo", type=float, default=0.5)
    ap.add_argument("--hi", type=float, default=99.5)
    args = ap.parse_args()

    imgs = [stretch(to_hwc(np.load(p)), args.lo, args.hi) for p in args.npy]

    if args.montage:
        h = min(im.shape[0] for im in imgs)
        chans = max(im.shape[-1] for im in imgs)
        cols = []
        for im in imgs:
            im = im[:h]
            if im.shape[-1] == 1 and chans == 3:
                im = np.repeat(im, 3, axis=-1)
            cols.append(im)
            cols.append(np.zeros((h, 4, chans), np.uint8))  # 4px separator
        strip = np.concatenate(cols[:-1], axis=1)
        save_png(strip, args.out or "montage.png")
    else:
        for p in args.npy:
            out = args.out if (args.out and len(args.npy) == 1) \
                else os.path.splitext(p)[0] + ".png"
            save_png(stretch(to_hwc(np.load(p)), args.lo, args.hi), out)


if __name__ == "__main__":
    main()
