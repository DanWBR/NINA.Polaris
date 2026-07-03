"""Timing harness for the exported ONNX models.

Reports per-model ms/tile (warmup + timed runs on ORT) AND the projected
ms/megapixel under each pipeline's REAL tiling configuration, because the
user-visible speed is tiles-per-megapixel times ms-per-tile:

  denoise 256/stride128 (hard crop)  -> 61.0 tiles/MP  (4.00x redundancy)
  denoise 256/stride192 (feather)    -> 27.1 tiles/MP  (1.78x)
  detail  256/stride192 (feather)    -> 27.1 tiles/MP
  decon   512/stride448              ->  5.0 tiles/MP
  single-pass 256 (BGE)              ->  1 inference regardless of size

  $env:PYTHONUTF8=1
  python bench_onnx.py --models models --report eval\\timing.json

On-device numbers (RKNN NPU, browser WebGPU/WASM, ncnn) are collected
manually but should be recorded into the same JSON schema for comparison.
"""
from __future__ import annotations

import argparse
import datetime
import glob
import json
import os
import time

import numpy as np


# tiles per megapixel = 1e6 / stride^2 (interior-dominated approximation)
PIPELINE_TILING = {
    "denoise-256/128": {"tile": 256, "stride": 128},
    "denoise-256/192": {"tile": 256, "stride": 192},
    "detail-256/192":  {"tile": 256, "stride": 192},
    "decon-512/448":   {"tile": 512, "stride": 448},
}


def _input_spec(sess):
    i = sess.get_inputs()[0]
    shape = [d if isinstance(d, int) else 1 for d in i.shape]
    return i.name, shape


def bench_session(sess, warmup=5, runs=20):
    name, shape = _input_spec(sess)
    x = np.random.default_rng(0).standard_normal(shape).astype(np.float32) * 0.05
    for _ in range(warmup):
        sess.run(None, {name: x})
    times = []
    for _ in range(runs):
        t0 = time.perf_counter()
        sess.run(None, {name: x})
        times.append((time.perf_counter() - t0) * 1000.0)
    arr = np.array(times)
    return {
        "input_shape": shape,
        "ms_mean": round(float(arr.mean()), 2),
        "ms_median": round(float(np.median(arr)), 2),
        "ms_p90": round(float(np.percentile(arr, 90)), 2),
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--models", default="models")
    ap.add_argument("--pattern", default="*.onnx",
                    help="Glob under --models (e.g. denoise_*.onnx)")
    ap.add_argument("--providers", default="cpu",
                    help="Comma list: cpu, cuda, dml. Each available provider "
                         "is benchmarked separately.")
    ap.add_argument("--threads", type=int, default=0,
                    help="Intra-op threads for CPU (0 = ORT default)")
    ap.add_argument("--warmup", type=int, default=5)
    ap.add_argument("--runs", type=int, default=20)
    ap.add_argument("--report", default="")
    args = ap.parse_args()

    import onnxruntime as ort

    prov_map = {
        "cpu": "CPUExecutionProvider",
        "cuda": "CUDAExecutionProvider",
        "dml": "DmlExecutionProvider",
    }
    avail = set(ort.get_available_providers())
    wanted = []
    for p in args.providers.split(","):
        ep = prov_map.get(p.strip().lower())
        if ep and ep in avail:
            wanted.append((p.strip().lower(), ep))
        elif ep:
            print(f"provider {p} not available in this ORT build, skipping")
    if not wanted:
        raise SystemExit("no requested execution providers available")

    paths = sorted(glob.glob(os.path.join(args.models, args.pattern)))
    if not paths:
        raise SystemExit(f"no models matching {args.pattern} under {args.models}")

    results = []
    for path in paths:
        base = os.path.basename(path)
        for pname, ep in wanted:
            so = ort.SessionOptions()
            if args.threads > 0:
                so.intra_op_num_threads = args.threads
            try:
                sess = ort.InferenceSession(path, so, providers=[ep])
            except Exception as ex:
                print(f"{base} [{pname}]: session failed ({ex})")
                continue
            r = bench_session(sess, args.warmup, args.runs)
            tile = r["input_shape"][2] if len(r["input_shape"]) == 4 else 256
            # Projected ms/MP under each tiling config whose tile matches.
            proj = {}
            for cfg, tl in PIPELINE_TILING.items():
                if tl["tile"] == tile or (tile not in (256, 512)):
                    tiles_per_mp = 1_000_000 / (tl["stride"] ** 2)
                    proj[cfg] = round(r["ms_median"] * tiles_per_mp, 1)
            row = {"model": base, "provider": pname, **r, "ms_per_mp": proj}
            results.append(row)
            pj = "  ".join(f"{k}={v}ms/MP" for k, v in proj.items())
            print(f"{base:44} [{pname}] {r['ms_median']:8.2f} ms/tile  {pj}")

    if args.report:
        import platform
        doc = {
            "date": datetime.date.today().isoformat(),
            "host": platform.node() or "?",
            "warmup": args.warmup, "runs": args.runs,
            "results": results,
        }
        os.makedirs(os.path.dirname(args.report) or ".", exist_ok=True)
        with open(args.report, "w", encoding="utf-8") as f:
            json.dump(doc, f, indent=2)
        print(f"wrote {args.report}")


if __name__ == "__main__":
    main()
