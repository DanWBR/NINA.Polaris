"""Star-centric quality metrics for the decon/halo models.

PSNR/SSIM average over the whole tile and are dominated by background sky, so
they under-weight exactly what the Detail model is for (star tightening) and
what its known failure mode is (the dark "bubble" ring around saturated
stars). This tool measures, per detected star:

  * FWHM ratio      pred/target and pred/input (did it actually deconvolve,
                    without over-sharpening past the target)
  * dark-ring index max dip below local background in an annulus around the
                    star (0 = no ring; larger = worse bubble artifact)
  * flux ratio      aperture-sum pred/reference (photometric preservation)

Synthetic mode (default) reuses the same deterministic pairs as
eval_models.py (decon: synth.make_pair + log-norm; matching the training/
calibration domain). Real mode (--real) takes a directory of mono .npy tiles
(e.g. from make_holdout.py) with no ground truth and reports pred vs INPUT.

  python eval_star_metrics.py --task decon --models models --tiles-val data/own/decon_tiles_val
  python eval_star_metrics.py --task decon --models models --real data/holdout_real --sigma 0.5

numpy + scipy only, same dependency footprint as eval_models.py.
"""
from __future__ import annotations

import argparse
import datetime
import glob
import json
import os

import numpy as np


# --- star detection ---------------------------------------------------------

def detect_stars(img: np.ndarray, k_sigma: float = 8.0, exclude: int = 20,
                 max_stars: int = 40):
    """Local maxima above median + k_sigma * MAD, away from the borders.
    Returns a list of (y, x) int coordinates, brightest first."""
    from scipy.ndimage import maximum_filter

    med = float(np.median(img))
    mad = float(np.median(np.abs(img - med))) or 1e-6
    thr = med + k_sigma * mad
    mx = maximum_filter(img, size=7)
    peaks = (img >= mx) & (img > thr)
    ys, xs = np.nonzero(peaks)
    h, w = img.shape
    keep = [(float(img[y, x]), int(y), int(x)) for y, x in zip(ys, xs)
            if exclude <= y < h - exclude and exclude <= x < w - exclude]
    keep.sort(reverse=True)
    # Suppress near-duplicates (two maxima on one saturated flat top).
    out = []
    for v, y, x in keep:
        if all((y - yy) ** 2 + (x - xx) ** 2 > 8 ** 2 for yy, xx in out):
            out.append((y, x))
        if len(out) >= max_stars:
            break
    return out


# --- per-star measurements ---------------------------------------------------

def _local_bg(img, y, x, rin=12, rout=16):
    """Median of an annulus well outside the star core."""
    yy, xx = np.ogrid[-rout:rout + 1, -rout:rout + 1]
    r2 = yy ** 2 + xx ** 2
    ann = (r2 >= rin ** 2) & (r2 <= rout ** 2)
    win = img[y - rout:y + rout + 1, x - rout:x + rout + 1]
    if win.shape != ann.shape:
        return float(np.median(img))
    return float(np.median(win[ann]))


def fwhm_of(img, y, x, half=10):
    """FWHM via the area-above-half-max method (robust to asymmetry/noise):
    count pixels >= bg + peak/2 in a small window, FWHM = 2*sqrt(area/pi)."""
    win = img[y - half:y + half + 1, x - half:x + half + 1].astype(np.float64)
    if win.size == 0:
        return 0.0
    bg = _local_bg(img, y, x)
    peak = float(win.max()) - bg
    if peak <= 0:
        return 0.0
    area = int(np.count_nonzero(win >= bg + peak / 2.0))
    return 2.0 * np.sqrt(area / np.pi)


def dark_ring_index(img, y, x, rin=4, rout=9):
    """How far the darkest annulus pixel dips BELOW the local background
    (clipped at 0). This is the 'bubble' artifact, directly."""
    yy, xx = np.ogrid[-rout:rout + 1, -rout:rout + 1]
    r2 = yy ** 2 + xx ** 2
    ann = (r2 >= rin ** 2) & (r2 <= rout ** 2)
    win = img[y - rout:y + rout + 1, x - rout:x + rout + 1]
    if win.shape != ann.shape:
        return 0.0
    bg = _local_bg(img, y, x)
    return max(0.0, bg - float(win[ann].min()))


def flux_of(img, y, x, r=6):
    """Background-subtracted aperture sum."""
    yy, xx = np.ogrid[-r:r + 1, -r:r + 1]
    ap = (yy ** 2 + xx ** 2) <= r ** 2
    win = img[y - r:y + r + 1, x - r:x + r + 1].astype(np.float64)
    if win.shape != ap.shape:
        return 0.0
    bg = _local_bg(img, y, x)
    return float((win[ap] - bg).sum())


def measure(pred, ref, stars):
    """Aggregate per-star metrics of pred against a reference plane."""
    fw_p, fw_r, rings, fluxes = [], [], [], []
    for y, x in stars:
        fp = fwhm_of(pred, y, x)
        fr = fwhm_of(ref, y, x)
        if fp <= 0 or fr <= 0:
            continue
        fw_p.append(fp)
        fw_r.append(fr)
        rings.append(dark_ring_index(pred, y, x))
        f_ref = flux_of(ref, y, x)
        if abs(f_ref) > 1e-9:
            fluxes.append(flux_of(pred, y, x) / f_ref)
    n = len(fw_p)
    if n == 0:
        return None
    return {
        "stars": n,
        "fwhm_ratio": round(float(np.mean(np.array(fw_p) / np.array(fw_r))), 4),
        "dark_ring": round(float(np.mean(rings)), 6),
        "flux_ratio": round(float(np.mean(fluxes)) if fluxes else 0.0, 4),
    }


