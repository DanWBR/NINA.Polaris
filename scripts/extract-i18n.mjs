// Extract translatable English source strings for the Polaris web UI i18n
// catalog (see wwwroot/js/i18n.js — "English source as key" model).
//
//   node scripts/extract-i18n.mjs
//
// Scans index.html for static element text + title/placeholder/aria-label/alt
// attributes, and app.js for t('...') / $t('...') / toast('...') string
// literals, then MERGES the findings into wwwroot/data/locales/_source.json as
// a sorted { "English source": "" } map (existing entries preserved). It also
// reports a count and likely orphans (entries in _source.json no longer found
// in the source — candidates that an English edit left behind).
//
// This is a pragmatic tokenizer, not a full HTML/JS parser: it favours
// recall (find candidate strings for translators) over precision. Curate the
// per-language catalogs from _source.json; never hand-edit translations here.

import { readFileSync, writeFileSync, existsSync, mkdirSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const HTML = resolve(ROOT, 'src/NINA.Polaris/wwwroot/index.html');
const APPJS = resolve(ROOT, 'src/NINA.Polaris/wwwroot/js/app.js');
// Interactive tour: step copy lives in plain object literals (title:/body:) and
// injected button/offer text, not t()/toast() calls, so it's scanned as generic
// string literals below (keep() drops the selectors/class-name fragments).
const TOURJS = resolve(ROOT, 'src/NINA.Polaris/wwwroot/js/tour.js');
const OUT = resolve(ROOT, 'src/NINA.Polaris/wwwroot/data/locales/_source.json');

// A sentinel that never occurs in UI text: we replace HTML tags with it and
// split on it, so a single text node wrapped across source lines stays ONE key
// (splitting on a newline or space would shatter it into partial keys that
// never match the runtime DOM text the observer reads).
const SENTINEL = String.fromCharCode(1);

// Decode the HTML entities that appear in static markup so the extracted key
// matches the DECODED text the runtime DOM observer sees (e.g. "Equipment &amp;
// capture" in the source is "Equipment & capture" at runtime). Without this,
// any string containing & < > " ' or a numeric/emoji entity never matches.
const NAMED = { amp: '&', lt: '<', gt: '>', quot: '"', apos: "'", nbsp: ' ', '#39': "'" };
function decodeEntities(s) {
    return s.replace(/&(#x?[0-9a-fA-F]+|[a-zA-Z]+);/g, (m, body) => {
        if (body[0] === '#') {
            const cp = body[1] === 'x' || body[1] === 'X'
                ? parseInt(body.slice(2), 16) : parseInt(body.slice(1), 10);
            return Number.isFinite(cp) ? String.fromCodePoint(cp) : m;
        }
        return Object.prototype.hasOwnProperty.call(NAMED, body) ? NAMED[body] : m;
    });
}

const norm = (s) => decodeEntities(s).replace(/\s+/g, ' ').trim();

// High-precision filter: keep things that look like natural-language UI prose,
// drop the code/CSS/Alpine-expression fragments that leak from a hand-written
// HTML+JS scrape. Recall is sacrificed for precision so the catalog (and the
// translation/Crowdin effort) isn't polluted with junk keys. Anything missed
// here just stays English at runtime (graceful), so erring strict is safe.
function keep(s) {
    s = norm(s);
    if (!s || s.length < 2 || s.length > 320) return false;
    if (!/[A-Za-z]/.test(s)) return false;                       // needs a letter
    if (/^[^A-Za-z(À-ÿ]/.test(s)) return false;                  // must START with a letter or "("
    if (/^[a-z]/.test(s) && !/\s/.test(s)) return false;         // lone lowercase token (identifier/var)
    // Code-ish / Alpine-binding / expression fragments:
    if (/[_<>{}=`$@#"]|=>|::|\|\||&&|\(\)|\/\/|\bx-[a-z]|@click|\bfunction\b/.test(s)) return false;
    if (/\b(null|undefined|true|false|return)\b/.test(s) && !/\s\w+\s\w+/.test(s)) return false;
    if (/^[-A-Za-z0-9]+\.[A-Za-z]/.test(s) && !/\s/.test(s)) return false; // dotted identifier
    if (/^[a-z]+([A-Z][a-z]+)+$/.test(s)) return false;          // camelCase identifier
    if (/\b(ps|video|editorState|guider|update|host|auth)\.[a-zA-Z]/.test(s)) return false; // alpine state paths
    return true;
}

const found = new Set();

// ---- index.html -----------------------------------------------------------
let html = readFileSync(HTML, 'utf8');
// Drop script/style blocks and comments.
html = html.replace(/<script[\s\S]*?<\/script>/gi, ' ')
           .replace(/<style[\s\S]*?<\/style>/gi, ' ')
           .replace(/<!--[\s\S]*?-->/g, ' ');

// Static attributes (plain, not Alpine :bindings).
for (const m of html.matchAll(/(?<![:\w-])(title|placeholder|aria-label|alt)\s*=\s*"([^"]*)"/g)) {
    if (keep(m[2])) found.add(norm(m[2]));
}
// Static element text: replace tags with the sentinel, split on it (one text
// node -> one fragment), then norm() collapses internal whitespace.
const text = html.replace(/<[^>]+>/g, SENTINEL);
for (const frag of text.split(SENTINEL)) {
    if (keep(frag)) found.add(norm(frag));
}

// ---- app.js ---------------------------------------------------------------
const js = readFileSync(APPJS, 'utf8');
// t('...') / $t('...') / this.$t('...') / toast('...') first string arg.
const callRe = /(?:\$?t|toast)\(\s*(['"`])((?:\\.|(?!\1)[\s\S])*?)\1/g;
for (const m of js.matchAll(callRe)) {
    const raw = m[2].replace(/\\(['"`\\])/g, '$1');
    if (keep(raw)) found.add(norm(raw));
}

// ---- tour.js --------------------------------------------------------------
// Every quoted string literal; keep() filters out CSS selectors, class names
// and other code fragments, leaving the user-facing step/offer/button copy.
if (existsSync(TOURJS)) {
    const tourjs = readFileSync(TOURJS, 'utf8');
    const strRe = /(['"`])((?:\\.|(?!\1)[\s\S])*?)\1/g;
    for (const m of tourjs.matchAll(strRe)) {
        const raw = m[2].replace(/\\(['"`\\])/g, '$1');
        if (keep(raw)) found.add(norm(raw));
    }
}

// ---- merge ----------------------------------------------------------------
let existing = {};
if (existsSync(OUT)) {
    try { existing = JSON.parse(readFileSync(OUT, 'utf8')); } catch { existing = {}; }
}
const merged = {};
const keys = [...found].sort((a, b) => a.localeCompare(b));
let added = 0;
for (const k of keys) {
    merged[k] = Object.prototype.hasOwnProperty.call(existing, k) ? existing[k] : '';
    if (!(k in existing)) added++;
}
// Orphans: in the old file but no longer extracted.
const orphans = Object.keys(existing).filter((k) => !found.has(k));

mkdirSync(dirname(OUT), { recursive: true });
writeFileSync(OUT, JSON.stringify(merged, null, 2) + '\n', 'utf8');

console.log(`extract-i18n: ${keys.length} source strings -> ${OUT}`);
console.log(`  new this run: ${added}`);
if (orphans.length) {
    console.log(`  orphans (in _source.json, not in source — review): ${orphans.length}`);
    for (const o of orphans.slice(0, 20)) console.log(`    - ${JSON.stringify(o)}`);
    if (orphans.length > 20) console.log(`    ... and ${orphans.length - 20} more`);
}
