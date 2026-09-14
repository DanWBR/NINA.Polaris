#!/usr/bin/env python3
"""
Add the stars people actually search for to the DSO catalogue database.

The SKY search knew deep-sky objects (dso.db) and whatever the map engine
answers for (planets, a few bright stars by proper name). A Wolf-Rayet star
typed as "WR 134" or "V1764 Cyg", a carbon star as "Y CVn", a Bayer name as
"beta Ori", found nothing. This adds three star tables to the same SQLite
file the search already reads, so every row is searchable by proper name,
Bayer/Flamsteed, HR/HD/SAO/HIP number and variable-star designation.

Sources (all public domain or CC-BY, cached under scripts/.dso-cache/):

  1. Yale Bright Star Catalogue, 5th ed. (Hoffleit & Warren 1991),
     VizieR V/50: 9,110 stars to V ~6.5 with HR, HD, SAO, Bayer/Flamsteed
     and variable designations.
  2. IAU Catalog of Star Names (WGSN): ~450 proper names, attached to the
     BSC row by HD/HIP; names of stars not in the BSC (faint exoplanet
     hosts) become rows of their own.
  3. VIIth Catalogue of Galactic Wolf-Rayet Stars (van der Hucht 2001),
     VizieR III/215: 226 WR stars with HD, HIP, GCVS designations,
     spectral type and v magnitude.

Rows are written with catalog in ('HR', 'Star', 'WR'); running again
deletes those first, so the script is idempotent and never touches the
deep-sky rows.

Usage:
    python scripts/build-star-catalog.py
    python scripts/build-star-catalog.py --skip-download
    python scripts/build-star-catalog.py --db path/to/dso.db

Requirements: Python 3.8+, stdlib only.
"""

import argparse
import re
import sqlite3
import sys
import urllib.request
from pathlib import Path

HERE = Path(__file__).resolve().parent
CACHE = HERE / ".dso-cache"
DEFAULT_DB = HERE.parent / "src" / "NINA.Polaris" / "wwwroot" / "catalogs" / "dso" / "dso.db"

VIZIER = "https://vizier.cds.unistra.fr/viz-bin/asu-tsv"
SOURCES = {
    "bsc5.tsv": VIZIER + "?-source=V/50/catalog&-out.max=unlimited"
                "&-out=HR,Name,HD,SAO,VarID,RAJ2000,DEJ2000,Vmag,SpType",
    "wr_all.tsv": VIZIER + "?-source=III/215&-out.max=unlimited&-out.all=1",
    "IAU-CSN.txt": "https://www.pas.rochester.edu/~emamajek/WGSN/IAU-CSN.txt",
}

# SIMBAD spells a few Greek abbreviations differently (alf, tet, ksi, khi);
# both forms are searchable.
GREEK_ALT = {"alp": ["alf"], "the": ["tet"], "xi": ["ksi"], "chi": ["khi"]}

GREEK = {
    "alp": ("alpha", "α"), "bet": ("beta", "β"), "gam": ("gamma", "γ"), "del": ("delta", "δ"),
    "eps": ("epsilon", "ε"), "zet": ("zeta", "ζ"), "eta": ("eta", "η"), "the": ("theta", "θ"),
    "iot": ("iota", "ι"), "kap": ("kappa", "κ"), "lam": ("lambda", "λ"), "mu": ("mu", "μ"),
    "nu": ("nu", "ν"), "xi": ("xi", "ξ"), "omi": ("omicron", "ο"), "pi": ("pi", "π"),
    "rho": ("rho", "ρ"), "sig": ("sigma", "σ"), "tau": ("tau", "τ"), "ups": ("upsilon", "υ"),
    "phi": ("phi", "φ"), "chi": ("chi", "χ"), "psi": ("psi", "ψ"), "ome": ("omega", "ω"),
}
SUPERSCRIPT = {"1": "¹", "2": "²", "3": "³", "4": "⁴", "5": "⁵", "6": "⁶", "7": "⁷", "8": "⁸", "9": "⁹"}

