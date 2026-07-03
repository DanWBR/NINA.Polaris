"""Build a FROZEN real-data holdout set for model evaluation.

Not training data: a fixed set of ~30-50 real 256px tiles sampled across
targets / SNR levels, saved once and never touched again, so every model
generation is judged against the same real frames (synthetic PSNR alone can't
catch domain drift like the MAD operating point or saturated-star behavior).

Selection per source frame: detect candidate tiles on a stride grid, score by
(a) star presence (bright-pixel count) and (b) structure (std), keep a spread
of low/mid/high-signal tiles. Tiles are stored LINEAR ~[0,1] mono .npy (the
same convention as decon training tiles) plus a manifest recording the source
file + offsets, so provenance is auditable.

  $env:PYTHONUTF8=1
  python make_holdout.py --sources "E:\\Projeto Aguia\\2026\\RGB\\lights" `
      "C:\\Users\\danie\\Downloads\\SV535\\lights" --out data\\holdout_real --count 40

The output lives under data/ (gitignored: FITS-derived pixels never go to
GitHub). Consumers: eval_star_metrics.py --real, future real-metrics scripts.
"""
from __future__ import annotations

import argparse
import glob
import json
import os
import sys

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "data_prep"))
import common  # noqa: E402


def _frame_paths(sources):
    exts = ("fits", "fit", "fts")
    out = []
    for src in sources:
        for ext in exts:
            out.extend(glob.glob(os.path.join(src, "**", f"*.{ext}"), recursive=True))
    return sorted(out)


def _score_tiles(lum: np.ndarray, tile: int, stride: int):
    """Yield (score_std, star_pixels, y, x) for each grid tile worth keeping."""
    med, mad = common.median_mad(lum)
    star_thr = med + 12.0 * max(mad, 1e-6)
    h, w = lum.shape
    for y in range(0, h - tile + 1, stride):
        for x in range(0, w - tile + 1, stride):
            t = lum[y:y + tile, x:x + tile]
            if common.is_mostly_empty(t):
                continue
            yield float(t.std()), int(np.count_nonzero(t > star_thr)), y, x


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--sources", nargs="+", required=True,
                    help="Directories with real light frames (searched recursively)")
    ap.add_argument("--out", default=os.path.join("data", "holdout_real"))
    ap.add_argument("--count", type=int, default=40)
    ap.add_argument("--tile", type=int, default=256)
    ap.add_argument("--per-frame", type=int, default=2,
                    help="Max tiles taken from any single frame (diversity)")
    ap.add_argument("--seed", type=int, default=20260702,
                    help="Fixed seed: the holdout must be reproducible")
    args = ap.parse_args()

    frames = _frame_paths(args.sources)
    if not frames:
        raise SystemExit("no FITS frames found under the given sources")
    print(f"{len(frames)} candidate frames")

    rng = np.random.default_rng(args.seed)
    # Shuffle deterministically so the holdout isn't biased to one session's
    # alphabetical ordering, then walk until we have enough tiles.
    order = rng.permutation(len(frames))

    os.makedirs(args.out, exist_ok=True)
    manifest = []
    kept = 0
    for fi in order:
        if kept >= args.count:
            break
        path = frames[int(fi)]
        try:
            a = common.load_fits(path)
        except Exception as ex:  # unreadable/odd frame: skip, don't die
            print(f"  skip {os.path.basename(path)}: {ex}")
            continue
        if a.ndim == 3:
            a = common.to_luminance(a)
        # Linear ~[0,1]: divide by the sensor range if it looks like ADU.
        mx = float(a.max())
        if mx > 2.0:
            a = a / max(mx, 1.0)
        a = a.astype(np.float32)

        cands = sorted(_score_tiles(a, args.tile, args.tile),  # non-overlapping grid
                       key=lambda c: (c[1], c[0]), reverse=True)
        took = 0
        for _std, stars, y, x in cands:
            if took >= args.per_frame or kept >= args.count:
                break
            t = np.ascontiguousarray(a[y:y + args.tile, x:x + args.tile])
            name = f"holdout_{kept:03d}.npy"
            np.save(os.path.join(args.out, name), t)
            manifest.append({
                "tile": name,
                "source": path,
                "y": int(y), "x": int(x),
                "std": round(float(t.std()), 6),
                "star_pixels": stars,
            })
            kept += 1
            took += 1
        if took:
            print(f"  {os.path.basename(path)}: kept {took}")

    with open(os.path.join(args.out, "manifest.json"), "w", encoding="utf-8") as f:
        json.dump({"seed": args.seed, "tile": args.tile, "tiles": manifest},
                  f, indent=2)
    print(f"holdout: {kept} tiles -> {args.out} (manifest.json written)")
    if kept < args.count:
        print("WARNING: fewer tiles than requested; add more source dirs")


if __name__ == "__main__":
    main()
