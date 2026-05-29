#!/usr/bin/env python3
"""
Build the expanded DSO catalog SQLite database that Polaris's SKY tab
search / Atlas filter / Tonight's-Best consume.

Output: src/NINA.Polaris/wwwroot/catalogs/dso/dso.db
        (bundled by the csproj Content Include into the publish
        output + Docker image; ~3-5 MB)

Catalogs ingested (CAT-1):

  1. OpenNGC     -- 13226 NGC/IC objects (also covers Messier + Caldwell
                    cross-IDs in the same file). CC-BY-SA-4.0.
                    https://github.com/mattiaverga/OpenNGC
  2. Sharpless 2 -- 313 HII regions. Vizier VII/20 (public domain).
  3. ARP         -- 338 peculiar galaxies. Vizier VII/192A (public domain).
  4. Abell PN    -- ~86 planetary nebulae. Vizier V/84 (public domain).
  5. Hickson CG  -- 100 compact galaxy groups. Vizier VII/213 (public).
  6. Abell GC    -- ~2700 Abell-Corwin-Olowin galaxy clusters.
                    Vizier VII/110A (public, mag-trimmed to brightest).

Schema:

    CREATE TABLE objects (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        catalog TEXT NOT NULL,    -- 'NGC' | 'IC' | 'M' | 'C' | 'Arp' | 'Sh2' | 'Abell-PN' | 'HCG' | 'AGC'
        catalog_id TEXT NOT NULL, -- '7331' | '273' | '92'
        name TEXT NOT NULL,       -- 'NGC 7331'
        common_name TEXT,
        type TEXT NOT NULL,       -- 'Spiral Galaxy' | 'HII Region' | ...
        ra_hours REAL NOT NULL,
        dec_deg REAL NOT NULL,
        magnitude REAL,
        size_arcmin REAL,
        constellation TEXT,       -- 3-letter IAU
        aliases TEXT              -- pipe-separated cross-refs
    );
    CREATE VIRTUAL TABLE objects_idx USING rtree(
        id, min_ra, max_ra, min_dec, max_dec
    );
    CREATE INDEX idx_objects_name      ON objects(name COLLATE NOCASE);
    CREATE INDEX idx_objects_catalog   ON objects(catalog, catalog_id);
    CREATE INDEX idx_objects_type      ON objects(type);
    CREATE INDEX idx_objects_magnitude ON objects(magnitude);

Each catalog ingest is independent: a network failure on one source
logs + continues, the resulting DB has whatever did download. Run
the script multiple times safely; the cache under scripts/.dso-cache/
short-circuits already-downloaded files.

Usage:
    python scripts/build-dso-catalog.py
    python scripts/build-dso-catalog.py --skip-download
    python scripts/build-dso-catalog.py --output /tmp/dso.db

Requirements: Python 3.8+, stdlib only (urllib + sqlite3 + csv).
"""

import argparse
import csv
import re
import sqlite3
import sys
import time
import urllib.parse
import urllib.request
from pathlib import Path

# ---------------------------------------------------------------------
# Data sources
# ---------------------------------------------------------------------

OPENNGC_CSV_URL = (
    "https://raw.githubusercontent.com/mattiaverga/OpenNGC/master/"
    "database_files/NGC.csv"
)

# CDS VizieR ASU-TSV endpoint. Simpler than TAP/ADQL: hands back a
# tab-separated dump of selected columns with one row per catalog
# entry. We always include `_RAJ2000` and `_DEJ2000`, virtual decimal-
# degree columns that Vizier auto-projects to J2000 regardless of the
# native catalog epoch (B1900/B1950/etc), so we never need to do
# precession ourselves.
VIZIER_ASU = "https://vizier.cds.unistra.fr/viz-bin/asu-tsv"
VIZIER_MAXREC = 200_000

# Per-catalog source-table + columns to request. Each entry maps to one
# vizier_asu_tsv() download. The first two columns are always
# `_RAJ2000`,`_DEJ2000` (degrees J2000), so the ingest function can
# index positionally without parsing a name-keyed header.
#
# ID column is the catalog's primary identifier (Sh2 number, Arp
# number, etc). Mag / size columns are optional and may be missing
# from individual rows.