# Constellation genitives, so "V1764 Cyg" is also findable as "V1764 Cygni".
GENITIVE = {
    "And": "Andromedae", "Ant": "Antliae", "Aps": "Apodis", "Aqr": "Aquarii", "Aql": "Aquilae",
    "Ara": "Arae", "Ari": "Arietis", "Aur": "Aurigae", "Boo": "Bootis", "Cae": "Caeli",
    "Cam": "Camelopardalis", "Cnc": "Cancri", "CVn": "Canum Venaticorum", "CMa": "Canis Majoris",
    "CMi": "Canis Minoris", "Cap": "Capricorni", "Car": "Carinae", "Cas": "Cassiopeiae",
    "Cen": "Centauri", "Cep": "Cephei", "Cet": "Ceti", "Cha": "Chamaeleontis", "Cir": "Circini",
    "Col": "Columbae", "Com": "Comae Berenices", "CrA": "Coronae Australis", "CrB": "Coronae Borealis",
    "Crv": "Corvi", "Crt": "Crateris", "Cru": "Crucis", "Cyg": "Cygni", "Del": "Delphini",
    "Dor": "Doradus", "Dra": "Draconis", "Equ": "Equulei", "Eri": "Eridani", "For": "Fornacis",
    "Gem": "Geminorum", "Gru": "Gruis", "Her": "Herculis", "Hor": "Horologii", "Hya": "Hydrae",
    "Hyi": "Hydri", "Ind": "Indi", "Lac": "Lacertae", "Leo": "Leonis", "LMi": "Leonis Minoris",
    "Lep": "Leporis", "Lib": "Librae", "Lup": "Lupi", "Lyn": "Lyncis", "Lyr": "Lyrae",
    "Men": "Mensae", "Mic": "Microscopii", "Mon": "Monocerotis", "Mus": "Muscae", "Nor": "Normae",
    "Oct": "Octantis", "Oph": "Ophiuchi", "Ori": "Orionis", "Pav": "Pavonis", "Peg": "Pegasi",
    "Per": "Persei", "Phe": "Phoenicis", "Pic": "Pictoris", "Psc": "Piscium", "PsA": "Piscis Austrini",
    "Pup": "Puppis", "Pyx": "Pyxidis", "Ret": "Reticuli", "Sge": "Sagittae", "Sgr": "Sagittarii",
    "Sco": "Scorpii", "Scl": "Sculptoris", "Sct": "Scuti", "Ser": "Serpentis", "Sex": "Sextantis",
    "Tau": "Tauri", "Tel": "Telescopii", "Tri": "Trianguli", "TrA": "Trianguli Australis",
    "Tuc": "Tucanae", "UMa": "Ursae Majoris", "UMi": "Ursae Minoris", "Vel": "Velorum",
    "Vir": "Virginis", "Vol": "Volantis", "Vul": "Vulpeculae",
}
GENITIVE_CI = {k.lower(): v for k, v in GENITIVE.items()}

# Names astrophotographers use that no catalogue carries as such.
NICKNAMES = {
    "R Lep": "Hind's Crimson Star", "Y CVn": "La Superba", "mu Cep": "Herschel's Garnet Star",
    "P Cyg": "P Cygni", "V Hya": "V Hydrae", "T Lyr": "T Lyrae", "RS Cyg": "RS Cygni",
    "WR 124": "Merrill's Star", "WR 6": "EZ Canis Majoris", "WR 136": "Central star of the Crescent Nebula",
    "eta Car": "Eta Carinae",
}


def log(msg):
    print(msg, flush=True)


def download(name, url, skip):
    CACHE.mkdir(exist_ok=True)
    path = CACHE / name
    if path.exists() and (skip or path.stat().st_size > 1000):
        return path
    log(f"downloading {name}")
    with urllib.request.urlopen(url, timeout=120) as r, open(path, "wb") as f:
        f.write(r.read())
    return path


def vizier_rows(text):
    """Yield (header, rows) for every table block in an ASU-TSV dump."""
    for block in text.split("\n\n"):
        lines = [l for l in block.split("\n") if l and not l.startswith("#")]
        if len(lines) < 3:
            continue
        header = [h.strip() for h in lines[0].split("\t")]
        rows = [r.split("\t") for r in lines[3:]] if lines[2].startswith("---") else [r.split("\t") for r in lines[2:]]
        yield header, [[c.strip() for c in r] for r in rows]


