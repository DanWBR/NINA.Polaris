"""Per-task quality eval across precisions (fp32 / fp16 / int16 / w8a16 / int8).

Runs each exported ONNX over a held-out validation set and reports mean PSNR +
SSIM, so the "no quantization degradation" claim is measured: int8-QAT should
land within a small delta of fp16/fp32.

  python eval_models.py --task denoise --models models --val-pairs data/own/denoise_val
  python eval_models.py --task bge     --models models --val-pairs data/own/bge_val
  python eval_models.py --task decon   --models models --tiles-val data/own/decon_tiles_val
"""
from __future__ import annotations

import argparse
import datetime
import glob
import json
import os

import numpy as np


def psnr(a, b, rng=None):
    mse = float(np.mean((a - b) ** 2))
    if mse <= 1e-12:
        return 99.0
    if rng is None:
        rng = float(max(a.max(), b.max()) - min(a.min(), b.min())) or 1.0
    return 10.0 * np.log10(rng * rng / mse)


def ssim(a, b, rng=None):
    """Global Gaussian-window SSIM (single scale), numpy + scipy.

    SSIM's stabilization constants scale with the data range L (C1=(0.01L)^2,
    C2=(0.03L)^2). Our tensors live in the MAD-normalized domain (roughly
    +/-10, NOT 0..1), so hardcoding L=1 mis-scales C1/C2 and collapses the
    score (e.g. 0.49 alongside a 51 dB PSNR). L comes from the data; when the
    caller passes a dataset-global range, numbers are comparable across runs
    (a per-pair L floats with each pair's outliers).
    """
    from scipy.ndimage import uniform_filter

    a = a.astype(np.float64)
    b = b.astype(np.float64)
    L = rng if rng is not None else \
        (float(max(a.max(), b.max()) - min(a.min(), b.min())) or 1.0)
    C1, C2 = (0.01 * L) ** 2, (0.03 * L) ** 2
    mu_a = uniform_filter(a, 7)
    mu_b = uniform_filter(b, 7)
    va = uniform_filter(a * a, 7) - mu_a ** 2
    vb = uniform_filter(b * b, 7) - mu_b ** 2
    vab = uniform_filter(a * b, 7) - mu_a * mu_b
    s = ((2 * mu_a * mu_b + C1) * (2 * vab + C2)) / \
        ((mu_a ** 2 + mu_b ** 2 + C1) * (va + vb + C2) + 1e-12)
    return float(np.clip(s, -1, 1).mean())


def _iter_pairs(args):
    """Yield (x_input, y_target) in the model's native layout.
       nhwc tasks: x [H,W,3], y [3,H,W];  decon: x [2,H,W], y [1,H,W]."""
    if args.task == "decon":
        import synth
        import dataset as ds
        import zlib
        for p in sorted(glob.glob(os.path.join(args.tiles_val, "*.npy"))):
            sharp = np.load(p).astype(np.float32)
            # crc32, NOT hash(): Python salts str hashes per process
            # (PYTHONHASHSEED), so hash() re-synthesized DIFFERENT pairs on
            # every run and no two evals were comparable.
            rng = np.random.default_rng(
                zlib.crc32(os.path.basename(p).encode("utf-8")) % (2**31))
            x, y, _ = synth.make_pair(                    # x [2,H,W], y [1,H,W]
                sharp, rng, noise_matched=getattr(args, "noise_matched", False))
            # CRITICAL: the production Detail model is TRAINED in the
            # GraXpert log-mean-std domain (train_task.py --log-norm default
            # True) and the int8/int16 calibration set is log-normalized too
            # (quantize.py cmd_calib). Feeding raw linear pairs here ran the
            # fp32/fp16 models out of distribution, which invalidated every
            # decon number and produced the absurd "int16 beats fp32 by
            # +2.9 dB" artifact. Mirror the training/calib domain by default;
            # --no-log-norm only for evaluating a legacy percentile model.
            if args.log_norm:
                x, y = ds.log_norm_pair(x, y)
            yield x, y
    else:
        idir = os.path.join(args.val_pairs, "input")
        tdir = os.path.join(args.val_pairs, "target")
        for p in sorted(glob.glob(os.path.join(idir, "*.npy"))):
            t = os.path.join(tdir, os.path.basename(p))
            if not os.path.exists(t):
                continue
            x = np.load(p).astype(np.float32)            # [3,H,W]
            y = np.load(t).astype(np.float32)            # [3,H,W]
            yield np.transpose(x, (1, 2, 0)), y          # x -> NHWC [H,W,3]