VIZIER_CATALOGS = [
    # (cache_filename, source_table, [columns ...], ingest kwargs)
    {
        "cache":   "Sh2.tsv",
        "source":  "VII/20/catalog",
        "columns": ["_RAJ2000", "_DEJ2000", "Sh2", "Diam"],
        "ingest":  dict(catalog="Sh2", type_str="HII Region",
                        name_prefix="Sh2", id_col=2, ra_col=0,
                        dec_col=1, size_col=3, mag_col=None),
    },
    {
        "cache":   "Arp.tsv",
        "source":  "VII/192A/arplist",
        "columns": ["_RAJ2000", "_DEJ2000", "Arp", "VT", "dim1"],
        "ingest":  dict(catalog="Arp", type_str="Peculiar Galaxy",
                        name_prefix="Arp", id_col=2, ra_col=0,
                        dec_col=1, mag_col=3, size_col=4),
    },
    # V/84 (Strasbourg-ESO PN catalog, ~1500 PNe) was tried here but
    # asu-tsv exposes only B1950 sexagesimal columns + `_RA.icrs` as
    # sexagesimal strings (no decimal-degree virtual column). Skip
    # until ingest_vizier_tsv gains a sexagesimal-string fallback;
    # OpenNGC already supplies PNe with NGC/IC designations
    # ("Planetary Nebula" type), so the gap is small.
    {
        "cache":   "HCG.tsv",
        "source":  "VII/213/groups",
        "columns": ["_RAJ2000", "_DEJ2000", "HCG", "Totmag", "MCount"],
        "ingest":  dict(catalog="HCG", type_str="Galaxy Group",
                        name_prefix="HCG", id_col=2, ra_col=0,
                        dec_col=1, mag_col=3, size_col=None),
    },
    {
        # ACO galaxy clusters, trimmed to m10 < 17 mag (brightest ~500
        # of 2712) at ingest time via post-filter; the asu-tsv endpoint
        # has no WHERE clause so we filter in ingest_vizier_tsv.
        "cache":   "ACO.tsv",
        "source":  "VII/110A/table3",
        "columns": ["_RAJ2000", "_DEJ2000", "ACO", "m10", "Rich"],
        "ingest":  dict(catalog="AGC", type_str="Galaxy Cluster",
                        name_prefix="Abell", id_col=2, ra_col=0,
                        dec_col=1, mag_col=3, size_col=None,
                        mag_max=17.0),
    },
]

# ---------------------------------------------------------------------
# Type taxonomy: normalize wildly varying source labels into the
# free-form strings the existing SkyCatalogService already uses
# (so the type-filter dropdown stays coherent across all catalogs).
# ---------------------------------------------------------------------

# OpenNGC uses 2-char codes per object. Map them to friendly type
# strings matching what the legacy hardcoded SkyCatalogService used.
OPENNGC_TYPE_MAP = {
    "*":      "Star",
    "**":     "Double Star",
    "*Ass":   "Asterism",
    "OCl":    "Open Cluster",
    "GCl":    "Globular Cluster",
    "Cl+N":   "Cluster + Nebula",
    "G":      "Galaxy",
    "GPair":  "Galaxy Pair",
    "GTrpl":  "Galaxy Triplet",
    "GGroup": "Galaxy Group",
    "PN":     "Planetary Nebula",
    "HII":    "HII Region",
    "EmN":    "Emission Nebula",
    "Neb":    "Nebula",
    "RfN":    "Reflection Nebula",
    "SNR":    "Supernova Remnant",
    "Nova":   "Nova",
    "NonEx":  "Non-existent",
    "Dup":    "Duplicate",
    "Other":  "Other",
}


def parse_args() -> argparse.Namespace:
    p = argparse.ArgumentParser(
        description="Build the expanded DSO catalog SQLite for Polaris")
    p.add_argument("--output", type=Path, default=None,
        help="Output path for dso.db. Default is "
             "src/NINA.Polaris/wwwroot/catalogs/dso/dso.db")
    p.add_argument("--skip-download", action="store_true",
        help="Reuse already-downloaded source files in scripts/.dso-cache/")
    p.add_argument("--openngc-only", action="store_true",
        help="Skip Vizier downloads, only ingest OpenNGC. Useful for "
             "quick iteration when Vizier is unreachable.")
    return p.parse_args()


