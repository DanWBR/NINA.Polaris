// Bulk machine pre-translation of the Polaris UI catalog.
//
//   # DeepL (recommended):
//   DEEPL_API_KEY=xxxx node scripts/pretranslate-i18n.mjs            # all released langs
//   DEEPL_API_KEY=xxxx node scripts/pretranslate-i18n.mjs pt-BR es   # specific langs
//
//   # LibreTranslate (self-host / free):
//   LIBRETRANSLATE_URL=http://localhost:5000 node scripts/pretranslate-i18n.mjs
//
// Reads wwwroot/data/locales/_source.json (run extract-i18n.mjs first), and for
// each target language fills the MISSING keys in {lang}.json via the configured
// MT provider. Existing (human/curated) translations are NEVER overwritten.
// The astro glossary (scripts/i18n-glossary.json) is applied:
//   - 'doNotTranslate' terms that EXACTLY match a source string are copied as-is
//   - 'forced' terms pin a preferred translation
// Machine-filled keys are recorded in {lang}.machine.json so a reviewer (or the
// CI coverage guard) can tell human-reviewed strings from raw MT output.
//
// NOTE: this requires network access + a provider key/URL. It deliberately does
// NOT invent translations when no provider is configured — it exits with
// instructions instead, so the catalog never gets fake data.

import { readFileSync, writeFileSync, existsSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const DIR = resolve(ROOT, 'src/NINA.Polaris/wwwroot/data/locales');
const SRC = resolve(DIR, '_source.json');
const GLOSS = resolve(ROOT, 'scripts/i18n-glossary.json');

const RELEASED = ['pt-BR', 'es', 'fr', 'de'];
// Provider locale codes (DeepL uses EN->target; pt-BR => PT-BR).
const DEEPL_CODE = { 'pt-BR': 'PT-BR', es: 'ES', fr: 'FR', de: 'DE' };
const LT_CODE = { 'pt-BR': 'pt', es: 'es', fr: 'fr', de: 'de' };

const args = process.argv.slice(2).filter((a) => RELEASED.includes(a));
const langs = args.length ? args : RELEASED;

const deeplKey = process.env.DEEPL_API_KEY;
const deeplUrl = process.env.DEEPL_API_URL
    || (deeplKey && deeplKey.endsWith(':fx') ? 'https://api-free.deepl.com' : 'https://api.deepl.com');
const ltUrl = process.env.LIBRETRANSLATE_URL;

if (!deeplKey && !ltUrl) {
    console.error(`No MT provider configured. Set one of:
  DEEPL_API_KEY=...        (optionally DEEPL_API_URL)
  LIBRETRANSLATE_URL=http://host:port
Then re-run. No catalog was modified.`);
    process.exit(2);
}

const source = JSON.parse(readFileSync(SRC, 'utf8'));
const gloss = JSON.parse(readFileSync(GLOSS, 'utf8'));
const dnt = new Set(gloss.doNotTranslate || []);

async function translateDeepL(texts, lang) {
    const params = new URLSearchParams();
    params.append('auth_key', deeplKey);
    params.append('source_lang', 'EN');
    params.append('target_lang', DEEPL_CODE[lang]);
    for (const t of texts) params.append('text', t);
    const r = await fetch(deeplUrl + '/v2/translate', { method: 'POST', body: params });
    if (!r.ok) throw new Error('DeepL HTTP ' + r.status + ' ' + (await r.text()).slice(0, 200));
    return (await r.json()).translations.map((x) => x.text);
}
async function translateLT(texts, lang) {
    const out = [];
    for (const t of texts) {       // LibreTranslate is one-at-a-time on most hosts
        const r = await fetch(ltUrl.replace(/\/$/, '') + '/translate', {
            method: 'POST', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ q: t, source: 'en', target: LT_CODE[lang], format: 'text' })
        });
        if (!r.ok) throw new Error('LibreTranslate HTTP ' + r.status);
        out.push((await r.json()).translatedText);
    }
    return out;
}
const translate = deeplKey ? translateDeepL : translateLT;

function chunk(a, n) { const o = []; for (let i = 0; i < a.length; i += n) o.push(a.slice(i, i + n)); return o; }

for (const lang of langs) {
    const catPath = resolve(DIR, lang + '.json');
    const cat = existsSync(catPath) ? JSON.parse(readFileSync(catPath, 'utf8')) : {};
    const forced = (gloss.forced && gloss.forced[lang]) || {};
    const machinePath = resolve(DIR, lang + '.machine.json');
    const machine = existsSync(machinePath) ? JSON.parse(readFileSync(machinePath, 'utf8')) : {};

    // Keys still needing a translation (skip ones already in the catalog).
    const todo = Object.keys(source).filter((k) => !(k in cat) || cat[k] === '');
    // Apply glossary first (free, deterministic).
    const mtNeeded = [];
    for (const k of todo) {
        if (forced[k]) { cat[k] = forced[k]; continue; }
        if (dnt.has(k.trim())) { cat[k] = k; continue; }
        mtNeeded.push(k);
    }

    let done = 0;
    for (const batch of chunk(mtNeeded, deeplKey ? 50 : 1)) {
        let res;
        try { res = await translate(batch, lang); }
        catch (e) { console.error(`[${lang}] MT failed: ${e.message}. Wrote ${done} so far.`); break; }
        batch.forEach((k, i) => { cat[k] = res[i]; machine[k] = true; });
        done += batch.length;
        process.stdout.write(`\r[${lang}] machine-translated ${done}/${mtNeeded.length}`);
    }
    process.stdout.write('\n');

    // Sort keys for stable diffs.
    const sorted = {}; for (const k of Object.keys(cat).sort((a, b) => a.localeCompare(b))) sorted[k] = cat[k];
    writeFileSync(catPath, JSON.stringify(sorted, null, 2) + '\n', 'utf8');
    writeFileSync(machinePath, JSON.stringify(machine, null, 2) + '\n', 'utf8');
    console.log(`[${lang}] catalog: ${Object.keys(cat).length} entries (${Object.keys(machine).length} machine, need review).`);
}
