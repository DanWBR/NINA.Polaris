"""Per-task quality eval across precisions (fp32 / fp16 / int16 / int8).

Runs each exported ONNX over a held-out validation set and reports mean PSNR +
SSIM, so the "no quantization degradation" claim is measured: int8-QAT should
land within a small delta of fp16/fp32.

  python eval_models.py --task denoise --models models --val-pairs data/own/denoise_val
  python eval_models.py --task bge     --models models --val-pairs data/own/bge_val
  python eval_models.py --task decon   --models models --tiles-val data/own/decon_tiles_val
"""
from __future__ import annotations

import argparse
import glob
import os

import numpy as np


def psnr(a, b):
    mse = float(np.mean((a - b) ** 2))
    if mse <= 1e-12:
        return 99.0
    rng = float(max(a.max(), b.max()) - min(a.min(), b.min())) or 1.0
    return 10.0 * np.log10(rng * rng / mse)


def ssim(a, b):
    """Global Gaussian-window SSIM (single scale), numpy + scipy."""
    from scipy.ndimage import uniform_filter

    a = a.astype(np.float64)
    b = b.astype(np.float64)
    C1, C2 = (0.01 * 1) ** 2, (0.03 * 1) ** 2
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
        for p in sorted(glob.glob(os.path.join(args.tiles_val, "*.npy"))):
            sharp = np.load(p).astype(np.float32)
            rng = np.random.default_rng(abs(hash(os.path.basename(p))) % (2**31))
            x, y, _ = synth.make_pair(sharp, rng)        # x [2,H,W], y [1,H,W]
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
    ap.add_argument("--task", required=True, choices=["decon", "denoise", "bge"])
    ap.add_argument("--models", default="models")
    ap.add_argument("--val-pairs", default="")
    ap.add_argument("--tiles-val", default="")
    ap.add_argument("--size", type=int, default=256)
    ap.add_argument("--limit", type=int, default=200)
    args = ap.parse_args()

    import onnxruntime as ort

    pairs = list(_iter_pairs(args))[: args.limit]
    if not pairs:
        raise SystemExit("no validation pairs found")
    print(f"{args.task}: {len(pairs)} validation samples")

    variants = []
    for tag in ("fp32", "fp16", "int16", "int8"):
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
    for tag, path in variants:
        sess = ort.InferenceSession(path, providers=["CPUExecutionProvider"])
        iname = sess.inputNames[0] if hasattr(sess, "inputNames") else sess.get_inputs()[0].name
        ps, ss = [], []
        for x, y in pairs:
            out = sess.run(None, {iname: x[None].astype(np.float32)})[0]
            pred = _to_chw(out, args.task)
            ps.append(psnr(pred, y))
            ss.append(np.mean([ssim(pred[c], y[c]) for c in range(pred.shape[0])]))
        mp, ms = float(np.mean(ps)), float(np.mean(ss))
        delta = "" if base is None else f"  (Δ {mp - base:+.2f} dB vs fp32)"
        base = mp if tag == "fp32" else base
        print(f"{tag:8} {mp:10.2f} {ms:8.4f}{delta}")


if __name__ == "__main__":
    main()