def repo_root() -> Path:
    return Path(__file__).resolve().parent.parent


def default_output(root: Path) -> Path:
    return (root / "src" / "NINA.Polaris" / "wwwroot"
            / "catalogs" / "dso" / "dso.db")


def cache_dir(root: Path) -> Path:
    d = root / "scripts" / ".dso-cache"
    d.mkdir(parents=True, exist_ok=True)
    return d


def http_get(url: str, dest: Path, timeout: int = 300) -> bool:
    """Download URL to dest. Returns True on success."""
    print(f"  GET {url[:90]}...", end="", flush=True)
    t0 = time.time()
    try:
        req = urllib.request.Request(url, headers={
            "User-Agent": "NINA.Polaris-DSO-Builder/1.0"
        })
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            data = resp.read()
        dest.write_bytes(data)
        elapsed = time.time() - t0
        size_kb = len(data) / 1024
        print(f" {size_kb:7.1f} KB in {elapsed:4.1f}s", flush=True)
        return True
    except Exception as e:
        print(f" FAILED: {e}", flush=True)
        return False


def vizier_asu_tsv(source: str, columns: list, dest: Path,
                   timeout: int = 300) -> bool:
    """
    Pull a Vizier catalog via the asu-tsv REST endpoint. `source` is
    the table identifier (e.g. "VII/20/catalog"), `columns` is a list
    of column names to project. Returns True on download success.

    The returned file has Vizier's standard format: lines starting
    with '#' are metadata, then a single column-name header (TSV),
    a units row, a row of dashes acting as a delimiter, then data.
    """
    # Build the asu query string. urlencode can't handle repeated
    # `-out=` keys natively, so build it manually.
    parts = [
        ("-source", source),
        ("-out.max", str(VIZIER_MAXREC)),
    ]
    for c in columns:
        parts.append(("-out", c))
    query = "&".join(f"{urllib.parse.quote(k)}={urllib.parse.quote(v)}"
                     for k, v in parts)
    url = f"{VIZIER_ASU}?{query}"
    return http_get(url, dest, timeout=timeout)


# ---------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------

def maybe_float(s):
    if s is None:
        return None
    s = str(s).strip()
    if not s or s.lower() in ("nan", "null", "--"):
        return None
    try:
        return float(s)
    except (TypeError, ValueError):
        return None


def parse_sexagesimal_ra(s: str):
    """Parse 'HH MM SS.S' or 'HH:MM:SS' to decimal hours. None on fail."""
    if not s:
        return None
    s = s.strip().replace(":", " ")
    parts = s.split()
    if not parts:
        return None
    try:
        h = float(parts[0])
        m = float(parts[1]) if len(parts) > 1 else 0
        sec = float(parts[2]) if len(parts) > 2 else 0
        return h + m / 60.0 + sec / 3600.0
    except (TypeError, ValueError):
        return None


def parse_sexagesimal_dec(s: str):
    """Parse '+DD MM SS' or '-DD:MM:SS' to decimal degrees. None on fail."""
    if not s:
        return None
    s = s.strip().replace(":", " ")
    sign = 1.0
    if s.startswith("-"):
        sign = -1.0
        s = s[1:].strip()
    elif s.startswith("+"):
        s = s[1:].strip()
    parts = s.split()
    if not parts:
        return None
    try:
        d = float(parts[0])
        m = float(parts[1]) if len(parts) > 1 else 0
        sec = float(parts[2]) if len(parts) > 2 else 0
        return sign * (d + m / 60.0 + sec / 3600.0)
    except (TypeError, ValueError):
        return None


# ---------------------------------------------------------------------
# Ingest functions per catalog. Each appends (catalog, catalog_id,
# name, common_name, type, ra_hours, dec_deg, magnitude, size_arcmin,
# constellation, aliases) tuples to the caller's accumulator list.
# Each is independent: a failure logs + continues with what loaded.
# ---------------------------------------------------------------------