def _to_chw(out, task):
    """ORT output -> [C,H,W] for comparison."""
    o = np.asarray(out)
    o = o[0] if o.ndim == 4 else o
    if task == "decon":
        return o if o.ndim == 3 else o[None]             # [1,H,W]
    return np.transpose(o, (2, 0, 1))                    # NHWC [H,W,3] -> [3,H,W]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--task", required=True,
                    choices=["decon", "denoise", "bge", "upscale", "halo"])
    ap.add_argument("--models", default="models")
    ap.add_argument("--val-pairs", default="")
    ap.add_argument("--tiles-val", default="")
    ap.add_argument("--size", type=int, default=256)
    ap.add_argument("--limit", type=int, default=200)
    ap.add_argument("--log-norm", action=argparse.BooleanOptionalAction, default=True,
                    help="Decon only: evaluate in the GraXpert log-mean-std "
                         "domain the production model was trained in (mirrors "
                         "quantize.py calib). --no-log-norm only for a legacy "
                         "percentile-normalized model.")
    ap.add_argument("--noise-matched", action="store_true",
                    help="Decon only: synthesize eval pairs with the BXT "
                         "noise-preserving target (target = f*g' + n). Pass this "
                         "when evaluating a model trained with "
                         "--noise-matched-target, else PSNR reads artificially "
                         "low (the model correctly outputs noise the clean "
                         "target lacks).")
    ap.add_argument("--per-pair-range", action="store_true",
                    help="Legacy behaviour: derive the PSNR/SSIM range L per "
                         "pair instead of once over the whole validation set. "
                         "Per-pair ranges float with each pair's outliers and "
                         "make runs non-comparable.")
    ap.add_argument("--json-out", default="",
                    help="Also write results as JSON (machine-diffable across "
                         "experiments), e.g. eval/results_decon_2026-07-02.json")
    args = ap.parse_args()

    import onnxruntime as ort

    pairs = list(_iter_pairs(args))[: args.limit]
    if not pairs:
        raise SystemExit("no validation pairs found")
    print(f"{args.task}: {len(pairs)} validation samples")

    # Dataset-global range L for PSNR/SSIM: computed ONCE from the targets so
    # every variant (and every future run on the same set) scores against the
    # same denominator. Per-pair ranges made numbers drift with outliers.
    global_rng = None
    if not args.per_pair_range:
        lo = min(float(y.min()) for _, y in pairs)
        hi = max(float(y.max()) for _, y in pairs)
        global_rng = (hi - lo) or 1.0
        print(f"range L (targets, fixed): {global_rng:.4f}")

    variants = []
    for tag in ("fp32", "fp16", "int16", "w8a16", "int8"):
        # accept both sized and unsized names
        for cand in (f"{args.task}_{tag}_{args.size}.onnx", f"{args.task}_{tag}.onnx"):
            path = os.path.join(args.models, cand)
            if os.path.exists(path):
                variants.append((tag, path))
                break
    if not variants:
        raise SystemExit(f"no {args.task}_* ONNX models under {args.models}")

    print(f"{'variant':8} {'PSNR(dB)':>10} {'SSIM':>8}")
    base = None
    results = []
    for tag, path in variants:
        sess = ort.InferenceSession(path, providers=["CPUExecutionProvider"])
        iname = sess.inputNames[0] if hasattr(sess, "inputNames") else sess.get_inputs()[0].name
        ps, ss = [], []
        for x, y in pairs:
            out = sess.run(None, {iname: x[None].astype(np.float32)})[0]
            pred = _to_chw(out, args.task)
            ps.append(psnr(pred, y, rng=global_rng))
            ss.append(np.mean([ssim(pred[c], y[c], rng=global_rng)
                               for c in range(pred.shape[0])]))
        mp, ms = float(np.mean(ps)), float(np.mean(ss))
        delta = "" if base is None else f"  (Δ {mp - base:+.2f} dB vs fp32)"
        base = mp if tag == "fp32" else base
        print(f"{tag:8} {mp:10.2f} {ms:8.4f}{delta}")
        results.append({
            "variant": tag, "model": os.path.basename(path),
            "psnr_db": round(mp, 4), "ssim": round(ms, 6),
            "delta_db_vs_fp32": None if tag == "fp32" or base is None
                                else round(mp - base, 4),
        })

    if args.json_out:
        doc = {
            "task": args.task,
            "date": datetime.date.today().isoformat(),
            "samples": len(pairs),
            "log_norm": bool(args.log_norm) if args.task == "decon" else None,
            "range_L": global_rng,
            "results": results,
        }
        os.makedirs(os.path.dirname(args.json_out) or ".", exist_ok=True)
        with open(args.json_out, "w", encoding="utf-8") as f:
            json.dump(doc, f, indent=2)
        print(f"wrote {args.json_out}")


if __name__ == "__main__":
    main()
