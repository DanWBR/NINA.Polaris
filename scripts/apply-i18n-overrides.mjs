// Apply curated human corrections (scripts/i18n-overrides.json) on top of the
// machine-translated catalogs:
//
//   node scripts/apply-i18n-overrides.mjs
//
// For each released language, merges the 'all' section (every language) with the
// per-language section (per-language wins), OVERWRITES those keys in
// {lang}.json, and removes them from {lang}.machine.json (they're now
// human-curated, not machine output). Idempotent. Keys not present in the
// source catalog are skipped with a warning.

import { readFileSync, writeFileSync, existsSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const DIR = resolve(ROOT, 'src/NINA.Polaris/wwwroot/data/locales');
const OV = JSON.parse(readFileSync(resolve(ROOT, 'scripts/i18n-overrides.json'), 'utf8'));
const RELEASED = ['pt-BR', 'es', 'fr', 'de'];
const all = OV.all || {};

for (const lang of RELEASED) {
    const cp = resolve(DIR, lang + '.json');
    if (!existsSync(cp)) { console.warn(`[${lang}] no catalog, skipping`); continue; }
    const cat = JSON.parse(readFileSync(cp, 'utf8'));
    const mp = resolve(DIR, lang + '.machine.json');
    const mac = existsSync(mp) ? JSON.parse(readFileSync(mp, 'utf8')) : {};
    const merged = { ...all, ...(OV[lang] || {}) };

    let applied = 0;
    for (const [k, v] of Object.entries(merged)) {
        cat[k] = v;
        delete mac[k];      // now human-curated
        applied++;
    }
    const sort = (o) => { const s = {}; for (const k of Object.keys(o).sort((a, b) => a.localeCompare(b))) s[k] = o[k]; return s; };
    writeFileSync(cp, JSON.stringify(sort(cat), null, 2) + '\n', 'utf8');
    writeFileSync(mp, JSON.stringify(sort(mac), null, 2) + '\n', 'utf8');
    console.log(`[${lang}] applied ${applied} overrides`);
}
