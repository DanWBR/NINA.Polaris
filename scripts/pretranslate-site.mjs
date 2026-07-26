// Machine-translate the Astro marketing site's content JSON with DeepL.
//
// Mirrors scripts/pretranslate-i18n.mjs, but targets website/content/ instead of
// the app's data/locales/. Writes a sibling carrying the language tag
// (content/pages/home.pt-BR.json); site.ts merges it over English PER KEY, so a
// partial file is safe.
//
//   $env:DEEPL_API_KEY = "..."; node scripts/pretranslate-site.mjs pt-BR
//   node scripts/pretranslate-site.mjs pt-BR home install    # only these files
//   node scripts/pretranslate-site.mjs --dry-run pt-BR       # count characters
//
// Jargon is held in English by a DeepL glossary of identity entries, created on
// first use. A Free key may hold only ONE glossary, so all four language pairs
// share a single multilingual one.
//
// Output is overwritten wholesale, so hand corrections to a generated file are
// lost on the next run for that language.
//
// NEVER commit the key: this repository is public.

import fs from 'node:fs';
import path from 'node:path';

const ROOT = path.resolve(import.meta.dirname, '..', 'website', 'content');
const KEY = process.env.DEEPL_API_KEY;
// A ":fx" suffix marks a Free key, which lives on a different host.
const API = KEY?.endsWith(':fx')
  ? 'https://api-free.deepl.com/v2/translate'
  : 'https://api.deepl.com/v2/translate';

// DeepL target codes differ from our locale tags in places.
const DEEPL_TARGET = { 'pt-BR': 'PT-BR', es: 'ES', fr: 'FR', de: 'DE' };

const FILES = {
  home: 'pages/home.json',
  install: 'install/install.json',
  guide: 'guide/getting-started.json',
  features: 'features/features.json',
  about: 'about/about.json',
  'ai-tools': 'ai-tools/ai-tools.json',
  assistant: 'assistant/assistant.json',
  // Chrome rather than page content: nav, footer, the strings baked into
  // components, and the two prose fields of `site`.
  nav: '_ui/navLinks.json',
  footer: '_ui/footerCols.json',
  strings: '_ui/strings.json',
  meta: '_ui/site.json',
};

// Keys whose values are never prose. Translating a hex colour or an href would
// corrupt the page, and translating an emoji wastes quota.
const SKIP_KEYS = new Set([
  'icon', 'color', 'url', 'href', 'image', 'src', 'alt', 'source', 'id',
  'score', 'stacking', 'capture', 'memory', 'price', 'cores', 'device',
  'version', 'external', 'highlight', 'anchor', 'slug', 'highlightProduct',
]);

// Arrays whose string elements are data, not copy. `products` is the column
// header row of the comparison table: competitor brand names.
const SKIP_ARRAYS = new Set(['products']);

// The comparison table encodes a cell as "yes" / "partial" / "no", optionally
// with a ":qualifier" suffix (see parseCell in Comparison.astro). Translating
// the token turns the tick into plain text, so it is not copy either.
const MARK_CELL = /^(yes|no|partial)(:|$)/;

// Terms the astrophotography audience expects in English. Wrapping them tells
// DeepL to leave them alone; the same rule the app's catalogs follow.
const KEEP_ENGLISH = [
  'plate solve', 'plate solving', 'plate-solve', 'plate-solving', 'plate solver',
  'dithering', 'dither', 'live stacking', 'live stack', 'live-stacking',
  'guiding', 'autoguiding', 'guider', 'flats', 'darks', 'bias', 'subs',
  'meridian flip', 'polar alignment', 'autofocus', 'auto-focus', 'sequencer',
  'hotspot', 'live view', 'framing', 'stacking',
  'Polaris Astro Controller', 'Polaris', 'GraXpert', 'StarNet', 'INDI',
  'ASCOM', 'Alpaca', 'PHD2', 'ASTAP', 'Raspberry Pi', 'Orange Pi', 'Radxa',
  'Canopus', 'Siril',
];

