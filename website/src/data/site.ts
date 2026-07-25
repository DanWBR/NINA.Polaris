// Editorial content (hero text, features, screenshots, quick-start, stack, CTA,
// section headings) is managed by TinaCMS and lives in
// content/pages/home.json. Edit it visually at /admin (run `npm run dev`),
// or by hand in the JSON. The exports below just re-surface that content to the
// components, so nothing else needs to change.
import home from '../../content/pages/home.json';
import install from '../../content/install/install.json';
import gettingStarted from '../../content/guide/getting-started.json';
import featuresPage from '../../content/features/features.json';
import aboutPage from '../../content/about/about.json';
import aiToolsJson from '../../content/ai-tools/ai-tools.json';
import assistantJson from '../../content/assistant/assistant.json';

// Chrome that is not page content: nav and footer labels, the strings baked
// into components, and the two prose fields of `site`. They live in
// content/_ui/ so the translation pipeline can reach them like any other copy.
import siteCopy from '../../content/_ui/site.json';
import navLinksJson from '../../content/_ui/navLinks.json';
import footerColsJson from '../../content/_ui/footerCols.json';
import uiStrings from '../../content/_ui/strings.json';

// ---- Static site config (links/meta, not editorial copy) ----
export const site = {
  name: 'Polaris Astro Controller',
  tagline: siteCopy.tagline,
  description: siteCopy.description,
  ogImage: '/assets/horsehead.jpg',
  repo: 'https://github.com/DanWBR/NINA.Polaris',
  issues: 'https://github.com/DanWBR/NINA.Polaris/issues',
  readme: 'https://github.com/DanWBR/NINA.Polaris/blob/master/README.md',
  donate: 'https://buy.stripe.com/9B68wPeoLcMSgOz2iJbMQ02',
  patreon: 'https://www.patreon.com/c/nina_polaris',
  discord: 'https://discord.gg/FYQeNhEGDp',
};

// The Handbook entry points at the static Quarto book rendered into
// public/handbook/ by CI (deploy-website.yml), not at an Astro route.
export const navLinks: { label: string; href: string; external?: boolean }[] =
  navLinksJson;

export const footerCols: {
  title: string;
  links: { label: string; href: string; external?: boolean }[];
}[] = footerColsJson;

/** Strings that live in components rather than in a page's content file. */
export const strings = uiStrings;

// ---- Tina-managed editorial content (content/pages/home.json) ----
export const hero = home.hero;
export const featuresSection = home.featuresSection;
export const features = home.features;
export const benchmarks = home.benchmarks;
export const comparison = home.comparison;
export const closing = home.closing;
export const gallery = home.gallery;

// ---- Install guide (content/install/install.json) ----
export const installGuide = install;

// ---- Getting started (content/guide/getting-started.json) ----
export const guide = gettingStarted;

// ---- Features page (content/features/features.json) ----
export const featurePage = featuresPage;

// ---- About page (content/about/about.json) ----
export const aboutContent = aboutPage;

// ---- AI Tools page (content/ai-tools/ai-tools.json) ----
export const aiToolsPage = aiToolsJson;

export const assistantPage = assistantJson;

// ---------------------------------------------------------------------------
// Localisation
//
// English is the source and stays in `content/<col>/<name>.json`. A translation
// is a sibling carrying the tag: `content/pages/home.pt-BR.json`.
//
// Overrides merge PER KEY, not per file. A translator can land a single heading
// and everything around it keeps rendering in English, which is what makes it
// safe to translate this site incrementally instead of in one 64,000-word push.
// The alternative (whole-file switching) would force every file to be finished
// before any of it could ship.
// ---------------------------------------------------------------------------

export type Lang = 'en' | 'pt-BR' | 'es' | 'fr' | 'de';

/** English first: it is the source, and the order drives the language picker. */
export const LANGS: Lang[] = ['en', 'pt-BR', 'es', 'fr', 'de'];

/** URL segment for a language. English has none: it is served at the root. */
export function langPrefix(lang: Lang): string {
  return lang === 'en' ? '' : `/${lang.toLowerCase()}`;
}

/**
 * Drop a leading language segment from a path, so a page can rebuild the same
 * URL in another language. Returns a path that always starts with '/'.
 */
