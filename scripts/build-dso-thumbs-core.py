#!/usr/bin/env python3
"""
THUMBPACK-1: build the CURATED CORE subset of DSO thumbnails.

The full dso-thumbs/ set is ~215 MB of DSS2 cutouts (one per catalogued object)
and is EXCLUDED from the distribution package (see NINA.Polaris.csproj) —
Polaris downloads it on demand. But the showpieces a user actually clicks
(Lagoon, Orion, Andromeda, every Messier) should render offline straight out of
the box, so a small curated subset ships bundled in:

    wwwroot/sky/data/skydata/dso-thumbs-core/<SLUG>.jpg

These files are committed as real bytes (NOT via Git LFS), so a checkout without
`lfs pull` — and every `dotnet publish` — still gets genuine JPEGs, not pointers.
That's the whole point of a separate folder: the big dso-thumbs/ is LFS + excluded
from publish, this stays small, real and shipped.

Curated set = every object with a common name (the named showpieces) + all
Messier. For each, copy its thumb under EVERY catalogue-form slug it answers to
(primary name + aliases), because the SKY card resolves a common name to a
catalogue code via search and then tries the row's name and each alias
(_resolveDsoThumb in app.js) — so a bundled thumb has to exist under whichever
form the search happens to return first.

Stdlib only (sqlite3 + shutil), same as the other build-dso-*.py scripts. Run it
after build-dso-thumbs.py has populated the full set locally:

    python scripts/build-dso-thumbs-core.py
"""
import os
import re
import shutil
import sqlite3

HERE = os.path.dirname(os.path.abspath(__file__))
DB = os.path.join(HERE, "..", "src", "NINA.Polaris", "wwwroot",
                  "catalogs", "dso", "dso.db")
THUMBS = os.path.join(HERE, "..", "src", "NINA.Polaris", "wwwroot", "sky",
                      "data", "skydata", "dso-thumbs")
CORE = os.path.join(HERE, "..", "src", "NINA.Polaris", "wwwroot", "sky",
                    "data", "skydata", "dso-thumbs-core")


def slug_for_name(name: str) -> str:
    """Mirror app.js dsoThumbUrl()'s slug rule so the bundled filenames match
    exactly what the runtime asks for."""
    if not name:
        return ""
    raw = name.strip()
    m = re.match(r"^sh\s*2\s*[-\s]?\s*0*(\d+)", raw, re.I) \
        or re.match(r"^sharpless\s*[-\s]?\s*0*(\d+)", raw, re.I)
    if m:
        return "SH2" + m.group(1)
    m = re.match(r"^([A-Za-z]+)\s*0*(\d+[A-Za-z]?)", raw)
    return (m.group(1) + m.group(2)).upper() if m else ""


def slug_for_catalog(catalog: str, catalog_id: str) -> str:
    """Mirror build-dso-thumbs.py's file naming."""
    return (catalog + catalog_id).upper().replace(" ", "")


def main() -> int:
    if not os.path.isfile(DB):
        print(f"catalog db missing: {DB}")
        return 1
    if not os.path.isdir(THUMBS):
        print(f"full thumb set missing: {THUMBS}\n"
              f"run scripts/build-dso-thumbs.py first")
        return 1

    con = sqlite3.connect(DB)
    cur = con.cursor()
    cur.execute("""
        SELECT catalog, catalog_id, name, common_name, aliases
        FROM objects
        WHERE (common_name IS NOT NULL AND common_name != '')
           OR catalog = 'M'
    """)
    rows = cur.fetchall()

    os.makedirs(CORE, exist_ok=True)
    # Clear any stale contents so a re-run reflects the current catalogue.
    for f in os.listdir(CORE):
        if f.endswith(".jpg"):
            os.remove(os.path.join(CORE, f))

    copied, missing, objects = 0, 0, 0
    for catalog, catalog_id, name, common_name, aliases in rows:
        objects += 1
        # Every slug this object might be requested under: its own catalogue
        # form, its parsed name, and each alias.
        slugs = set()
        slugs.add(slug_for_catalog(catalog, catalog_id))
        slugs.add(slug_for_name(name or ""))
        for alias in (aliases or "").split(","):
            slugs.add(slug_for_name(alias.strip()))
        slugs.discard("")

        found_any = False
        for slug in slugs:
            src = os.path.join(THUMBS, slug + ".jpg")
            if os.path.isfile(src):
                shutil.copy2(src, os.path.join(CORE, slug + ".jpg"))
                copied += 1
                found_any = True
        if not found_any:
            missing += 1

    size_mb = sum(os.path.getsize(os.path.join(CORE, f))
                  for f in os.listdir(CORE)) / (1024 * 1024)
    print(f"curated {objects} objects -> {copied} thumb files "
          f"({size_mb:.1f} MB) in dso-thumbs-core/")
    if missing:
        print(f"  ({missing} curated objects had no thumb in the full set — "
              f"expected for a few oddballs)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
