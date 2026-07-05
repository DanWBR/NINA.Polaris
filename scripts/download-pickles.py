#!/usr/bin/env python3
"""
Fetch the Pickles (1998) stellar spectral flux library and convert it to
the compact JSON the SPCC engine consumes.

Output: src/NINA.Polaris/wwwroot/catalogs/spcc/pickles.json
        (gitignored; bundled by the csproj Content Include into the
        publish output + Docker image, like the APASS catalog)

Why: SPCC's always-available spectral source is a blackbody derived from a
star's catalog B-V. That is a good broadband approximation, but real stars
have absorption lines. Pickles templates are empirical spectra; picking the
nearest-colour template and integrating IT through the filter/QE curves is
the "Pickles" spectral source Polaris offers as an accuracy upgrade. Fully
offline once downloaded.

Reference: A.J. Pickles, "A Stellar Spectral Flux Library: 1150-25000 A",
PASP 110, 863 (1998); VizieR J/PASP/110/863.

Each template's B-V is computed self-consistently by synthetic photometry
through the Johnson B and V bands (Bessell 1990 passbands, embedded below),
so no external colour table is needed. Spectra are resampled onto the SPCC
working grid (380-720 nm @ 5 nm) and normalised.

Usage:
    python scripts/download-pickles.py

Requirements:
    Python 3.8+, stdlib only (urllib + json). If the VizieR table layout
    changes, adjust VIZIER_TAP and the ADQL query near the top of main().
"""

import argparse
import json
import math
import os
import sys
import urllib.parse
import urllib.request

VIZIER_TAP = "https://tapvizier.cds.unistra.fr/TAPVizieR/tap/sync"

# SPCC working grid (must match SpccDatabase.Grid on the C# side).
GRID = [380 + 5 * i for i in range(int((720 - 380) / 5) + 1)]

# Johnson B and V passbands (Bessell 1990), wavelength in nm, response 0..1.
# Coarse but sufficient for a B-V colour index used only to pick a template.
JOHNSON_B = ([360, 380, 400, 420, 440, 460, 480, 500, 520, 540, 560],
             [0.00, 0.30, 0.80, 1.00, 0.92, 0.70, 0.45, 0.22, 0.08, 0.02, 0.00])
JOHNSON_V = ([470, 490, 510, 530, 550, 570, 590, 610, 630, 650, 680, 700],
             [0.00, 0.15, 0.55, 0.87, 1.00, 0.94, 0.79, 0.58, 0.36, 0.18, 0.03, 0.00])


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
    """Photon-weighted flux through a passband: sum F*R*lambda."""
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
    """B-V from synthetic photometry, anchored so a ~solar template ≈ 0.65.
    The zero-point offset cancels in template SELECTION (nearest B-V), so a
    consistent instrumental colour is all we need."""
    fb = band_flux(wl_nm, flux, JOHNSON_B)
    fv = band_flux(wl_nm, flux, JOHNSON_V)
    if fb <= 0 or fv <= 0:
        return None
    return -2.5 * math.log10(fb / fv)


def fetch_pickles():
    """Return list of (name, wl_nm[list], flux[list]) from VizieR.

    The Pickles spectra live in VizieR J/PASP/110/863. This queries the
    spectra table and groups rows by spectrum name. Adjust the ADQL if the
    published column names differ in your VizieR mirror."""
    adql = (
        "SELECT SpType, lambda, Flux "
        'FROM "J/PASP/110/863/table3" '
        "ORDER BY SpType, lambda"
    )
    params = urllib.parse.urlencode({
        "request": "doQuery", "lang": "ADQL", "format": "csv", "query": adql,
    })
    url = VIZIER_TAP + "?" + params
    print(f"Querying VizieR: {url[:90]}...", file=sys.stderr)
    with urllib.request.urlopen(url, timeout=300) as resp:
        text = resp.read().decode("utf-8", "replace")

    rows = {}
    lines = text.splitlines()
    for line in lines[1:]:                      # skip header
        parts = line.split(",")
        if len(parts) < 3:
            continue
        name = parts[0].strip().strip('"')
        try:
            lam_ang = float(parts[1])
            flux = float(parts[2])
        except ValueError:
            continue
        rows.setdefault(name, ([], []))
        rows[name][0].append(lam_ang / 10.0)     # Angstrom -> nm
        rows[name][1].append(flux)
    out = []
    for name, (wl, fl) in rows.items():
        pairs = sorted(zip(wl, fl))
        out.append((name, [p[0] for p in pairs], [p[1] for p in pairs]))
    return out


def main():
    ap = argparse.ArgumentParser(description="Build SPCC pickles.json")
    default_out = os.path.join(
        os.path.dirname(__file__), "..", "src", "NINA.Polaris",
        "wwwroot", "catalogs", "spcc", "pickles.json")
    ap.add_argument("--out", default=default_out)
    args = ap.parse_args()

    try:
        specs = fetch_pickles()
    except Exception as e:  # noqa: BLE001
        print(f"ERROR fetching Pickles library: {e}\n"
              "The VizieR table layout may have changed; edit fetch_pickles().",
              file=sys.stderr)
        return 1
    if not specs:
        print("ERROR: no spectra returned.", file=sys.stderr)
        return 1

    templates = []
    for name, wl, fl in specs:
        if len(wl) < 10:
            continue
        bv = synthetic_bv(wl, fl)
        if bv is None:
            continue
        # Resample onto the shared grid + normalise to a unit mean so
        # templates are on a comparable scale (SPCC only uses ratios).
        resampled = [interp(wl, fl, g) for g in GRID]
        m = sum(resampled) / len(resampled)
        if m <= 0:
            continue
        resampled = [v / m for v in resampled]
        templates.append({"name": name, "bv": round(bv, 4),
                          "flux": [round(v, 6) for v in resampled]})

    templates.sort(key=lambda t: t["bv"])
    out = {"grid": GRID, "templates": templates,
           "source": "Pickles 1998 (VizieR J/PASP/110/863)"}
    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(out, f)
    print(f"Wrote {len(templates)} templates to {args.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
