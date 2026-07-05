#!/usr/bin/env python3
"""
Fetch the Pickles (1998) UVKLIB stellar spectral flux library and convert it
to the compact JSON the SPCC engine consumes.

Output: src/NINA.Polaris/wwwroot/catalogs/spcc/pickles.json
        (committed + bundled: at ~95 KB it ships in the repo and travels into
        the publish output / Docker image / .deb like curves.json, so SPCC's
        "Pickles" spectral source is available out of the box — no download
        needed on the device. Re-run this only to refresh/rebuild it.)

Why: SPCC's always-available spectral source is a blackbody derived from a
star's catalog B-V. That is a good broadband approximation, but real stars
have absorption lines. Pickles templates are empirical spectra; picking the
nearest-colour template and integrating IT through the filter/QE curves is
the "Pickles" spectral source Polaris offers as an accuracy upgrade. Fully
offline once bundled.

Source: STScI CDBS reference atlas, grid/pickles/dat_uvk (the 131-spectrum
UVKLIB set), which is a stable public mirror of the Pickles atlas. The older
VizieR TAP path (J/PASP/110/863) was retired here because that service is
unreliable and its table layout changed.

Reference: A.J. Pickles, "A Stellar Spectral Flux Library: 1150-25000 A",
PASP 110, 863 (1998).

Each template's B-V is computed self-consistently by synthetic photometry
through the Johnson B and V bands (Bessell 1990 passbands, embedded below),
so no external colour table is needed. Spectra are resampled onto the SPCC
working grid (380-720 nm @ 5 nm) and normalised. Real spectral-type names
(O5V, G2V, M3II, ...) come from the atlas index file.

Usage:
    python scripts/download-pickles.py

Requirements:
    Python 3.8+, numpy, astropy (to read the atlas FITS tables). Needs
    internet only when (re)building; the produced JSON is committed.
"""

import argparse
import io
import json
import math
import os
import sys
import urllib.request

BASE = "https://ssb.stsci.edu/cdbs/grid/pickles/dat_uvk/"

# SPCC working grid (must match SpccDatabase.Grid on the C# side).
GRID = [380 + 5 * i for i in range(int((720 - 380) / 5) + 1)]

# Johnson B and V passbands (Bessell 1990), wavelength in nm, response 0..1.
JOHNSON_B = ([360, 380, 400, 420, 440, 460, 480, 500, 520, 540, 560],
             [0.00, 0.30, 0.80, 1.00, 0.92, 0.70, 0.45, 0.22, 0.08, 0.02, 0.00])
JOHNSON_V = ([470, 490, 510, 530, 550, 570, 590, 610, 630, 650, 680, 700],
             [0.00, 0.15, 0.55, 0.87, 1.00, 0.94, 0.79, 0.58, 0.36, 0.18, 0.03, 0.00])


def _get(url):
    with urllib.request.urlopen(url, timeout=90) as resp:
        return resp.read()


def interp(xs, ys, x):
    if x < xs[0] or x > xs[-1]:
        return 0.0
    lo, hi = 0, len(xs) - 1
    while hi - lo > 1:
        mid = (lo + hi) // 2
        if xs[mid] <= x:
            lo = mid
        else:
            hi = mid
    dx = xs[hi] - xs[lo]
    if dx <= 0:
        return ys[lo]
    t = (x - xs[lo]) / dx
    return ys[lo] + t * (ys[hi] - ys[lo])


def band_flux(wl_nm, flux, band):
    """Photon-weighted flux through a passband: integral of F*R*lambda."""
    bx, by = band
    s = 0.0
    prev_l = bx[0]
    prev_v = interp(wl_nm, flux, bx[0]) * by[0] * bx[0]
    for i in range(1, len(bx)):
        lam = bx[i]
        v = interp(wl_nm, flux, lam) * by[i] * lam
        s += 0.5 * (prev_v + v) * (lam - prev_l)
        prev_l, prev_v = lam, v
    return s


def synthetic_bv(wl_nm, flux):
    fb = band_flux(wl_nm, flux, JOHNSON_B)
    fv = band_flux(wl_nm, flux, JOHNSON_V)
    if fb <= 0 or fv <= 0:
        return None
    return -2.5 * math.log10(fb / fv)


def main():
    try:
        from astropy.io import fits
        import numpy as np
    except ImportError:
        print("ERROR: this script needs numpy + astropy to read the atlas "
              "FITS tables (pip install numpy astropy).", file=sys.stderr)
        return 1

    ap = argparse.ArgumentParser(description="Build SPCC pickles.json")
    default_out = os.path.join(
        os.path.dirname(__file__), "..", "src", "NINA.Polaris",
        "wwwroot", "catalogs", "spcc", "pickles.json")
    ap.add_argument("--out", default=default_out)
    args = ap.parse_args()

    # Spectral-type names from the atlas index (FILENAME -> SPTYPE).
    names = {}
    try:
        idx = fits.open(io.BytesIO(_get(BASE + "pickles_uk.fits")))
        cols = idx[1].columns.names
        for row in idx[1].data:
            rec = {c.upper(): row[i] for i, c in enumerate(cols)}
            fn = str(rec.get("FILENAME", "")).strip().lower()
            sp = str(rec.get("SPTYPE", "")).strip()
            if fn:
                names[fn] = sp
    except Exception as e:  # noqa: BLE001
        print(f"WARN: could not read atlas index ({e}); using uk<N> names.",
              file=sys.stderr)

    templates = []
    for i in range(1, 132):
        fn = f"pickles_uk_{i}"
        try:
            h = fits.open(io.BytesIO(_get(BASE + fn + ".fits")))
            wl_ang = np.asarray(h[1].data["WAVELENGTH"], float)
            flux = np.asarray(h[1].data["FLUX"], float)
        except Exception as e:  # noqa: BLE001
            print(f"skip {fn}: {e}", file=sys.stderr)
            continue
        wl_nm = (wl_ang / 10.0).tolist()
        fl = flux.tolist()
        bv = synthetic_bv(wl_nm, fl)
        if bv is None:
            continue
        resampled = [interp(wl_nm, fl, g) for g in GRID]
        m = sum(resampled) / len(resampled)
        if m <= 0:
            continue
        resampled = [v / m for v in resampled]
        templates.append({"name": names.get(fn, f"uk{i}"), "bv": round(bv, 4),
                          "flux": [round(v, 6) for v in resampled]})

    if not templates:
        print("ERROR: no spectra fetched (network / source unavailable).",
              file=sys.stderr)
        return 1

    templates.sort(key=lambda t: t["bv"])
    out = {"grid": GRID, "templates": templates,
           "source": "Pickles 1998 UVKLIB (STScI CDBS grid/pickles/dat_uvk); "
                     "PASP 110, 863"}
    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(out, f)
    print(f"Wrote {len(templates)} templates "
          f"(B-V {templates[0]['bv']}..{templates[-1]['bv']}) to {args.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
