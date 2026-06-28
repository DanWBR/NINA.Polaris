"""Visual sanity check for a trained/exported deconvolution model.

Takes a sharp tile (random from a folder, a given .npy/.fits, or a synthetic
star field), blurs it with a known Moffat PSF, runs the ONNX model, and writes a
side-by-side PNG: sharp | blurred (input) | deconvolved (output). Prints PSNR of
input-vs-sharp and output-vs-sharp so you get a number, not just a vibe.

  python infer.py --onnx models/decon_fp32_256.onnx --fwhm 3.5 --out check.png
  python infer.py --onnx models/decon_fp16_256.onnx --tile data/tiles/synth_000001.npy
"""
from __future__ import annotations

import argparse
import glob
import os

import numpy as np

import synth


def _load_sharp(args, rng) -> np.ndarray:
    if args.tile:
        a = np.load(args.tile) if args.tile.endswith(".npy") else None
        if a is None:
            from dataset import _load_tile  # reuse the loader for fits/png
            a = _load_tile(args.tile)
    elif args.tiles:
        paths = sorted(glob.glob(os.path.join(args.tiles, "*.npy")))
        if not paths:
            raise FileNotFoundError(f"no .npy tiles under {args.tiles}")
        a = np.load(paths[int(rng.integers(0, len(paths)))])
    else:
        from download import synth_tile
        a = synth_tile(args.size, rng)
    a = np.asarray(a, dtype=np.float32)
    # center-crop / pad to size
    s = args.size
    h, w = a.shape
    if h < s or w < s:
        a = np.pad(a, ((0, max(0, s - h)), (0, max(0, s - w))), mode="reflect")
        h, w = a.shape
    y0, x0 = (h - s) // 2, (w - s) // 2
    return np.ascontiguousarray(a[y0:y0 + s, x0:x0 + s])


def _psnr(a, b):
    mse = float(np.mean((a - b) ** 2))
    return 99.0 if mse <= 1e-12 else 10.0 * np.log10(1.0 / mse)


def _star_fwhm(img, n=60, win=6):
    """Approximate mean stellar FWHM (px) from the brightest stars: find local
    maxima, then an intensity-weighted second-moment radius per star. Relative
    before/after is what matters (lower = tighter = sharper)."""
    h, w = img.shape
    thr = np.percentile(img, 99.5)
    # local maxima above threshold (3x3)
    ys, xs = np.where(img > thr)
    cand = []
    for y, x in zip(ys, xs):
        if y < win or x < win or y >= h - win or x >= w - win:
            continue
        p = img[y, x]
        if p >= img[y - 1:y + 2, x - 1:x + 2].max():
            cand.append((p, y, x))
    cand.sort(reverse=True)
    fwhms = []
    used = []
    for p, y, x in cand:
        if any(abs(y - yy) < win and abs(x - xx) < win for yy, xx in used):
            continue
        used.append((y, x))
        patch = img[y - win:y + win + 1, x - win:x + win + 1].astype(np.float64)
        patch = np.clip(patch - np.median(patch), 0, None)
        s = patch.sum()
        if s <= 0:
            continue
        ax = np.arange(-win, win + 1)
        xx, yy = np.meshgrid(ax, ax)
        r2 = (patch * (xx ** 2 + yy ** 2)).sum() / s
        sigma = np.sqrt(max(r2, 1e-6) / 2.0)        # 2D second moment -> per-axis sigma
        fwhms.append(2.3548 * sigma)
        if len(fwhms) >= n:
            break
    return (float(np.median(fwhms)) if fwhms else float("nan")), len(fwhms)


def _stretch(a):
    """Asinh display stretch so faint structure + stars are both visible."""
    lo, hi = np.percentile(a, 5.0), np.percentile(a, 99.8)
    x = np.clip((a - lo) / max(1e-6, hi - lo), 0, 1)
    x = np.arcsinh(10.0 * x) / np.arcsinh(10.0)
    return (x * 255).astype(np.uint8)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--onnx", required=True)
    ap.add_argument("--tile", default="", help="a specific .npy/.fits/.png sharp tile")
    ap.add_argument("--tiles", default="", help="folder to pick a random .npy from")
    ap.add_argument("--fwhm", type=float, default=3.5, help="PSF FWHM to apply + ask the model to undo")
    ap.add_argument("--size", type=int, default=256)
    ap.add_argument("--out", default="infer_check.png")
    ap.add_argument("--seed", type=int, default=0)
    ap.add_argument("--no-degrade", action="store_true",
                    help="run on the tile AS-IS (real image; no synthetic blur, no PSNR)")
    args = ap.parse_args()

    import onnxruntime as ort
    from PIL import Image

    rng = np.random.default_rng(args.seed)
    sharp = _load_sharp(args, rng)
    sess = ort.InferenceSession(args.onnx, providers=["CPUExecutionProvider"])
    iname = sess.get_inputs()[0].name

    if args.no_degrade:
        # REAL-IMAGE test: feed the tile straight in (it's already seeing-blurred);
        # the model deconvolves it. No ground truth -> visual comparison only.
        img = np.clip(sharp, 0.0, 1.0).astype(np.float32)
        cond = np.full_like(img, synth.condition_value(args.fwhm), dtype=np.float32)
        x = np.stack([img, cond], axis=0)[None, ...].astype(np.float32)
        out = sess.run(None, {iname: x})[0]
        decon = np.clip(out[0, 0], 0.0, 1.0)
        fin, nin = _star_fwhm(img)
        fout, nout = _star_fwhm(decon)
        print(f"mean star FWHM  input : {fin:.2f} px  ({nin} stars)")
        print(f"mean star FWHM  decon : {fout:.2f} px  ({nout} stars)  "
              f"(lower = tighter = sharper)")
        panel = np.concatenate([_stretch(img), _stretch(decon)], axis=1)
        Image.fromarray(panel).save(args.out)
        print(f"wrote {args.out}  (left: real input | right: deconvolved @ fwhm={args.fwhm})")
        return

    blurred = synth.degrade(sharp, args.fwhm, rng=rng)
    cond = np.full_like(sharp, synth.condition_value(args.fwhm), dtype=np.float32)
    x = np.stack([blurred, cond], axis=0)[None, ...].astype(np.float32)  # [1,2,H,W]
    out = sess.run(None, {iname: x})[0]
    decon = np.clip(out[0, 0], 0.0, 1.0)

    print(f"PSNR  blurred vs sharp : {_psnr(blurred, sharp):.2f} dB")
    print(f"PSNR  decon   vs sharp : {_psnr(decon, sharp):.2f} dB  "
          f"(higher than the line above = the model helped)")

    panel = np.concatenate([_stretch(sharp), _stretch(blurred), _stretch(decon)], axis=1)
    Image.fromarray(panel).save(args.out)
    print(f"wrote {args.out}  (left: sharp | middle: blurred input | right: deconvolved)")


if __name__ == "__main__":
    main()
