#!/usr/bin/env python3
"""
Download + index APASS DR9 (via VizieR TAP, II/336/apass9) into the
SQLite catalog Polaris's Photometric Color Calibration (PCC) workflow
consumes (CCALB-3).

Output: src/NINA.Polaris/wwwroot/catalogs/apass/apass.db
        (gitignored; bundled by the csproj Content Include into the
        publish output + Docker image)

Why DR9 (not DR10): AAVSO has not published DR10 as a programmatic
download. The original DR10 download endpoint Polaris targeted
(https://www.aavso.org/apass/dr10/apass_dec_*.csv) returns HTTP 500
and the AAVSO landing page only offers a web form. DR9 contains
about 62 million stars; with the default Vmag cap of 13 it trims to
~5.3M stars, which is what PCC's per-FOV cone-search wants. DR9 is
the same catalog Siril and most of the astrophotography toolchain
ships against today.

The data comes from CDS/VizieR's TAP service:
    https://tapvizier.cds.unistra.fr/TAPVizieR/tap

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
    python scripts/download-apass.py [--mag-limit 13.0] [--stripe-deg 5]

Requirements:
    - Python 3.8+
    - stdlib only (urllib + sqlite3)
"""

import argparse
import csv
import io
import os
import sqlite3
import sys
import time
import urllib.parse
import urllib.request
from pathlib import Path

# CDS VizieR TAP endpoint serving APASS DR9 (II/336/apass9).
# Stable + free + no API key. Each ADQL query streams back as TSV.
TAP_URL = "https://tapvizier.cds.unistra.fr/TAPVizieR/tap/sync"

# Per-stripe row cap. VizieR's default is around 100k; we raise this
# to 1M to comfortably fit any 5-degree slice (typical density is
# ~40k stars/deg at Vmag<=13). The server still enforces its own
# hard limit, so massively over-provisioning is harmless.
MAXREC = 2_000_000


def parse_args() -> argparse.Namespace:
    p = argparse.ArgumentParser(
        description="Download + index APASS DR9 for Polaris PCC")
    p.add_argument("--mag-limit", type=float, default=13.0,
        help="Drop stars dimmer than this V mag. Default 13.0 keeps "
             "the file ~80 MB; raise to 15.0 for ~400 MB if you "
             "plate-solve at very long focal lengths.")
    p.add_argument("--stripe-deg", type=float, default=5.0,
        help="Dec stripe width per TAP query. Smaller = more "
             "round-trips but safer against the per-query row cap. "
             "Default 5 fits ~190k rows per stripe at Vmag<=13.")
    p.add_argument("--output", type=Path, default=None,
        help="Output path for apass.db. Default is "
             "src/NINA.Polaris/wwwroot/catalogs/apass/apass.db "
             "relative to this script's repo root.")
    p.add_argument("--skip-download", action="store_true",
        help="Reuse already-downloaded TSV stripes in the cache "
             "dir (useful when iterating on the indexing step).")
    p.add_argument("--keep-cache", action="store_true",
        help="Keep the per-stripe TSV cache after a successful "
             "build. Default is to leave them in place (the next "
             "run with --skip-download reuses them).")
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


def stripe_filename(dec_lo: float, dec_hi: float, mag_limit: float) -> str:
    # Keep the mag_limit in the filename so a re-run at a different
    # mag cap doesn't accidentally pick up a stale cache.
    return f"apass9_dec_{dec_lo:+07.2f}_{dec_hi:+07.2f}_v{mag_limit:.1f}.tsv"


def download_stripe(cache: Path, dec_lo: float, dec_hi: float,
                    mag_limit: float, skip_download: bool) -> Path:
    """Fetch one Dec stripe [dec_lo, dec_hi) via VizieR TAP."""
    local = cache / stripe_filename(dec_lo, dec_hi, mag_limit)
    if local.exists() and local.stat().st_size > 0 and skip_download:
        return local

    # ADQL query: pick the four columns we index. B-V column has a
    # hyphen so it must be quoted with double quotes inside the ADQL.
    adql = (
        'SELECT RAJ2000, DEJ2000, Vmag, Bmag, "B-V" '
        'FROM "II/336/apass9" '
        f'WHERE Vmag <= {mag_limit:.2f} '
        f'  AND DEJ2000 >= {dec_lo:.4f} '
        f'  AND DEJ2000 < {dec_hi:.4f} '
        '  AND RAJ2000 IS NOT NULL '
        '  AND DEJ2000 IS NOT NULL'
    )
    params = urllib.parse.urlencode({
        "REQUEST": "doQuery",
        "LANG": "ADQL",
        "FORMAT": "tsv",
        "MAXREC": str(MAXREC),
        "QUERY": adql,
    })
    url = f"{TAP_URL}?{params}"

    print(f"  fetching dec [{dec_lo:+.2f}, {dec_hi:+.2f})...",
          end="", flush=True)
    t0 = time.time()
    try:
        # 300s timeout: VizieR can stall on a busy server.
        with urllib.request.urlopen(url, timeout=300) as resp:
            data = resp.read()
        local.write_bytes(data)
        elapsed = time.time() - t0
        # Subtract the TSV header line from the row count.
        row_count = max(0, len(data.splitlines()) - 1)
        print(f" {row_count:7,d} rows in {elapsed:5.1f}s",
              flush=True)
        return local
    except Exception as e:
        print(f" FAILED: {e}", flush=True)
        # Write an empty marker so subsequent --skip-download
        # passes know the stripe was attempted.
        local.write_text("")
        return local