function isProse(value) {
  if (typeof value !== 'string') return false;
  const s = value.trim();
  if (!s) return false;
  if (/^(https?:|mailto:|\/|#|\$)/.test(s)) return false;   // link or token
  if (!/[A-Za-z]{3}/.test(s)) return false;                  // emoji, numbers
  if (MARK_CELL.test(s)) return false;                       // table tick
  return true;
}

// tag_handling: 'xml' makes DeepL parse the payload as XML, which is what keeps
// the inline markup in the copy intact. The copy mixes four things:
//   real markup      <strong>, <code>   -> leave as tags, DeepL preserves them
//   entities         &lt;board-ip&gt;   -> already valid XML, leave alone
//   pseudo-tags      <hostname>         -> data, not markup, so escape it
//   bare ampersands  "Slew & Center"    -> a parse error, so escape it
// DeepL round-trips entities unchanged, so the only thing to undo afterwards is
// the ampersand we introduced.
const REAL_TAG =
  /^<\/?(?:strong|em|b|i|code|br|a|span|small|sup|sub|p|ul|ol|li)\b[^>]*>$/i;

function escapeNonMarkup(text) {
  return text
    // Only the five XML-predefined entities and numeric refs survive as-is.
    // &nbsp; is an HTML entity and XML rejects it as undefined, so it has to
    // travel escaped and come back whole.
    .replace(/&(?!(?:amp|lt|gt|quot|apos|#\d{1,5}|#x[0-9a-fA-F]{1,5});)/g, '&amp;')
    .replace(/<[^<>]*>/g, (m) =>
      REAL_TAG.test(m) ? m : m.replace(/</g, '&lt;').replace(/>/g, '&gt;'));
}

const unprotect = (text) => text.replace(/&amp;/g, '&');

/**
 * Put the angle brackets back the way the source stored them.
 *
 * The copy is inconsistent on purpose: `<hostname>` is stored raw and rendered
 * through Astro's escaping, while `&lt;board-ip&gt;` sits inside a set:html
 * block and must stay an entity. Escaping everything for the XML round trip
 * turns the first kind into a literal "&lt;hostname&gt;" on the page, so the
 * source string decides which form comes back.
 */
function restoreBrackets(src, out) {
  if (/&lt;|&gt;/.test(src)) return out;
  return out.replace(/&lt;/g, '<').replace(/&gt;/g, '>');
}

// ---------------------------------------------------------------------------
// Keeping the jargon in English
//
// The obvious approach, wrapping each term in an ignored <x> tag, works but
// costs a word: DeepL treats the span as one opaque token and swallows the
// space or the article in front of it ("relacionados aPolaris", "dGraXpert").
// A glossary of identity entries (GraXpert -> GraXpert) says the same thing
// without the tag, so the sentence around the term is still built properly.
// ---------------------------------------------------------------------------

/** Glossary target codes are coarser than translation ones: PT-BR -> pt. */
const GLOSSARY_TARGET = { 'PT-BR': 'pt', ES: 'es', FR: 'fr', DE: 'de' };
const GLOSSARY_NAME = 'polaris-site';
// A Free key may hold exactly ONE glossary, so all four language pairs have to
// share it. That is what the v3 endpoint's multilingual form is for: one
// glossary carrying a dictionary per pair.
const GLOSSARY_V3 = API.replace(/\/v2\/translate$/, '/v3/glossaries');

async function ensureGlossary(target) {
  const to = GLOSSARY_TARGET[target];
  if (!to) return null;
  const auth = { Authorization: `DeepL-Auth-Key ${KEY}` };
  const entries = KEEP_ENGLISH.map((t) => `${t}\t${t}`).join('\n');

  const list = await (await fetch(GLOSSARY_V3, { headers: auth })).json();
  const found = (list.glossaries ?? []).find((g) => g.name === GLOSSARY_NAME);
  if (found) {
    const has = (found.dictionaries ?? []).some(
      (d) => d.source_lang === 'en' && d.target_lang === to);
    if (has) return found.glossary_id;
    // Add the missing pair to the existing glossary rather than making another.
    const put = await fetch(`${GLOSSARY_V3}/${found.glossary_id}/dictionaries`, {
      method: 'PUT',
      headers: { ...auth, 'Content-Type': 'application/json' },
      body: JSON.stringify({ source_lang: 'en', target_lang: to, entries, entries_format: 'tsv' }),
    });
    if (!put.ok) {
      console.warn(`  glossary dictionary en>${to} failed (${put.status}: ${(await put.text()).slice(0, 120)})`);
      return null;
    }
    console.log(`  glossary dictionary en>${to} added`);
    return found.glossary_id;
  }

  const res = await fetch(GLOSSARY_V3, {
    method: 'POST',
    headers: { ...auth, 'Content-Type': 'application/json' },
    body: JSON.stringify({
      name: GLOSSARY_NAME,
      dictionaries: [{ source_lang: 'en', target_lang: to, entries, entries_format: 'tsv' }],
    }),
  });
  if (!res.ok) {
    console.warn(`  glossary unavailable (${res.status}: ${(await res.text()).slice(0, 160)}), terms may be translated`);
    return null;
  }
  console.log(`  glossary ${GLOSSARY_NAME} created (en>${to})`);
  return (await res.json()).glossary_id;
}

// House style: no em or en dashes in user-facing copy. DeepL introduces them
// freely in German and French, so strip them on the way back in rather than
// hunting for them later.
const cleanDashes = (text) => text.replace(/\s*[–—]\s*/g, ' - ');

async function translateBatch(texts, target, glossaryId) {
  const res = await fetch(API, {
    method: 'POST',
    headers: {
      'Authorization': `DeepL-Auth-Key ${KEY}`,
      'Content-Type': 'application/json',
    },
    body: JSON.stringify({
      text: texts.map(escapeNonMarkup),
      target_lang: target,
      source_lang: 'EN',
      tag_handling: 'xml',
      // <code> holds commands and URLs. Translating inside it produced
      // "NINA. Polaris /releases".
      ignore_tags: ['code'],
      preserve_formatting: true,
      ...(glossaryId ? { glossary_id: glossaryId } : {}),
    }),
  });
  if (!res.ok) throw new Error(`DeepL ${res.status}: ${(await res.text()).slice(0, 300)}`);
  const data = await res.json();
  return data.translations.map((t) => cleanDashes(unprotect(t.text)));
}

/**
 * Collect every translatable leaf, remembering where it came from.
 *
 * Two things this has to get right, both of which it got wrong at first and
 * silently shipped English into the locale files:
 *   - an array of plain strings (`bio: ["...", "...", "..."]`) is a leaf per
 *     element, with the array as the owner and the index as the key;
 *   - SKIP_KEYS names leaf values that must not be translated (a hex colour,
 *     an href). Applying it to containers pruned whole subtrees, which is how
 *     `version: { body: "..." }` never got translated in any language.
 */
function collect(node, slots) {
  if (Array.isArray(node)) {
    node.forEach((v, i) => {
      if (isProse(v)) slots.push({ owner: node, key: i, text: v });
      else collect(v, slots);
    });
    return;
  }
  if (node && typeof node === 'object') {
    for (const [k, v] of Object.entries(node)) {
      if (typeof v === 'string') {
        if (!SKIP_KEYS.has(k) && isProse(v)) slots.push({ owner: node, key: k, text: v });
      } else if (!SKIP_ARRAYS.has(k)) {
        collect(v, slots);
      }
    }
  }
}

const args = process.argv.slice(2);
const dryRun = args.includes('--dry-run');
const [lang, ...only] = args.filter((a) => a !== '--dry-run');

if (!lang || !DEEPL_TARGET[lang]) {
  console.error(`usage: node scripts/pretranslate-site.mjs [--dry-run] <${Object.keys(DEEPL_TARGET).join('|')}> [file...]`);
  process.exit(1);
}
if (!KEY && !dryRun) { console.error('DEEPL_API_KEY is not set'); process.exit(1); }

const picked = only.length ? only : Object.keys(FILES);
const glossaryId = dryRun ? null : await ensureGlossary(DEEPL_TARGET[lang]);
let grandTotal = 0;

for (const name of picked) {
  const rel = FILES[name];
  if (!rel) { console.error(`unknown file: ${name}`); continue; }
  const src = path.join(ROOT, rel);
  const doc = JSON.parse(fs.readFileSync(src, 'utf8'));

  const slots = [];
  collect(doc, slots);
  const chars = slots.reduce((n, s) => n + s.text.length, 0);
  grandTotal += chars;
  console.log(`${name}: ${slots.length} strings, ${chars} characters`);
  if (dryRun) continue;

  // Batch to stay under DeepL's per-request limits.
  for (let i = 0; i < slots.length; i += 40) {
    const batch = slots.slice(i, i + 40);
    const out = await translateBatch(batch.map((s) => s.text), DEEPL_TARGET[lang], glossaryId);
    batch.forEach((slot, j) => { slot.owner[slot.key] = restoreBrackets(slot.text, out[j]); });
    process.stdout.write(`  ${Math.min(i + 40, slots.length)}/${slots.length}\r`);
  }

  const dest = path.join(ROOT, rel.replace(/\.json$/, `.${lang}.json`));
  fs.writeFileSync(dest, JSON.stringify(doc, null, 2) + '\n', 'utf8');
  console.log(`  -> ${path.relative(process.cwd(), dest)}`);
}

console.log(`total: ${grandTotal} characters${dryRun ? ' (dry run, nothing sent)' : ''}`);
