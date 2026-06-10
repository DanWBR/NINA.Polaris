# N.I.N.A. Polaris — website

The marketing site for N.I.N.A. Polaris, built with [Astro](https://astro.build).
Static output, so it deploys anywhere (GitHub Pages, Netlify, Vercel,
Cloudflare Pages, or bundled into the app's `wwwroot/`).

## Develop

```bash
cd website
npm install          # first time only
npm run dev          # http://localhost:4321 with hot reload
```

## Build

```bash
npm run build        # → ./dist (static HTML/CSS/JS)
npm run preview      # serve ./dist locally to sanity-check
```

## Project layout

```
website/
├── astro.config.mjs        # site URL + build options
├── public/                 # copied verbatim → /
│   ├── favicon.svg
│   └── assets/horsehead.jpg
└── src/
    ├── data/site.ts        # ← ALL content lives here (links, features, stats…)
    ├── styles/global.css   # design tokens + shared classes
    ├── layouts/Layout.astro# <head>, meta/OG tags
    ├── components/         # Nav, Hero, Features, GettingStarted, Stack, CTA, Footer
    └── pages/index.astro   # composes the components
```

### Design system

The palette and fonts mirror the Polaris app
([`src/NINA.Polaris/wwwroot/css/app.css`](../src/NINA.Polaris/wwwroot/css/app.css)).
The `:root` tokens in [`src/styles/global.css`](src/styles/global.css) are a
copy of the app's — `--bg-primary #1a1a2e`, `--accent #2196f3`, `--border
#2a2a4a`, `--radius 6px`, etc. Body font is **Atkinson Hyperlegible** and mono
is **JetBrains Mono**, both self-hosted in `public/fonts/` (same OFL-1.1 files
the app vendors). If you re-theme the app, update those tokens here to match.

### Editing content

Almost everything is data-driven: edit **`src/data/site.ts`** to change the
feature cards, quick-start snippets, stack list, stats, or links — no markup
changes needed. Layout/visuals live in each component's `<style>` block and in
`src/styles/global.css` (the `:root` design tokens control the whole palette).

## Deploy

The build is plain static files in `dist/`. Pick a host:

### GitHub Pages
Two ways:
- **Folder build in CI** — add a workflow that runs `npm ci && npm run build`
  in `website/` and publishes `website/dist`. If you deploy to a *project*
  page (`https://<user>.github.io/NINA.Polaris/`), set `base: '/NINA.Polaris/'`
  in `astro.config.mjs`. For a custom domain or a `<user>.github.io` repo,
  leave `base` unset.

### Netlify / Vercel / Cloudflare Pages
Point the host at this repo with:
- **Base directory:** `website`
- **Build command:** `npm run build`
- **Publish directory:** `website/dist` (Netlify) / `dist` (Vercel, Cloudflare)

PR previews and push-to-deploy work out of the box. Update `site:` in
`astro.config.mjs` to your final domain so OG/canonical URLs are correct.

### Bundled with the app
`npm run build`, then copy `dist/*` into `src/NINA.Polaris/wwwroot/` and it's
served at `http://your-rig:5000/` alongside the app.

## Notes

- This supersedes the old zero-build `landing/` page. Once you're happy with
  this one, `landing/` can be deleted.
- The hero background (`public/assets/horsehead.jpg`) is IC 434 / Horsehead +
  NGC 2024 Flame, used by permission of M. Pugh.