def ingest_openngc(csv_path: Path, rows: list) -> int:
    """Parse OpenNGC's NGC.csv. Returns # rows added."""
    if not csv_path.exists() or csv_path.stat().st_size == 0:
        print("  ingest_openngc: source file empty, skipped")
        return 0
    added = 0
    with csv_path.open("r", encoding="utf-8", errors="replace") as f:
        # OpenNGC uses ';' delimiter and a header row.
        reader = csv.DictReader(f, delimiter=";")
        for row in reader:
            name = (row.get("Name") or "").strip()
            if not name:
                continue
            # Catalog + id split: "NGC0224" -> ("NGC", "224"), "IC0010" -> ("IC", "10").
            m = re.match(r"^(NGC|IC)(\d+)$", name)
            if not m:
                continue
            cat = m.group(1)
            cat_id = m.group(2).lstrip("0") or "0"
            ra_h = parse_sexagesimal_ra(row.get("RA"))
            dec_d = parse_sexagesimal_dec(row.get("Dec"))
            if ra_h is None or dec_d is None:
                continue
            type_code = (row.get("Type") or "").strip()
            type_str = OPENNGC_TYPE_MAP.get(type_code, type_code or "Other")
            # Skip dup/non-existent entries — they pollute the search.
            if type_code in ("Dup", "NonEx"):
                continue
            v_mag = maybe_float(row.get("V-Mag"))
            b_mag = maybe_float(row.get("B-Mag"))
            mag = v_mag if v_mag is not None else b_mag
            maj_ax = maybe_float(row.get("MajAx"))   # arcmin
            constellation = (row.get("Const") or "").strip() or None
            messier = (row.get("M") or "").strip()
            common_name = (row.get("Common names") or "").strip() or None
            # Aliases: Messier number if any, plus IC/NGC cross-refs.
            aliases = []
            if messier:
                # Could be '031' or '110' (zero-padded).
                m_id = messier.lstrip("0") or "0"
                aliases.append(f"M{m_id}")
            ngc_xref = (row.get("NGC") or "").strip()
            if ngc_xref and cat != "NGC":
                aliases.append(f"NGC {ngc_xref.lstrip('0') or '0'}")
            ic_xref = (row.get("IC") or "").strip()
            if ic_xref and cat != "IC":
                aliases.append(f"IC {ic_xref.lstrip('0') or '0'}")
            rows.append((
                cat, cat_id, f"{cat} {cat_id}", common_name, type_str,
                ra_h, dec_d, mag, maj_ax, constellation,
                "|".join(aliases) if aliases else None
            ))
            added += 1
            # Also emit an M-named row for any Messier-tagged object so a
            # search for "M31" matches directly (not just via aliases).
            if messier:
                m_id = messier.lstrip("0") or "0"
                rows.append((
                    "M", m_id, f"M{m_id}", common_name, type_str,
                    ra_h, dec_d, mag, maj_ax, constellation,
                    f"{cat} {cat_id}"
                ))
                added += 1
    print(f"  ingest_openngc: +{added} rows")
    return added