def hms_to_hours(s):
    p = s.replace(":", " ").split()
    if len(p) < 2:
        return None
    h, m = float(p[0]), float(p[1]); sec = float(p[2]) if len(p) > 2 else 0.0
    return h + m / 60 + sec / 3600


def dms_to_deg(s):
    s = s.strip()
    sign = -1 if s.startswith("-") else 1
    p = s.lstrip("+-").replace(":", " ").split()
    if len(p) < 2:
        return None
    d, m = float(p[0]), float(p[1]); sec = float(p[2]) if len(p) > 2 else 0.0
    return sign * (d + m / 60 + sec / 3600)


def constellation_genitive_alias(designation):
    """'V1764 Cyg' -> 'V1764 Cygni'; None when the last token is not a constellation."""
    m = re.match(r"^(.*)\s+([A-Za-z]{3})$", designation.strip())
    if not m:
        return None
    gen = GENITIVE_CI.get(m.group(2).lower())
    return f"{m.group(1)} {gen}" if gen else None


def parse_bsc_name(raw):
    """'19Bet Ori' -> (flamsteed 19, bayer 'Bet', superscript '', const 'Ori')."""
    raw = raw.strip()
    m = re.match(r"^(\d+)?\s*([A-Za-z]{1,3})?(\d)?\s*([A-Z][A-Za-z]{2})$", raw)
    if not m:
        return None
    return m.group(1), m.group(2), m.group(3) or "", m.group(4)


def star_type(sptype, var_id):
    sp = (sptype or "").strip()
    if re.match(r"^[CN]\d|^C[-\d]|^R\d", sp):
        return "Carbon Star"
    if sp.startswith("W"):
        return "Wolf-Rayet Star"
    if var_id:
        return "Variable Star"
    return "Star"


