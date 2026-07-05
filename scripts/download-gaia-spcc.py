#!/usr/bin/env python3
"""
Build a Gaia DR3 sampled-spectra subset for the SPCC "gaia" spectral source.

Output: src/NINA.Polaris/wwwroot/catalogs/spcc/gaia-spcc.db (SQLite)

STATUS: scaffold. The Pickles source (scripts/download-pickles.py) is the
recommended offline default and is fully wired. This Gaia path is the
optional per-star measured-spectra upgrade (PixInsight/Siril-grade). The
converter here documents the intended schema and query so it can be
completed without re-deciding the design; SpccDatabase already reports
GaiaAvailable and prefers it when the DB is present.

Why heavy: Gaia DR3 BP/RP externally-calibrated sampled spectra are large.
A practical subset caps by magnitude (G < ~16) and stores each star's
sampled spectrum resampled onto the SPCC grid (380-720 nm), keyed by an
rtree over RA/Dec so SpccService can cone-search per field like APASS.

Intended schema (mirrors the APASS rtree pattern):

    CREATE TABLE stars (
        id INTEGER PRIMARY KEY,
        ra REAL NOT NULL, dec REAL NOT NULL,   -- degrees
        g_mag REAL,
        flux BLOB NOT NULL                      -- float32[len(GRID)], normalised
    );
    CREATE VIRTUAL TABLE stars_idx USING rtree(
        id, min_ra, max_ra, min_dec, max_dec
    );

Data source: Gaia DR3 gaiadr3.xp_sampled_mean_spectrum via the ESA Gaia
TAP+DataLink service, or the bulk XP-continuous files converted with
GaiaXPy. See https://www.cosmos.esa.int/web/gaia/dr3 .

Usage (once completed):
    python scripts/download-gaia-spcc.py --mag-limit 16 --region <ra> <dec> <radius>

Requirements (planned): Python 3.9+, numpy, astroquery or GaiaXPy.
"""

import sys


def main():
    print(
        "download-gaia-spcc.py is a scaffold. Use scripts/download-pickles.py\n"
        "for the offline SPCC spectral upgrade; the Gaia per-star path is a\n"
        "planned enhancement (see this file's docstring for the schema/query).",
        file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main())