def ingest_vizier_tsv(tsv_path: Path, rows: list, *,
                     catalog: str, type_str: str,
                     ra_col: int, dec_col: int, id_col: int,
                     mag_col=None, size_col=None,
                     name_prefix: str = None,
                     mag_max: float = None) -> int:
    """
    Generic Vizier asu-tsv ingest. The body format is:

        #...comments...
        <COLNAMES>      (tab-separated header line)
        <UNITS>         (e.g. "deg\\tdeg\\t \\tarcmin")
        ----------\\t-------\\t...    (dashes delimiter)
        <data>          (one row per object until EOF or blank line)

    RA/Dec come from the `_RAJ2000` / `_DEJ2000` virtual columns we
    always project first, so they're decimal degrees regardless of
    the catalog's native epoch. Convert RA degrees -> hours here.
    """
    if not tsv_path.exists() or tsv_path.stat().st_size == 0:
        print(f"  ingest_vizier_tsv [{catalog}]: source empty, skipped")
        return 0
    added = 0
    skipped_dim = 0
    in_data = False
    saw_header = False
    saw_dashes = False
    with tsv_path.open("r", encoding="utf-8", errors="replace") as f:
        for raw in f:
            line = raw.rstrip("\n\r")
            if not line:
                # Blank line ends the data block in asu-tsv format.
                if in_data:
                    break
                continue
            if line.startswith("#"):
                continue
            # First non-comment line is the column header (TSV). Skip.
            if not saw_header:
                saw_header = True
                continue
            # Then comes the units row, then a dashes row, then data.
            stripped = line.strip()
            if set(stripped) <= {"-", "\t", " "}:
                saw_dashes = True
                in_data = True
                continue
            if not in_data:
                # Units line.
                continue
            parts = line.split("\t")
            if len(parts) <= max(ra_col, dec_col, id_col):
                continue
            cat_id = parts[id_col].strip()
            if not cat_id:
                continue
            ra_deg = maybe_float(parts[ra_col])
            dec_d  = maybe_float(parts[dec_col])
            if ra_deg is None or dec_d is None:
                continue
            ra_h = ra_deg / 15.0
            mag = (maybe_float(parts[mag_col])
                   if mag_col is not None and mag_col < len(parts) else None)
            size_arc = (maybe_float(parts[size_col])
                        if size_col is not None and size_col < len(parts) else None)
            if mag_max is not None and (mag is None or mag > mag_max):
                skipped_dim += 1
                continue
            name = (f"{name_prefix} {cat_id}"
                    if name_prefix is not None else f"{catalog} {cat_id}")
            rows.append((
                catalog, cat_id, name, None, type_str,
                ra_h, dec_d, mag, size_arc, None, None
            ))
            added += 1
    note = f", skipped {skipped_dim} dimmer than mag {mag_max}" if mag_max else ""
    print(f"  ingest_vizier_tsv [{catalog}]: +{added} rows{note}")
    return added


# ---------------------------------------------------------------------
# Caldwell cross-reference. The OpenNGC CSV doesn't carry an explicit
# Caldwell column, but Caldwell IS a 109-entry hand-picked subset of
# NGC/IC objects with a fixed assignment. Embed the mapping here and
# emit a duplicate row per Caldwell entry tagged 'C' so a search for
# "C14" matches without needing OpenNGC to have the cross-ref column.
# Source: official Caldwell list (P. Moore 1995).
# ---------------------------------------------------------------------