# --- data feeds --------------------------------------------------------------

def _iter_synth(args):
    """(x_model_input, target_plane, input_plane) triples, deterministic."""
    import synth
    import dataset as ds
    import zlib
    for p in sorted(glob.glob(os.path.join(args.tiles_val, "*.npy")))[: args.limit]:
        sharp = np.load(p).astype(np.float32)
        # crc32, not hash(): str hashes are salted per process, which would
        # re-synthesize different pairs every run (see eval_models.py).
        rng = np.random.default_rng(
            zlib.crc32(os.path.basename(p).encode("utf-8")) % (2**31))
        x, y, _ = synth.make_pair(
            sharp, rng, noise_matched=getattr(args, "noise_matched", False),
            noise_match_alpha=getattr(args, "noise_match_alpha", 1.0))
        if args.log_norm:
            x, y = ds.log_norm_pair(x, y)
        yield x, y[0], x[0]


def _iter_real(args):
    """Real tiles (no GT): model input built from the tile + constant sigma;
    reference is the input itself."""
    import dataset as ds
    for p in sorted(glob.glob(os.path.join(args.real, "*.npy")))[: args.limit]:
        img = np.load(p).astype(np.float32)
        if img.ndim == 3:
            img = img.mean(axis=0)
        sig = np.full_like(img, args.sigma, dtype=np.float32)
        x = np.stack([img, sig])
        if args.log_norm:
            # Same params from the input; the "target" arg is a dummy copy.
            x, _ = ds.log_norm_pair(x, img[None])
        yield x, x[0], x[0]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--task", default="decon", choices=["decon", "halo"])
    ap.add_argument("--models", default="models")
    ap.add_argument("--tiles-val", default="")
    ap.add_argument("--real", default="",
                    help="Directory of real mono .npy tiles (no ground truth); "
                         "metrics compare prediction vs INPUT.")
    ap.add_argument("--sigma", type=float, default=0.5,
                    help="Real mode: constant sigma condition channel value "
                         "(fwhm/9 in the training convention).")
    ap.add_argument("--size", type=int, default=256)
    ap.add_argument("--limit", type=int, default=100)
    ap.add_argument("--log-norm", action=argparse.BooleanOptionalAction, default=True)
    ap.add_argument("--noise-matched", action="store_true",
                    help="Synthesize eval pairs with the BXT noise-preserving "
                         "target; pass when the model was trained with "
                         "--noise-matched-target.")
    ap.add_argument("--noise-match-alpha", type=float, default=1.0,
                    help="Match the training --noise-match-alpha.")
    ap.add_argument("--json-out", default="")
    args = ap.parse_args()

    import onnxruntime as ort

    triples = list(_iter_real(args) if args.real else _iter_synth(args))
    if not triples:
        raise SystemExit("no evaluation tiles found")
    mode = "real (vs input)" if args.real else "synthetic (vs target)"
    print(f"{args.task} star metrics, {mode}: {len(triples)} tiles")

    variants = []
    for tag in ("fp32", "fp16", "int16", "int8"):
        for cand in (f"{args.task}_{tag}_{args.size}.onnx", f"{args.task}_{tag}.onnx"):
            path = os.path.join(args.models, cand)
            if os.path.exists(path):
                variants.append((tag, path))
                break
    if not variants:
        raise SystemExit(f"no {args.task}_* ONNX models under {args.models}")

    print(f"{'variant':8} {'stars':>6} {'FWHMratio':>10} {'darkring':>10} {'fluxratio':>10}")
    results = []
    for tag, path in variants:
        sess = ort.InferenceSession(path, providers=["CPUExecutionProvider"])
        iname = sess.get_inputs()[0].name
        aggs = []
        for x, ref, inp in triples:
            out = sess.run(None, {iname: x[None].astype(np.float32)})[0]
            pred = np.asarray(out)[0]
            pred = pred[0] if pred.ndim == 3 else pred
            # Stars detected on the REFERENCE so every variant measures the
            # same star set (comparable rows).
            stars = detect_stars(ref)
            m = measure(pred, ref, stars)
            if m:
                aggs.append(m)
        if not aggs:
            print(f"{tag:8} {'-':>6}")
            continue
        row = {
            "variant": tag,
            "stars": int(np.sum([a["stars"] for a in aggs])),
            "fwhm_ratio": round(float(np.mean([a["fwhm_ratio"] for a in aggs])), 4),
            "dark_ring": round(float(np.mean([a["dark_ring"] for a in aggs])), 6),
            "flux_ratio": round(float(np.mean([a["flux_ratio"] for a in aggs])), 4),
        }
        results.append(row)
        print(f"{tag:8} {row['stars']:6d} {row['fwhm_ratio']:10.4f} "
              f"{row['dark_ring']:10.6f} {row['flux_ratio']:10.4f}")

    if args.json_out:
        doc = {
            "task": args.task, "mode": mode,
            "date": datetime.date.today().isoformat(),
            "tiles": len(triples), "results": results,
        }
        os.makedirs(os.path.dirname(args.json_out) or ".", exist_ok=True)
        with open(args.json_out, "w", encoding="utf-8") as f:
            json.dump(doc, f, indent=2)
        print(f"wrote {args.json_out}")


if __name__ == "__main__":
    main()
