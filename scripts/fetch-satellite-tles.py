#!/usr/bin/env python3
"""Refresh the bundled satellite snapshot the SKY map ships with.

Writes src/NINA.Polaris/wwwroot/sky/data/skydata/tle_satellite.jsonl.gz from
CelesTrak's "visual" group plus the ISS and Tiangong from "stations", in the
record shape the sky engine's satellites module parses. The running host
regenerates the same file itself (SatelliteTleService); this script only
refreshes the copy that goes into a release, so run it before cutting one.

    python3 scripts/fetch-satellite-tles.py
"""
import gzip
import json
import os
import sys
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
OUT = os.path.join(ROOT, 'src', 'NINA.Polaris', 'wwwroot', 'sky', 'data', 'skydata', 'tle_satellite.jsonl.gz')
STDMAG = os.path.join(ROOT, 'src', 'NINA.Polaris', 'wwwroot', 'data', 'satellite-stdmag.json')

VISUAL = 'https://celestrak.org/NORAD/elements/gp.php?GROUP=visual&FORMAT=tle'
STATIONS = 'https://celestrak.org/NORAD/elements/gp.php?GROUP=stations&FORMAT=tle'
ISS, TIANGONG = 25544, 48274
ALIASES = {
    ISS: ['ISS', 'International Space Station'],
    TIANGONG: ['Tiangong', 'CSS', 'Chinese Space Station'],
    20580: ['Hubble', 'HST', 'Hubble Space Telescope'],
}


def fetch(url):
    req = urllib.request.Request(url, headers={'User-Agent': 'NINA.Polaris/1.0 (+https://github.com/DanWBR/NINA.Polaris)'})
    with urllib.request.urlopen(req, timeout=30) as r:
        return r.read().decode('utf-8')


def parse(text):
    lines = text.replace('\r', '').split('\n')
    out, name, i = [], None, 0
    while i < len(lines):
        l = lines[i].rstrip()
        if l.startswith('1 ') and i + 1 < len(lines) and lines[i + 1].startswith('2 '):
            l2 = lines[i + 1].rstrip()
            try:
                norad = int(l[2:7])
            except ValueError:
                name, i = None, i + 2
                continue
            out.append((name or '%05d' % norad, norad, l, l2))
            name, i = None, i + 2
            continue
        if l and not l.startswith('2 '):
            name = l.strip()
        i += 1
    return out


def select(visual, stations):
    by = {}
    for s in stations:
        if s[1] in (ISS, TIANGONG):
            by[s[1]] = s
    for s in visual:
        by.setdefault(s[1], s)
    return [by[k] for k in sorted(by)]


def record(sat, stdmag):
    name, norad, l1, l2 = sat
    names, short = [], name
    if norad in ALIASES:
        short = ALIASES[norad][0]
        names += ['NAME ' + a for a in ALIASES[norad]]
    p = name.find(' (')
    if p > 0:
        bare = name[:p].strip()
        if 'NAME ' + bare not in names:
            names.append('NAME ' + bare)
        if short == name:
            short = bare
    if 'NAME ' + name not in names:
        names.append('NAME ' + name)
    names.append('NORAD %05d' % norad)
    model = {'norad_number': norad, 'designation': l1[9:17].strip(), 'tle': [l1, l2]}
    if str(norad) in stdmag:
        model['mag'] = stdmag[str(norad)]
    return {'types': ['Asa'], 'model': 'tle_satellite', 'model_data': model,
            'names': names, 'short_name': short}


def main():
    stdmag = json.load(open(STDMAG)) if os.path.exists(STDMAG) else {}
    sats = select(parse(fetch(VISUAL)), parse(fetch(STATIONS)))
    if not any(s[1] == ISS for s in sats):
        sys.exit('no ISS in the download; not writing')
    with gzip.open(OUT, 'wt', encoding='utf-8') as f:
        for s in sats:
            f.write(json.dumps(record(s, stdmag), separators=(',', ':')) + '\n')
    print('%d satellites -> %s' % (len(sats), os.path.relpath(OUT, ROOT)))


if __name__ == '__main__':
    main()