# (Caldwell_number, NGC/IC name, common_name or None)
CALDWELL = [
    ("1",  "NGC 188",  "NGC 188"),
    ("2",  "NGC 40",   "Bow-Tie Nebula"),
    ("3",  "NGC 4236", None),
    ("4",  "NGC 7023", "Iris Nebula"),
    ("5",  "IC 342",   None),
    ("6",  "NGC 6543", "Cat's Eye Nebula"),
    ("7",  "NGC 2403", None),
    ("8",  "NGC 559",  None),
    ("9",  "Sh2-155",  "Cave Nebula"),
    ("10", "NGC 663",  None),
    ("11", "NGC 7635", "Bubble Nebula"),
    ("12", "NGC 6946", "Fireworks Galaxy"),
    ("13", "NGC 457",  "Owl Cluster"),
    ("14", "NGC 869",  "Double Cluster"),  # (NGC 869+NGC 884)
    ("15", "NGC 6826", "Blinking Planetary"),
    ("16", "NGC 7243", None),
    ("17", "NGC 147",  None),
    ("18", "NGC 185",  None),
    ("19", "IC 5146",  "Cocoon Nebula"),
    ("20", "NGC 7000", "North America Nebula"),
    ("21", "NGC 4449", None),
    ("22", "NGC 7662", "Blue Snowball"),
    ("23", "NGC 891",  None),
    ("24", "NGC 1275", "Perseus A"),
    ("25", "NGC 2419", None),
    ("26", "NGC 4244", "Silver Needle Galaxy"),
    ("27", "NGC 6888", "Crescent Nebula"),
    ("28", "NGC 752",  None),
    ("29", "NGC 5005", None),
    ("30", "NGC 7331", None),
    ("31", "IC 405",   "Flaming Star Nebula"),
    ("32", "NGC 4631", "Whale Galaxy"),
    ("33", "NGC 6992", "Eastern Veil Nebula"),
    ("34", "NGC 6960", "Western Veil Nebula"),
    ("35", "NGC 4889", None),
    ("36", "NGC 4559", None),
    ("37", "NGC 6885", None),
    ("38", "NGC 4565", "Needle Galaxy"),
    ("39", "NGC 2392", "Eskimo Nebula"),
    ("40", "NGC 3626", None),
    ("41", "Hyades",   "Hyades"),  # Hyades cluster, not NGC
    ("42", "NGC 7006", None),
    ("43", "NGC 7814", None),
    ("44", "NGC 7479", None),
    ("45", "NGC 5248", None),
    ("46", "NGC 2261", "Hubble's Variable Nebula"),
    ("47", "NGC 6934", None),
    ("48", "NGC 2775", None),
    ("49", "NGC 2237", "Rosette Nebula"),
    ("50", "NGC 2244", None),
    ("51", "IC 1613",  None),
    ("52", "NGC 4697", None),
    ("53", "NGC 3115", "Spindle Galaxy"),
    ("54", "NGC 2506", None),
    ("55", "NGC 7009", "Saturn Nebula"),
    ("56", "NGC 246",  None),
    ("57", "NGC 6822", "Barnard's Galaxy"),
    ("58", "NGC 2360", None),
    ("59", "NGC 3242", "Ghost of Jupiter"),
    ("60", "NGC 4038", "Antennae"),
    ("61", "NGC 4039", "Antennae"),
    ("62", "NGC 247",  None),
    ("63", "NGC 7293", "Helix Nebula"),
    ("64", "NGC 2362", None),
    ("65", "NGC 253",  "Sculptor Galaxy"),
    ("66", "NGC 5694", None),
    ("67", "NGC 1097", None),
    ("68", "NGC 6729", None),
    ("69", "NGC 6302", "Bug Nebula"),
    ("70", "NGC 300",  None),
    ("71", "NGC 2477", None),
    ("72", "NGC 55",   None),
    ("73", "NGC 1851", None),
    ("74", "NGC 3132", "Eight-Burst Nebula"),
    ("75", "NGC 6124", None),
    ("76", "NGC 6231", None),
    ("77", "NGC 5128", "Centaurus A"),
    ("78", "NGC 6541", None),
    ("79", "NGC 3201", None),
    ("80", "NGC 5139", "Omega Centauri"),
    ("81", "NGC 6352", None),
    ("82", "NGC 6193", None),
    ("83", "NGC 4945", None),
    ("84", "NGC 5286", None),
    ("85", "IC 2391",  "Omicron Velorum"),
    ("86", "NGC 6397", None),
    ("87", "NGC 1261", None),
    ("88", "NGC 5823", None),
    ("89", "NGC 6087", None),
    ("90", "NGC 2867", None),
    ("91", "NGC 3532", "Wishing Well Cluster"),
    ("92", "NGC 3372", "Eta Carinae Nebula"),
    ("93", "NGC 6752", None),
    ("94", "NGC 4755", "Jewel Box"),
    ("95", "NGC 6025", None),
    ("96", "NGC 2516", None),
    ("97", "NGC 3766", None),
    ("98", "NGC 4609", None),
    ("99", "Coalsack", "Coalsack"),  # dark nebula, no NGC
    ("100","IC 2944",  "Lambda Centauri Nebula"),
    ("101","NGC 6744", None),
    ("102","IC 2602",  "Southern Pleiades"),
    ("103","NGC 2070", "Tarantula Nebula"),
    ("104","NGC 362",  None),
    ("105","NGC 4833", None),
    ("106","NGC 104",  "47 Tucanae"),
    ("107","NGC 6101", None),
    ("108","NGC 4372", None),
    ("109","NGC 3195", None),
]


def add_caldwell_xrefs(rows: list) -> int:
    """Emit C-named duplicates pointing to the NGC/IC entry."""
    # Build an index of existing NGC/IC rows for fast lookup.
    index = {}
    for r in rows:
        cat, cat_id, name = r[0], r[1], r[2]
        if cat in ("NGC", "IC"):
            index[name] = r
    added = 0
    for c_id, ref, common in CALDWELL:
        m = re.match(r"^(NGC|IC) (\d+)$", ref)
        if not m:
            # Hyades / Coalsack / Sh2-155 don't have NGC/IC; skip.
            # (They'd need a separate pinned entry; out of scope for v1.)
            continue
        src = index.get(ref)
        if src is None:
            continue
        # Inherit type / coords / mag / size / constellation from the NGC/IC.
        rows.append((
            "C", c_id, f"C{c_id}",
            common or src[3],            # common_name
            src[4],                       # type
            src[5], src[6],               # ra_h, dec_d
            src[7], src[8],               # mag, size
            src[9],                       # constellation
            ref                           # alias: the NGC/IC name
        ))
        added += 1
    print(f"  add_caldwell_xrefs: +{added} rows")
    return added