def ingest_stripe(cur: sqlite3.Cursor, tsv_path: Path) -> int:
    """
    Parse one Dec-stripe TSV (VizieR TAP output) and insert rows into
    the open SQLite cursor. Returns row count inserted. The TSV has a
    one-line header followed by tab-separated data; blank values come
    through as the empty string.
    """
    if not tsv_path.exists() or tsv_path.stat().st_size == 0:
        return 0

    # VizieR's TSV is plain tab-delimited, but uses CR+LF on some
    # responses. csv.reader handles both.
    inserted = 0
    rows_buf = []
    idx_buf = []
    with tsv_path.open("r", encoding="utf-8", errors="replace",
                        newline="") as f:
        reader = csv.reader(f, delimiter="\t")
        header = next(reader, None)
        if header is None:
            return 0
        # Expect: RAJ2000, DEJ2000, Vmag, Bmag, B-V (in that order
        # because the ADQL pinned the projection).
        for row in reader:
            if len(row) < 5:
                continue
            ra_s, dec_s, vmag_s, bmag_s, bv_s = row[:5]
            try:
                ra = float(ra_s)
                dec = float(dec_s)
            except (TypeError, ValueError):
                continue
            mag_v = _maybe_float(vmag_s)
            mag_b = _maybe_float(bmag_s)
            bv    = _maybe_float(bv_s)
            if mag_v is None:
                continue
            rows_buf.append((ra, dec, mag_v, mag_b, bv))
            inserted += 1

    if not rows_buf:
        return 0

    # Bulk insert: one executemany for the stars table, then capture
    # the rowid range to populate the R*tree in a second sweep. The
    # alternative (one INSERT per row + lastrowid lookup) is roughly
    # 30x slower because of Python<->sqlite churn.
    cur.execute("SELECT COALESCE(MAX(id), 0) FROM stars")
    first_new_id = int(cur.fetchone()[0]) + 1
    cur.executemany(
        "INSERT INTO stars(ra, dec, mag_v, mag_b, b_v, source) "
        "VALUES (?, ?, ?, ?, ?, 'APASS')", rows_buf)
    # R*tree rows match the stars table rowid 1:1.
    idx_buf = [
        (first_new_id + i, r[0], r[0], r[1], r[1])
        for i, r in enumerate(rows_buf)
    ]
    cur.executemany(
        "INSERT INTO stars_idx(id, min_ra, max_ra, min_dec, max_dec) "
        "VALUES (?, ?, ?, ?, ?)", idx_buf)
    return inserted


def _maybe_float(s):
    """Convert string to float, returning None on empty/invalid."""
    if s is None:
        return None
    s = s.strip()
    if not s:
        return None
    try:
        return float(s)
    except (TypeError, ValueError):
        return None


def build_index(out_path: Path, mag_limit: float, stripe_deg: float,
                cache: Path, skip_download: bool) -> int:
    """Download all Dec stripes, filter by mag, build SQLite + R*tree."""
    out_path.parent.mkdir(parents=True, exist_ok=True)
    if out_path.exists():
        out_path.unlink()

    print(f"Building {out_path} (mag_v <= {mag_limit}, "
          f"{stripe_deg:.1f} deg stripes)...", flush=True)
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
        conn.commit()

        # Generate stripe edges. Step by `stripe_deg` from -90 to +90.
        # APASS DR9 has no northern polar cap (DR10's gap, not ours),
        # but we still ask for the full range; the empty stripes come
        # back as a header-only TSV in <1s.
        dec_lo = -90.0
        total = 0
        while dec_lo < 90.0:
            dec_hi = min(90.0, dec_lo + stripe_deg)
            tsv = download_stripe(cache, dec_lo, dec_hi, mag_limit,
                                  skip_download)
            n = ingest_stripe(cur, tsv)
            total += n
            conn.commit()
            if n > 0:
                print(f"    ingested {n:7,d}, total {total:,}",
                      flush=True)
            dec_lo = dec_hi

        # ANALYZE so SQLite picks good query plans for the cone
        # searches PCC issues. Cheap on a freshly populated DB.
        print("Running ANALYZE...", flush=True)
        cur.execute("ANALYZE")
        conn.commit()
        print(f"Total stars indexed: {total:,}", flush=True)
        return total
    finally:
        conn.close()


def main() -> int:
    args = parse_args()
    root = repo_root()
    out = args.output or default_output(root)
    cache = cache_dir(root)

    print(f"APASS download script (VizieR TAP, II/336/apass9)")
    print(f"  Repo root:        {root}")
    print(f"  Output catalog:   {out}")
    print(f"  Cache dir:        {cache}")
    print(f"  Magnitude limit:  V <= {args.mag_limit}")
    print(f"  Dec stripe width: {args.stripe_deg} deg")
    print(f"  Skip download:    {args.skip_download}")
    print()

    t0 = time.time()
    total = build_index(out, args.mag_limit, args.stripe_deg,
                        cache, args.skip_download)
    elapsed = time.time() - t0

    print()
    print(f"Done in {elapsed/60:.1f} min. "
          f"APASS catalog ready at: {out}")
    print(f"  Size: {out.stat().st_size / 1024 / 1024:.1f} MB")
    print(f"  Stars: {total:,}")
    if not args.keep_cache:
        print(f"  TSV cache kept at {cache} for reuse.")
    print("Polaris will pick the catalog up automatically.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
