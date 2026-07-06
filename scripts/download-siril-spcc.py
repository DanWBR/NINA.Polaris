#!/usr/bin/env python3
"""
Import the Siril SPCC sensor/filter/white-reference database into Polaris.

Siril's SPCC uses a large, community-curated database of REAL measured sensor
QE curves, filter transmission curves, and white-reference spectra for hundreds
of actual cameras and filters (Sony/Canon/Nikon sensors, ZWO/Antlia/Astronomik/
Baader/Optolong/Astrodon filters, ...). This converts that database into the
same `curves.json` schema Polaris SPCC consumes, as a SECOND, clearly-attributed
data file (`curves-siril.json`) that sits alongside the bundled generic curves.

LICENSE / ATTRIBUTION
    Source: https://gitlab.com/free-astro/siril-spcc-database (Team free-astro).
    That database is distributed under the GNU GPL v3, which is compatible with
    Polaris's AGPL v3. The converted file keeps a `_license`/`_attribution`
    header crediting free-astro + the Siril project; the individual curves carry
    the upstream `dataSource` (manufacturer scan / SVO Filter Profile Service /
    Pickles / Bessell / etc.). Do NOT relicense or present these as original.

The generated file is committed so the deployed app has it without cloning the
Siril repo.

Usage:
    # 1. clone the database once (or point --src at an existing checkout)
    git clone --depth 1 https://gitlab.com/free-astro/siril-spcc-database.git
    # 2. convert
    python scripts/download-siril-spcc.py --src ./siril-spcc-database

Requirements: Python 3.8+, stdlib only.
"""

import argparse
import glob
import json
import os
import re
import sys

SRC_URL = "https://gitlab.com/free-astro/siril-spcc-database"
# Keep curves a little beyond the SPCC working grid (380-720 nm) so
# interpolation at the edges is clean; anything further out is dead weight.
CLIP_LO, CLIP_HI = 360.0, 740.0


def _slug(*parts) -> str:
    s = "-".join(p for p in parts if p)
    s = re.sub(r"[^a-zA-Z0-9]+", "-", s).strip("-").lower()
    return s or "x"


def _curve(o):
    """Extract a {wl, v} curve from a Siril entry. Wavelengths are normalised to
    nm: sensor/filter curves are already nm, but the WB_REF spectra (SWIRE
    galaxies, etc.) are stored in Angstrom, so anything spanning past ~1100 is
    treated as Angstrom and divided by 10. Values are kept on their own scale -
    SPCC works on inter-channel ratios, so the absolute unit cancels; we must
    NOT renormalise per channel or we'd corrupt the R:G:B balance. Sorted +
    de-duplicated on wavelength (strictly increasing is required downstream) and
    clipped to the working range."""
    wl = o.get("wavelength", {})
    vv = o.get("values", {})
    wl = wl.get("value") if isinstance(wl, dict) else wl
    vv = vv.get("value", vv.get("values")) if isinstance(vv, dict) else vv
    if not isinstance(wl, list) or not isinstance(vv, list) or len(wl) != len(vv):
        return None
    raw = []
    for x, y in zip(wl, vv):
        try:
            raw.append((float(x), float(y)))
        except (TypeError, ValueError):
            continue
    if len(raw) < 2:
        return None
    scale = 0.1 if max(x for x, _ in raw) > 1100 else 1.0   # Angstrom -> nm
    pts = {}
    for x, y in raw:
        x *= scale
        if x < CLIP_LO or x > CLIP_HI:
            continue
        pts[round(x, 2)] = round(y, 6)      # dedupe on rounded wl; keep last
    if len(pts) < 2:
        return None
    xs = sorted(pts)
    return {"wl": xs, "v": [pts[x] for x in xs]}


def _display(man, model):
    """A clean display name: don't repeat the manufacturer if the model already
    starts with it (e.g. model 'Fujifilm X-Trans 5 HR', man 'Fujifilm')."""
    man = (man or "").strip()
    model = (model or "").strip()
    if not man or model.lower().startswith(man.lower()):
        return model or man
    return (man + " " + model).strip()


def _entries(path):
    d = json.load(open(path, encoding="utf-8"))
    return d if isinstance(d, list) else [d]