# ---------------------------------------------------------------------
# DB writer
# ---------------------------------------------------------------------

def build_db(out_path: Path, rows: list) -> int:
    out_path.parent.mkdir(parents=True, exist_ok=True)
    if out_path.exists():
        out_path.unlink()
    conn = sqlite3.connect(out_path)
    try:
        cur = conn.cursor()
        cur.executescript("""
            CREATE TABLE objects (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                catalog TEXT NOT NULL,
                catalog_id TEXT NOT NULL,
                name TEXT NOT NULL,
                common_name TEXT,
                type TEXT NOT NULL,
                ra_hours REAL NOT NULL,
                dec_deg REAL NOT NULL,
                magnitude REAL,
                size_arcmin REAL,
                constellation TEXT,
                aliases TEXT
            );
            CREATE VIRTUAL TABLE objects_idx USING rtree(
                id, min_ra, max_ra, min_dec, max_dec
            );
        """)
        cur.executemany("""
            INSERT INTO objects(catalog, catalog_id, name, common_name,
                                type, ra_hours, dec_deg, magnitude,
                                size_arcmin, constellation, aliases)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
        """, rows)
        # Populate R*tree (id maps 1:1 to objects.id via AUTOINCREMENT).
        cur.execute("""
            INSERT INTO objects_idx(id, min_ra, max_ra, min_dec, max_dec)
            SELECT id, ra_hours, ra_hours, dec_deg, dec_deg FROM objects
        """)
        # Secondary indexes for the non-spatial query paths.
        cur.executescript("""
            CREATE INDEX idx_objects_name      ON objects(name COLLATE NOCASE);
            CREATE INDEX idx_objects_catalog   ON objects(catalog, catalog_id);
            CREATE INDEX idx_objects_type      ON objects(type);
            CREATE INDEX idx_objects_magnitude ON objects(magnitude);
        """)
        cur.execute("ANALYZE")
        conn.commit()
        return len(rows)
    finally:
        conn.close()


# ---------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------

def main() -> int:
    args = parse_args()
    root = repo_root()
    out = args.output or default_output(root)
    cache = cache_dir(root)

    print(f"DSO catalog builder")
    print(f"  Output:          {out}")
    print(f"  Cache:           {cache}")
    print(f"  Skip-download:   {args.skip_download}")
    print(f"  OpenNGC only:    {args.openngc_only}")
    print()

    rows = []

    # 1. OpenNGC (mandatory; without this the DB is mostly empty).
    openngc_csv = cache / "OpenNGC.csv"
    if args.skip_download and openngc_csv.exists():
        print("OpenNGC: reusing cached file.")
    else:
        print("OpenNGC: downloading...")
        if not http_get(OPENNGC_CSV_URL, openngc_csv):
            print("OpenNGC download failed; aborting build.", file=sys.stderr)
            return 2
    ingest_openngc(openngc_csv, rows)

    # 2. Caldwell cross-refs (purely synthesized from OpenNGC rows).
    add_caldwell_xrefs(rows)

    if not args.openngc_only:
        # 3. Vizier catalogs (Sh2, Arp, PN-G, HCG, ACO). Each entry
        #    in VIZIER_CATALOGS describes its asu-tsv columns + how
        #    to ingest into the unified objects table.
        for cat in VIZIER_CATALOGS:
            tsv = cache / cat["cache"]
            if not (args.skip_download and tsv.exists() and tsv.stat().st_size > 0):
                vizier_asu_tsv(cat["source"], cat["columns"], tsv)
            ingest_vizier_tsv(tsv, rows, **cat["ingest"])

    print()
    print(f"Total rows to write: {len(rows):,}")
    n = build_db(out, rows)
    size_mb = out.stat().st_size / 1024 / 1024
    print(f"Wrote {n:,} rows to {out} ({size_mb:.2f} MB)")
    print("Polaris will pick the catalog up automatically on next boot.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
