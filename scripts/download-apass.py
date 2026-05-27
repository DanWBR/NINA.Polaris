#!/usr/bin/env python3
"""
Download + index the APASS DR10 bright-star subset for Polaris's
Photometric Color Calibration (PCC) workflow (CCALB-3).

Output: src/NINA.Polaris/wwwroot/catalogs/apass/apass.db
        (gitignored; bundled by the csproj Content Include into the
        publish output + Docker image)

The full APASS DR10 catalog is ~5 GB; PCC only needs stars bright
enough to plate-solve against (mag V <= 13), which trims to about
5 million rows + a SQLite R*tree index = ~80 MB on disk.

If you have a custom catalog (Gaia DR3 subset, Tycho-2, ...) follow
the same schema and Polaris will use it transparently:

    CREATE TABLE stars (
        id INTEGER PRIMARY KEY,
        ra REAL NOT NULL,    -- degrees
        dec REAL NOT NULL,   -- degrees
        mag_v REAL,          -- V mag (Johnson)
        mag_b REAL,          -- B mag (Johnson)
        b_v REAL,            -- B-V color index, NULL if either band missing
        source TEXT          -- 'APASS' / 'Tycho2' / 'GaiaDR3' / ...
    );
    CREATE VIRTUAL TABLE stars_idx USING rtree(
        id, min_ra, max_ra, min_dec, max_dec
    );

Usage:
    python scripts/download-apass.py [--mag-limit 13.0]

Requirements:
    - Python 3.8+
    - requests (pip install requests)
"""

import argparse
import csv
import os
import sqlite3
import sys
import tempfile
import urllib.request
from pathlib import Path

# AAVSO APASS DR10 ASCII download endpoint. Each file covers a
# 10-degree slice in declination; we fetch + concatenate them.
# Mirror list current as of 2026; see https://www.aavso.org/apass for
# the canonical hosting if these URLs rot.
APASS_BASE_URL = "https://www.aavso.org/apass/dr10"


def parse_args() -> argparse.Namespace:
    p = argparse.ArgumentParser(
        description="Download + index APASS DR10 for Polaris PCC")
    p.add_argument("--mag-limit", type=float, default=13.0,
        help="Drop stars dimmer than this V mag. Default 13.0 keeps "
             "the file ~80 MB; raise to 15.0 for ~400 MB if you "
             "plate-solve at very long focal lengths.")
    p.add_argument("--output", type=Path, default=None,
        help="Output path for apass.db. Default is "
             "src/NINA.Polaris/wwwroot/catalogs/apass/apass.db "
             "relative to this script's repo root.")
    p.add_argument("--skip-download", action="store_true",
        help="Reuse already-downloaded CSV files in the cache dir "
             "(useful when iterating on the indexing step).")
    return p.parse_args()


def repo_root() -> Path:
    """Detect the Polaris repo root from this script's location."""
    here = Path(__file__).resolve().parent
    # scripts/ is one level below the root.
    return here.parent


def default_output(root: Path) -> Path:
    return root / "src" / "NINA.Polaris" / "wwwroot" / "catalogs" / "apass" / "apass.db"


def cache_dir(root: Path) -> Path:
    d = root / "scripts" / ".apass-cache"
    d.mkdir(parents=True, exist_ok=True)
    return d


def download_dec_slice(cache: Path, dec_lo: int) -> Path:
    """Fetch one APASS .csv covering Dec slice [dec_lo, dec_lo+10)."""
    fname = f"apass_dec_{dec_lo:+03d}_to_{dec_lo + 10:+03d}.csv"
    local = cache / fname
    if local.exists() and local.stat().st_size > 0:
        return local
    url = f"{APASS_BASE_URL}/{fname}"
    print(f"  fetching {url}", flush=True)
    try:
        with urllib.request.urlopen(url, timeout=60) as resp:
            data = resp.read()
        local.write_bytes(data)
        return local
    except Exception as e:
        # Some Dec slices have no stars (e.g. southern slices APASS
        # has not fully covered yet); treat 404 as empty.
        print(f"  WARN: failed to fetch {fname}: {e}", flush=True)
        local.write_text("")
        return local