def build(db_path, skip_download):
    bsc = download("bsc5.tsv", SOURCES["bsc5.tsv"], skip_download)
    wr = download("wr_all.tsv", SOURCES["wr_all.tsv"], skip_download)
    iau = download("IAU-CSN.txt", SOURCES["IAU-CSN.txt"], skip_download)

    # ---- IAU proper names, keyed by HD and HIP -------------------------
    names_by_hd, names_by_hip, orphan_names = {}, {}, []
    for line in iau.read_text(encoding="utf-8", errors="replace").splitlines():
        if not line.strip() or line.startswith("#") or line.startswith("$"):
            continue
        ascii_name = line[:18].strip()
        pretty = line[18:36].strip()
        designation = line[36:49].strip()
        body = line[:134]
        toks = body.split()
        if len(toks) < 10:
            continue
        date, dec_s, ra_s, hd, hip, band, mag, wds, comp, con = toks[-1], toks[-2], toks[-3], toks[-4], toks[-5], toks[-6], toks[-7], toks[-8], toks[-9], toks[-10]
        try:
            ra_deg, dec_deg = float(ra_s), float(dec_s)
        except ValueError:
            continue
        try:
            vmag = float(mag)
        except ValueError:
            vmag = None
        rec = dict(name=pretty or ascii_name, ascii=ascii_name, designation=designation,
                   ra=ra_deg / 15.0, dec=dec_deg, mag=vmag, con=con)
        if hd.isdigit() and int(hd) > 400000:
            hd = "_"                       # 999999 is the list's "no HD" placeholder
        if hd.isdigit():
            names_by_hd[hd] = rec
        if hip.isdigit():
            names_by_hip[hip] = rec
        if not hd.isdigit() and not hip.isdigit():
            orphan_names.append(rec)
        elif not hd.isdigit():
            orphan_names.append(rec)   # matched later by HIP against the WR table only; BSC has no HIP

    # ---- Bright Star Catalogue ---------------------------------------
    rows = []
    used_hd = set()
    for header, data in vizier_rows(bsc.read_text(encoding="utf-8", errors="replace")):
        if "HR" not in header:
            continue
        col = {h: i for i, h in enumerate(header)}
        for r in data:
            try:
                hr = r[col["HR"]]; hd = r[col["HD"]]; sao = r[col["SAO"]]; var = r[col["VarID"]]
                ra = hms_to_hours(r[col["RAJ2000"]]); dec = dms_to_deg(r[col["DEJ2000"]])
                vmag = float(r[col["Vmag"]]) if r[col["Vmag"]] else None
                sptype = r[col["SpType"]]
            except (KeyError, IndexError, ValueError):
                continue
            if not hr or ra is None or dec is None:
                continue
            parsed = parse_bsc_name(r[col["Name"]])
            aliases, display = [], None
            if parsed:
                flam, bayer, sup, con = parsed
                gen = GENITIVE.get(con)
                if bayer and bayer.lower() in GREEK:
                    word, letter = GREEK[bayer.lower()]
                    s = SUPERSCRIPT.get(sup, sup)
                    display = f"{word}{s} {con}"
                    aliases += [f"{letter}{s} {con}", f"{bayer}{sup} {con}", f"{word}{sup} {con}"]
                    aliases += [f"{alt}{sup} {con}" for alt in GREEK_ALT.get(bayer.lower(), [])]
                    if gen:
                        aliases += [f"{word}{sup} {gen}", f"{letter}{s} {gen}"]
                elif bayer:
                    display = f"{bayer}{sup} {con}"
                    aliases.append(display)
                if flam:
                    aliases.append(f"{flam} {con}")
                    if gen:
                        aliases.append(f"{flam} {gen}")
                    if not display:
                        display = f"{flam} {con}"
            v = re.sub(r"\s+", " ", var) if var else ""
            # A bare number is an NSV (suspected variable) index, not a GCVS
            # designation: keep it findable, do not call the star variable.
            if v.isdigit():
                aliases += [f"NSV {v}", f"NSV{v}"]
                var = ""
                v = ""
            if v:
                aliases.append(v)
                g = constellation_genitive_alias(v)
                if g:
                    aliases.append(g)
                if not display:
                    display = v
            aliases += [f"HR {hr}", f"HR{hr}"]
            if hd:
                aliases += [f"HD {hd}", f"HD{hd}"]
            if sao:
                aliases += [f"SAO {sao}", f"SAO{sao}"]
            if not display:
                display = f"HR {hr}"
            common = None
            iau_rec = names_by_hd.get(hd) if hd else None
            if iau_rec:
                common = iau_rec["name"]
                if iau_rec["ascii"] != iau_rec["name"]:
                    aliases.append(iau_rec["ascii"])
                used_hd.add(hd)
            nick = None
            for key, nn in NICKNAMES.items():
                if key.lower() in (a.lower() for a in aliases) or key.lower() == (display or "").lower():
                    nick = nn
            if nick and nick not in (common or "").split(","):
                common = f"{common},{nick}" if common else nick
            name = common.split(",")[0] if common else display
            if common and display and display.lower() != name.lower():
                aliases.insert(0, display)
            rows.append(("HR", hr, name, common, star_type(sptype, var), ra, dec, vmag, None,
                         parsed[3] if parsed else None, "|".join(dict.fromkeys(a for a in aliases if a))))

    # ---- IAU names with no Bright Star row (faint exoplanet hosts) ----
    for rec in orphan_names:
        if rec["designation"].startswith("HR "):
            continue
        aliases = [rec["designation"], rec["ascii"]]
        rows.append(("Star", rec["ascii"], rec["name"], None, "Star", rec["ra"], rec["dec"], rec["mag"], None,
                     rec["con"] if rec["con"] in GENITIVE else None, "|".join(dict.fromkeys(a for a in aliases if a))))

    # ---- Wolf-Rayet stars ---------------------------------------------
    wr_pos, wr_phot = {}, {}
    for header, data in vizier_rows(wr.read_text(encoding="utf-8", errors="replace")):
        col = {h: i for i, h in enumerate(header)}
        if "RAJ2000" in col and "GCVS" in col:
            for r in data:
                wr_pos[r[col["WR"]]] = r
                wr_pos[r[col["WR"]]] = dict(name=r[col["Name"]], gcvs=r[col["GCVS"]], hd=r[col["OName"]],
                                            hip=r[col["Aname"]], ra=r[col["RAJ2000"]], dec=r[col["DEJ2000"]])
        elif "vmag" in col and "SpType" in col and "vinf" in col:
            for r in data:
                wr_phot[r[col["WR"]]] = dict(sp=r[col["SpType"]], vmag=r[col["vmag"]])
    for wrn, p in wr_pos.items():
        ra = hms_to_hours(p["ra"]); dec = dms_to_deg(p["dec"])
        if ra is None or dec is None:
            continue
        key = wrn.replace(" ", "")
        name = f"WR {key}"
        ph = wr_phot.get(wrn, {})
        try:
            vmag = float(ph.get("vmag") or "")
        except ValueError:
            vmag = None
        aliases = [f"WR{key}"]
        common = None
        gcvs = re.sub(r"\s+", " ", p["gcvs"]).strip()
        if gcvs:
            common = gcvs
            aliases.append(gcvs)
            g = constellation_genitive_alias(gcvs)
            if g:
                aliases.append(g)
        if p["name"]:
            aliases.append(p["name"].strip())
        if p["hd"]:
            hd = p["hd"].strip()
            aliases += [hd, hd.replace(" ", "")]
            hdnum = hd.replace("HD", "").strip()
            iau_rec = names_by_hd.get(hdnum)
            if iau_rec:
                common = f"{iau_rec['name']},{common}" if common else iau_rec["name"]
        if p["hip"]:
            aliases += [p["hip"].strip(), p["hip"].replace(" ", "").strip()]
        if ph.get("sp"):
            aliases.append(ph["sp"].strip())
        nick = NICKNAMES.get(name)
        if nick and nick not in (common or "").split(","):
            common = f"{common},{nick}" if common else nick
        rows.append(("WR", key, name, common, "Wolf-Rayet Star", ra, dec, vmag, None,
                     None, "|".join(dict.fromkeys(a for a in aliases if a))))

    # ---- write ----------------------------------------------------------
    conn = sqlite3.connect(str(db_path))
    cur = conn.cursor()
    old = cur.execute("SELECT id FROM objects WHERE catalog IN ('HR','Star','WR')").fetchall()
    for (oid,) in old:
        cur.execute("DELETE FROM objects_idx WHERE id = ?", (oid,))
    cur.execute("DELETE FROM objects WHERE catalog IN ('HR','Star','WR')")
    for cat, cid, name, common, typ, ra, dec, mag, size, con, aliases in rows:
        cur.execute("""INSERT INTO objects (catalog, catalog_id, name, common_name, type, ra_hours, dec_deg,
                       magnitude, size_arcmin, constellation, aliases) VALUES (?,?,?,?,?,?,?,?,?,?,?)""",
                    (cat, cid, name, common, typ, ra, dec, mag, size, con, aliases))
        oid = cur.lastrowid
        cur.execute("INSERT INTO objects_idx (id, min_ra, max_ra, min_dec, max_dec) VALUES (?,?,?,?,?)",
                    (oid, ra, ra, dec, dec))
    conn.commit()
    counts = cur.execute("SELECT catalog, COUNT(*) FROM objects WHERE catalog IN ('HR','Star','WR') GROUP BY catalog").fetchall()
    cur.execute("ANALYZE")
    conn.commit()
    conn.execute("VACUUM")
    conn.close()
    log(f"stars written: {dict(counts)} (removed {len(old)} previous star rows)")
    log(f"IAU names attached to BSC rows: {len(used_hd)}; orphan IAU names: {len([r for r in orphan_names if not r['designation'].startswith('HR ')])}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--db", default=str(DEFAULT_DB))
    ap.add_argument("--skip-download", action="store_true")
    a = ap.parse_args()
    db = Path(a.db)
    if not db.exists():
        sys.exit(f"{db} not found; run build-dso-catalog.py first")
    build(db, a.skip_download)


if __name__ == "__main__":
    main()
