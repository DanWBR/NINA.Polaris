#!/usr/bin/env python3
"""N.I.N.A. Polaris - generate curated per-object DSO preview thumbnails.

The SKY tab's search/atlas result cards show name, type, magnitude... but no
picture, so you can't tell a galaxy from a nebula at a glance. This bakes a
small DSS2-colour cutout per catalogued object (ASIAIR-style "photo per
target") that the frontend shows next to each result, fully offline once
bundled.

Each thumbnail is rendered by the CDS hips2fits service from the DSS2 Color
HiPS, centred on the object and sized to ~2.5x its major axis, 256x256 JPEG
(~10-15 KB). Files land at:

    wwwroot/sky/data/skydata/dso-thumbs/<SLUG>.jpg

where SLUG = catalog + catalog_id, uppercased, spaces stripped (e.g. M42,
NGC7000, IC1396, C14). The frontend derives the same slug from a result's
catalog/catalogId (or by parsing its name) and shows the image with an
onerror fallback, so missing thumbs simply don't render.

Selection is tunable so you choose the disk/coverage trade-off:
  --catalogs M,C,NGC,IC   which catalogs to include (default: M,C)
  --max-mag 11            include objects brighter than this (default 11)
  --min-size 3            ...OR larger than this arcmin (default 3)
  --limit 0               cap object count (0 = no cap)
Messier + Caldwell (~220 objects) is the default baseline (~3-4 MB) and the
set shipped in-repo via Git LFS. Widen to NGC/IC for fuller coverage; the
run is resumable (existing thumbs are skipped).

Stdlib only (sqlite3 + urllib), same as build-dso-catalog.py.

Attribution: DSS2 Color, STScI/NASA, HEALPixed + served by CDS Strasbourg
(hips2fits). Mirrored locally for offline field use.
"""
import argparse
import os
import sqlite3
import sys
import time
import urllib.parse
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
DB = os.path.join(HERE, "..", "src", "NINA.Polaris", "wwwroot", "catalogs", "dso", "dso.db")
OUT = os.path.join(HERE, "..", "src", "NINA.Polaris", "wwwroot", "sky", "data",
                   "skydata", "dso-thumbs")
HIPS2FITS = "https://alasky.cds.unistra.fr/hips-image-services/hips2fits"


def slug_for(catalog: str, catalog_id: str) -> str:
    return (catalog + catalog_id).upper().replace(" ", "")


def fov_deg(size_arcmin) -> float:
    # Frame ~2.5x the major axis so the object sits comfortably inside the
    # thumbnail; clamp so tiny PNe aren't a single pixel and huge nebulae
    # don't wash out into background.
    if size_arcmin and size_arcmin > 0:
        return min(max(size_arcmin / 60.0 * 2.5, 0.2), 3.0)
    return 0.5  # unknown size: a sensible deep-sky default


def fetch(ra_deg: float, dec_deg: float, fov: float, out_path: str) -> bool:
    qs = urllib.parse.urlencode({
        "hips": "CDS/P/DSS2/color",
        "width": 256, "height": 256,
        "fov": f"{fov:.4f}",
        "projection": "TAN", "coordsys": "icrs",
        "ra": f"{ra_deg:.5f}", "dec": f"{dec_deg:.5f}",
        "format": "jpg",
    })
    url = f"{HIPS2FITS}?{qs}"
    for attempt in range(3):
        try:
            req = urllib.request.Request(url, headers={"User-Agent": "NINA.Polaris/dso-thumbs"})
            with urllib.request.urlopen(req, timeout=60) as r:
                data = r.read()
            if not data:
                return False
            tmp = out_path + ".part"
            with open(tmp, "wb") as f:
                f.write(data)
            os.replace(tmp, out_path)
            return True
        except Exception as e:  # noqa: BLE001 - network is best-effort
            if attempt == 2:
                print(f"    ! failed: {e}", file=sys.stderr)
                return False
            time.sleep(1.5)
    return False


def main() -> int:
    ap = argparse.ArgumentParser(description="Generate DSO preview thumbnails (DSS2).")
    ap.add_argument("--catalogs", default="M,C",
                    help="comma list of catalogs to include (default M,C)")
    ap.add_argument("--max-mag", type=float, default=11.0)
    ap.add_argument("--min-size", type=float, default=3.0,
                    help="include objects at least this many arcmin even if faint")
    ap.add_argument("--limit", type=int, default=0, help="cap object count (0=all)")
    args = ap.parse_args()

    if not os.path.exists(DB):
        print(f"dso.db not found at {DB} - run build-dso-catalog.py first", file=sys.stderr)
        return 1
    os.makedirs(OUT, exist_ok=True)

    cats = [c.strip().upper() for c in args.catalogs.split(",") if c.strip()]
    placeholders = ",".join("?" for _ in cats)
    con = sqlite3.connect(DB)
    con.row_factory = sqlite3.Row
    # Bright OR large, within the chosen catalogs. magnitude can be a 99
    # sentinel for "unknown" in the catalog, so the size clause keeps big
    # well-known objects that lack a tabulated magnitude.
    rows = con.execute(
        f"""SELECT catalog, catalog_id, name, ra_hours, dec_deg, magnitude, size_arcmin
            FROM objects
            WHERE UPPER(catalog) IN ({placeholders})
              AND ( (magnitude IS NOT NULL AND magnitude < ?)
                    OR (size_arcmin IS NOT NULL AND size_arcmin >= ?) )
            ORDER BY (magnitude IS NULL), magnitude ASC""",
        (*cats, args.max_mag, args.min_size),
    ).fetchall()
    con.close()

    if args.limit > 0:
        rows = rows[:args.limit]

    print(f"{len(rows)} objects selected ({','.join(cats)}; mag<{args.max_mag} or >={args.min_size}')")
    print(f"-> {OUT}")

    done = skipped = failed = 0
    for i, row in enumerate(rows, 1):
        slug = slug_for(row["catalog"], row["catalog_id"])
        out_path = os.path.join(OUT, f"{slug}.jpg")
        if os.path.exists(out_path) and os.path.getsize(out_path) > 0:
            skipped += 1
            continue
        ra_deg = row["ra_hours"] * 15.0
        ok = fetch(ra_deg, row["dec_deg"], fov_deg(row["size_arcmin"]), out_path)
        if ok:
            done += 1
        else:
            failed += 1
        if i % 25 == 0 or i == len(rows):
            print(f"  {i}/{len(rows)}  (new {done}, skip {skipped}, fail {failed})")
        time.sleep(0.05)  # be polite to the shared CDS service

    print(f"Done. new {done}, skipped {skipped}, failed {failed}")
    print("Commit with Git LFS (already tracked in .gitattributes):")
    print(f"  git add {OUT} && git commit -m 'skydata: bundle DSO preview thumbnails'")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
