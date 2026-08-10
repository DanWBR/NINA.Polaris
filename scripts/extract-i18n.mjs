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
// `opts.prose` relaxes the two characters that are code-ish in markup but
// perfectly ordinary in a sentence: `"` (quoted phrases, e.g. use "Connect
// all") and `=` (e.g. Green = connected). Only pass it for a source where the
// remaining rules already reject the code: see the tour.js branch.
// `opts.maxLen` raises the length cap for sources that are known to be prose,
// like the HELP tutorial bodies, whose paragraphs run past the default 320.
// A {var} placeholder is the documented interpolation for t() / $t(), so it is
// part of a real key rather than code. It has to be taken out before the
// code-ish test, which rejects every brace: with it in, EVERY interpolated key
// was invisible to extraction and lived in the catalog as a permanent orphan,
// translated only where somebody had added it by hand.
const PLACEHOLDER = /\{[A-Za-z_][A-Za-z0-9_]*\}/g;

function keep(s, opts) {
    s = norm(s);
    const maxLen = (opts && opts.maxLen) || 320;
    if (!s || s.length < 2 || s.length > maxLen) return false;
    if (!/[A-Za-z]/.test(s)) return false;                       // needs a letter
    if (/^[^A-Za-z(À-ÿ]/.test(s)) return false;                  // must START with a letter or "("
    if (/^[a-z]/.test(s) && !/\s/.test(s)) return false;         // lone lowercase token (identifier/var)
    // Judge the sentence, not its placeholders. Anything still holding a brace
    // after they are removed is a real expression fragment and still goes.
    const prose = s.replace(PLACEHOLDER, '');
    // Code-ish / Alpine-binding / expression fragments:
    const codeish = opts && opts.prose
        ? /[_<>{}`$@#]|=>|::|\|\||&&|\(\)|\/\/|\bx-[a-z]|@click|\bfunction\b/
        : /[_<>{}=`$@#"]|=>|::|\|\||&&|\(\)|\/\/|\bx-[a-z]|@click|\bfunction\b/;
    if (codeish.test(prose)) return false;
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
// Alpine bindings that pick a literal at runtime:
//     x-text="nightMode ? 'Day' : 'Night'"
//     :title="collapsed ? 'Show the top bar' : ''"
// The observer translates whichever branch is written into the DOM, so those
// literals are real keys. Scanned separately because the two rules above only
// see STATIC text and STATIC attributes, which is why "Day", "Night", "Unlock
// UI" and friends kept showing up as orphans while being visibly translated.
//
// A binding often CONCATENATES a literal with state ("'(rated ' + amps + ')'").
// Those halves never appear alone in the DOM, so they can never match: drop the
// ones that give themselves away with unbalanced parentheses or an embedded
// newline (multi-line titles are always built by concatenation).
const balanced = (s) => {
    let d = 0;
    for (const c of s) { if (c === '(') d++; else if (c === ')' && --d < 0) return false; }
    return d === 0;
};
for (const m of html.matchAll(/(?:x-text|:title|:aria-label|:placeholder|:alt)\s*=\s*"([^"]*)"/g)) {
    for (const lit of m[1].matchAll(/'((?:\\.|[^'\\])*)'/g)) {
        if (/\\n/.test(lit[1])) continue;
        const raw = lit[1].replace(/\\(['"`\\])/g, '$1');
        if (!balanced(raw)) continue;
        if (keep(raw, { prose: true })) found.add(norm(raw));
    }
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
//
// A bare literal regex over the raw file is NOT enough here: an apostrophe in a
// // comment ("Cards that don't exist on this platform") opens a bogus string
// and desynchronises the quote pairing for every literal after it. So walk the
// file with a small scanner that knows comments and regex literals, the way the
// index.html branch strips <!-- --> before scanning.
function jsStringLiterals(src) {
    const out = [];
    let prev = '';                       // last significant char: regex vs. divide
    for (let i = 0; i < src.length; i++) {
        const c = src[i];
        if (c === '/' && src[i + 1] === '/') {            // line comment
            while (i < src.length && src[i] !== '\n') i++;
            continue;
        }
        if (c === '/' && src[i + 1] === '*') {            // block comment
            const end = src.indexOf('*/', i + 2);
            if (end < 0) break;
            i = end + 1;
            continue;
        }
        if (c === '/' && /[(,=:[!&|?{};+\-*%^~]/.test(prev)) {   // regex literal
            for (i++; i < src.length && src[i] !== '/' && src[i] !== '\n'; i++) {
                if (src[i] === '\\') { i++; continue; }
                if (src[i] === '[') for (i++; i < src.length && src[i] !== ']'; i++) if (src[i] === '\\') i++;
            }
            prev = '/';
            continue;
        }
        if (c === '"' || c === "'" || c === '`') {        // string / template
            let buf = '';
            for (i++; i < src.length && src[i] !== c; i++) {
                if (src[i] === '\\') { buf += src[i] + (src[i + 1] || ''); i++; continue; }
                buf += src[i];
            }
            out.push(buf);
            prev = c;
            continue;
        }
        if (!/\s/.test(c)) prev = c;
    }
    return out;
}

// ---- app.js: the HELP tutorials ------------------------------------------
// Step copy lives in plain object literals (title:/body:/tip:/warn:) the same
// way the tour's does, so the t()/toast() scan above never saw it and the whole
// in-app tutorial set stayed English in every language. Only this function's
// body is scanned, not all of app.js: a blanket literal scrape over 36k lines
// of selectors, endpoints and state paths would bury the catalog in junk.
{
    const start = js.indexOf('_helpTutorials() {');
    if (start >= 0) {
        // Walk braces from the function body to find its end.
        let depth = 0, end = -1;
        for (let i = js.indexOf('{', start); i < js.length; i++) {
            if (js[i] === '{') depth++;
            else if (js[i] === '}' && --depth === 0) { end = i; break; }
        }
        if (end > start) {
            for (const lit of jsStringLiterals(js.slice(start, end))) {
                const raw = lit.replace(/\\(['"`\\])/g, '$1');
                if (keep(raw, { prose: true, maxLen: 600 })) found.add(norm(raw));
            }
        }
    }
}

if (existsSync(TOURJS)) {
    for (const lit of jsStringLiterals(readFileSync(TOURJS, 'utf8'))) {
        const raw = lit.replace(/\\(['"`\\])/g, '$1');
        // Not copy: DOM/keyboard identifiers passed to querySelector, key
        // comparisons and addEventListener, which survive the rules above.
        if (/^(Escape|Enter|Arrow(Up|Down|Left|Right))$/.test(raw.trim())) continue;
        if (keep(raw, { prose: true })) found.add(norm(raw));
    }
}

// ---- merge ----------------------------------------------------------------
let existing = {};
if (existsSync(OUT)) {
    try { existing = JSON.parse(readFileSync(OUT, 'utf8')); } catch { existing = {}; }
}
// Orphans: in the old file but no longer extracted. They are KEPT and merely
// reported. This scraper's recall is deliberately imperfect (a string built in
// JS and rendered through Alpine is translated at runtime but may be invisible
// here), so a missing hit is not proof the string is gone. Dropping a key
// silently throws away four translations; keeping a dead one costs a line of
// JSON. Prune by hand once you have confirmed the string really left the app.
const orphans = Object.keys(existing).filter((k) => !found.has(k));

const merged = {};
const keys = [...found, ...orphans].sort((a, b) => a.localeCompare(b));
let added = 0;
for (const k of keys) {
    merged[k] = Object.prototype.hasOwnProperty.call(existing, k) ? existing[k] : '';
    if (!(k in existing)) added++;
}

mkdirSync(dirname(OUT), { recursive: true });
writeFileSync(OUT, JSON.stringify(merged, null, 2) + '\n', 'utf8');

console.log(`extract-i18n: ${keys.length} source strings -> ${OUT}`);
console.log(`  new this run: ${added}`);
if (orphans.length) {
    console.log(`  orphans (in _source.json, not in source — review): ${orphans.length}`);
    for (const o of orphans.slice(0, 20)) console.log(`    - ${JSON.stringify(o)}`);
    if (orphans.length > 20) console.log(`    ... and ${orphans.length - 20} more`);
}
