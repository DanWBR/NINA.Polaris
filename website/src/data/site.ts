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

// ---- Static site config (links/meta, not editorial copy) ----
export const site = {
  name: 'N.I.N.A. Polaris',
  tagline: 'Browser-controlled astrophotography for any device',
  description:
    "A lightweight, browser-controlled astrophotography control system on ASP.NET Core. Runs on Raspberry Pi, mini PCs, and Windows. INDI + ASCOM/Alpaca, PHD2 guiding, plate solving, live stacking, advanced sequencer, and a relay server for remote access.",
  ogImage: '/assets/horsehead.jpg',
  repo: 'https://github.com/DanWBR/NINA.Polaris',
  issues: 'https://github.com/DanWBR/NINA.Polaris/issues',
  readme: 'https://github.com/DanWBR/NINA.Polaris/blob/master/README.md',
  donate: 'https://buy.stripe.com/9B68wPeoLcMSgOz2iJbMQ02',
  patreon: 'https://www.patreon.com/c/nina_polaris',
  discord: 'https://discord.gg/FYQeNhEGDp',
};

export const navLinks = [
  { label: 'Features', href: '/features' },
  { label: 'Get Started', href: '/getting-started' },
  { label: 'Download & Install', href: '/install' },
  { label: 'About', href: '/about' },
  { label: 'Discord ↗', href: site.discord, external: true },
  { label: 'Patreon ↗', href: site.patreon, external: true },
  { label: 'GitHub ↗', href: site.repo, external: true },
];

export const footerCols = [
  {
    title: 'Project',
    links: [
      { label: 'All features', href: '/features', external: false },
      { label: 'Getting started', href: '/getting-started', external: false },
      { label: 'About', href: '/about', external: false },
      { label: 'Download & Install', href: '/install', external: false },
      { label: 'GitHub repo', href: site.repo },
      { label: 'README', href: site.readme },
      { label: 'Issues / requests', href: site.issues },
      { label: 'Discord (community)', href: site.discord },
      { label: 'Patreon (dev news)', href: site.patreon },
    ],
  },
  {
    title: 'Related',
    links: [
      { label: 'N.I.N.A. (desktop, upstream)', href: 'https://nighttime-imaging.eu/' },
      { label: 'INDI library', href: 'https://indilib.org/' },
      { label: 'ASCOM / Alpaca', href: 'https://ascom-standards.org/' },
      { label: 'PHD2 guiding', href: 'https://openphdguiding.org/' },
    ],
  },
];

// ---- Tina-managed editorial content (content/pages/home.json) ----
export const hero = home.hero;
export const featuresSection = home.featuresSection;
export const features = home.features;
export const benchmarks = home.benchmarks;
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