def main():
    ap = argparse.ArgumentParser(description="Convert the Siril SPCC database")
    ap.add_argument("--src", required=True,
                    help="Path to a checkout of siril-spcc-database")
    default_out = os.path.join(
        os.path.dirname(__file__), "..", "src", "NINA.Polaris",
        "wwwroot", "catalogs", "spcc", "curves-siril.json")
    ap.add_argument("--out", default=default_out)
    args = ap.parse_args()
    if not os.path.isdir(args.src):
        print(f"ERROR: --src not found: {args.src}", file=sys.stderr)
        return 1

    sensors, filter_sets, white_refs = [], [], []
    # Group multi-channel entries (OSC_SENSOR / MONO_FILTER) by their file.
    osc_channels, mono_filter_channels = {}, {}

    for path in sorted(glob.glob(os.path.join(args.src, "**", "*.json"),
                                 recursive=True)):
        base = os.path.basename(path).lower()
        if "schema" in base or os.sep + "utils" + os.sep in path.lower():
            continue
        try:
            entries = _entries(path)
        except Exception:
            continue
        stem = os.path.splitext(os.path.basename(path))[0]
        for o in entries:
            t = o.get("type")
            man = (o.get("manufacturer") or "").strip()
            model = (o.get("model") or o.get("name") or stem).strip()
            src = o.get("dataSource") or ""
            ch = (o.get("channel") or "").upper()
            cur = _curve(o)
            if cur is None:
                continue

            if t == "OSC_SENSOR" and ch in ("RED", "GREEN", "BLUE"):
                osc_channels.setdefault(path, {"man": man, "model": model, "src": src})[ch] = cur
            elif t == "MONO_SENSOR":
                sensors.append({"id": "siril-" + _slug(man, model), "type": "mono",
                                "name": _display(man, model) + " (Siril)",
                                "source": src, "qe": cur})
            elif t in ("OSC_FILTER", "OSC_LPF"):
                filter_sets.append({"id": "siril-" + _slug(man, model), "for": "osc",
                                    "name": _display(man, model) + " (Siril)",
                                    "source": src, "all": cur})
            elif t == "MONO_FILTER" and ch in ("RED", "GREEN", "BLUE"):
                g = mono_filter_channels.setdefault(path, {"man": man, "model": model, "src": src})
                g[{"RED": "r", "GREEN": "g", "BLUE": "b"}[ch]] = cur
            elif t == "WB_REF":
                # The DB also carries 131 Pickles stellar templates as WB_REFs;
                # those aren't white references and duplicate our Pickles source.
                if "pickles" in man.lower() or "pickles" in src.lower():
                    continue
                white_refs.append({"id": "siril-" + _slug(man, model), "kind": "spectrum",
                                   "name": _display(man, model) + " (Siril)",
                                   "source": src, "spectrum": cur})

    # Assemble grouped OSC sensors (need all three channels).
    for g in osc_channels.values():
        if all(k in g for k in ("RED", "GREEN", "BLUE")):
            sensors.append({"id": "siril-" + _slug(g["man"], g["model"]), "type": "osc",
                            "name": _display(g["man"], g["model"]) + " (Siril)",
                            "source": g["src"],
                            "r": g["RED"], "g": g["GREEN"], "b": g["BLUE"]})
    # Grouped mono RGB filter sets (need all three of R/G/B).
    for g in mono_filter_channels.values():
        if all(k in g for k in ("r", "g", "b")):
            filter_sets.append({"id": "siril-" + _slug(g["man"], g["model"]), "for": "mono",
                                "name": _display(g["man"], g["model"]) + " (Siril)",
                                "source": g["src"], "r": g["r"], "g": g["g"], "b": g["b"]})

    sensors.sort(key=lambda x: x["name"])
    filter_sets.sort(key=lambda x: x["name"])
    white_refs.sort(key=lambda x: x["name"])

    out = {
        "_license": "GPL-3.0-or-later",
        "_attribution": ("Sensor QE, filter transmission and white-reference "
                         "curves from the Siril SPCC database by Team free-astro "
                         "(" + SRC_URL + "), distributed under the GNU GPL v3. "
                         "Per-curve provenance is in each entry's `source` "
                         "(manufacturer data, SVO Filter Profile Service, "
                         "Pickles/Bessell reference series, etc.). Imported into "
                         "Polaris's SPCC curve schema; not original data."),
        "_source": SRC_URL,
        "version": 1,
        "sensors": sensors,
        "filterSets": filter_sets,
        "whiteRefs": white_refs,
    }
    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(out, f, ensure_ascii=False)
    print(f"Wrote {len(sensors)} sensors, {len(filter_sets)} filter sets, "
          f"{len(white_refs)} white refs -> {args.out} "
          f"({os.path.getsize(args.out) // 1024} KB)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