export function stripLangPrefix(pathname: string): string {
  for (const l of LANGS) {
    if (l === 'en') continue;
    const p = `/${l.toLowerCase()}`;
    if (pathname === p || pathname.startsWith(`${p}/`)) {
      return pathname.slice(p.length) || '/';
    }
  }
  return pathname || '/';
}

/** Prefix an internal link with the current language. */
export function localePath(pathname: string, lang: Lang): string {
  return `${langPrefix(lang)}${stripLangPrefix(pathname)}` || '/';
}

export function isLang(value: unknown): value is Lang {
  return typeof value === 'string' && (LANGS as string[]).includes(value);
}

// Vite resolves this at build time, so a language with no files contributes
// nothing and every key falls through to English. That is also why step one of
// this migration renders byte-identical output: there are no locale files yet.
const localeModules = import.meta.glob<{ default: unknown }>(
  '../../content/**/*.json',
  { eager: true },
);

/** `content/pages/home.pt-BR.json` -> { id: 'pages/home', lang: 'pt-BR' }. */
function parseLocalePath(path: string): { id: string; lang: Lang } | null {
  const m = path.match(/content\/([^/]+)\/(.+)\.json$/);
  if (!m) return null;
  const [, collection, stem] = m;
  const dot = stem.lastIndexOf('.');
  if (dot < 0) return null;               // the English base file
  const tag = stem.slice(dot + 1);
  if (!isLang(tag) || tag === 'en') return null;
  return { id: `${collection}/${stem.slice(0, dot)}`, lang: tag };
}

const overrides = new Map<string, unknown>();
for (const [path, mod] of Object.entries(localeModules)) {
  const parsed = parseLocalePath(path);
  if (parsed) overrides.set(`${parsed.lang}:${parsed.id}`, mod.default);
}

/**
 * Deep-merge a translation over the English base.
 *
 * Arrays are merged BY INDEX rather than replaced, so translating the first two
 * items of a ten-item list leaves the other eight in English instead of
 * dropping them. Replacing would silently shorten lists whenever a translation
 * lagged behind a newly added entry.
 */
function merge<T>(base: T, over: unknown): T {
  if (over === undefined || over === null) return base;
  if (Array.isArray(base)) {
    if (!Array.isArray(over)) return base;
    return base.map((item, i) => merge(item, over[i])) as unknown as T;
  }
  if (base && typeof base === 'object') {
    if (!over || typeof over !== 'object' || Array.isArray(over)) return base;
    const out: Record<string, unknown> = { ...(base as Record<string, unknown>) };
    for (const key of Object.keys(over as Record<string, unknown>)) {
      out[key] = key in out
        ? merge((base as Record<string, unknown>)[key], (over as Record<string, unknown>)[key])
        : (over as Record<string, unknown>)[key];
    }
    return out as T;
  }
  // Leaf: take the translation, but never let an empty string blank the source.
  return (typeof over === 'string' && over.trim() === '' ? base : over) as T;
}

function localised<T>(id: string, base: T, lang: Lang): T {
  return lang === 'en' ? base : merge(base, overrides.get(`${lang}:${id}`));
}

/**
 * Every piece of editorial copy, resolved for one language. Components take a
 * `lang` prop and read through this instead of importing the English constants
 * directly, so the same component renders any language.
 */
export function getContent(lang: Lang = 'en') {
  const h = localised('pages/home', home, lang);
  return {
    hero: h.hero,
    featuresSection: h.featuresSection,
    features: h.features,
    benchmarks: h.benchmarks,
    comparison: h.comparison,
    closing: h.closing,
    gallery: h.gallery,
    installGuide: localised('install/install', install, lang),
    guide: localised('guide/getting-started', gettingStarted, lang),
    featurePage: localised('features/features', featuresPage, lang),
    aboutContent: localised('about/about', aboutPage, lang),
    aiToolsPage: localised('ai-tools/ai-tools', aiToolsJson, lang),
    assistantPage: localised('assistant/assistant', assistantJson, lang),
    // Navigation and footer labels live in this file rather than the CMS, so
    // they are localised from here too once the locale files exist.
    navLinks: localised('_ui/navLinks', navLinks, lang),
    footerCols: localised('_ui/footerCols', footerCols, lang),
    site: localised('_ui/site', site, lang),
    strings: localised('_ui/strings', strings, lang),
  };
}