def build_index(out_path: Path, mag_limit: float, cache: Path,
                skip_download: bool) -> None:
    """Concatenate all Dec slices, filter by mag, build SQLite + R*tree."""
    out_path.parent.mkdir(parents=True, exist_ok=True)
    if out_path.exists():
        out_path.unlink()

    print(f"Building {out_path} (mag_v <= {mag_limit})...", flush=True)
    conn = sqlite3.connect(out_path)
    try:
        cur = conn.cursor()
        cur.executescript("""
            CREATE TABLE stars (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                ra REAL NOT NULL,
                dec REAL NOT NULL,
                mag_v REAL,
                mag_b REAL,
                b_v REAL,
                source TEXT NOT NULL
            );
            CREATE VIRTUAL TABLE stars_idx USING rtree(
                id,
                min_ra, max_ra,
                min_dec, max_dec
            );
        """)

        inserted = 0
        for dec_lo in range(-90, 90, 10):
            if not skip_download:
                slice_path = download_dec_slice(cache, dec_lo)
            else:
                slice_path = cache / f"apass_dec_{dec_lo:+03d}_to_{dec_lo + 10:+03d}.csv"
                if not slice_path.exists():
                    print(f"  skip {slice_path.name} (not cached)", flush=True)
                    continue
            inserted += ingest_csv(cur, slice_path, mag_limit)
            conn.commit()
            print(f"  Dec {dec_lo:+03d}..{dec_lo + 10:+03d}: "
                  f"total inserted so far = {inserted:,}", flush=True)

        print(f"Total stars indexed: {inserted:,}", flush=True)
    finally:
        conn.close()


def ingest_csv(cur: sqlite3.Cursor, csv_path: Path, mag_limit: float) -> int:
    """
    Parse one APASS Dec-slice CSV and insert (filtered) rows into the
    open SQLite cursor. APASS CSV columns vary by release; we look up
    by header name so the script does not break when the schema
    shuffles.
    """
    if not csv_path.exists() or csv_path.stat().st_size == 0:
        return 0
    inserted = 0
    with csv_path.open("r", encoding="utf-8", errors="replace") as f:
        reader = csv.DictReader(f)
        for row in reader:
            try:
                ra = float(row.get("radeg") or row.get("ra") or 0)
                dec = float(row.get("decdeg") or row.get("dec") or 0)
                mag_v = _maybe_float(row.get("Vmag") or row.get("vmag"))
                mag_b = _maybe_float(row.get("Bmag") or row.get("bmag"))
            except (TypeError, ValueError):
                continue
            if mag_v is None or mag_v > mag_limit:
                continue
            bv = (mag_b - mag_v) if mag_b is not None else None

            cur.execute(
                "INSERT INTO stars(ra, dec, mag_v, mag_b, b_v, source) "
                "VALUES (?, ?, ?, ?, ?, 'APASS')",
                (ra, dec, mag_v, mag_b, bv))
            row_id = cur.lastrowid
            cur.execute(
                "INSERT INTO stars_idx(id, min_ra, max_ra, min_dec, max_dec) "
                "VALUES (?, ?, ?, ?, ?)",
                (row_id, ra, ra, dec, dec))
            inserted += 1
    return inserted


def _maybe_float(s):
    """Convert string to float, returning None on empty/invalid."""
    if s is None: return None
    s = s.strip()
    if not s: return None
    try: return float(s)
    except (TypeError, ValueError): return None


def main() -> int:
    args = parse_args()
    root = repo_root()
    out = args.output or default_output(root)
    cache = cache_dir(root)

    print(f"APASS download script. Repo root: {root}")
    print(f"Output catalog DB: {out}")
    print(f"Cache dir for CSVs: {cache}")
    print(f"Magnitude limit: V <= {args.mag_limit}")
    print()

    build_index(out, args.mag_limit, cache, args.skip_download)
    print()
    print(f"Done. APASS catalog ready at: {out}")
    print("Polaris will pick it up automatically on the next request.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
