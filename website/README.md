# N.I.N.A. Polaris website

The marketing site for N.I.N.A. Polaris, built with [Astro](https://astro.build)
and edited visually with [TinaCMS](https://tina.io). Static output, so it deploys
anywhere (GitHub Pages, Netlify, Vercel, Cloudflare Pages, or bundled into the
app's `wwwroot/`).

## Develop

```bash
cd website
npm install          # first time only
npm run dev          # Tina + Astro together
```

- **Site:** http://localhost:4321 (hot reload)
- **Visual editor:** http://localhost:4321/admin/index.html

`npm run dev` runs `tinacms dev -c "astro dev"`, which starts Astro plus Tina's
local content server. To run Astro alone (no editor), use `npm run dev:astro`.

## Build

```bash
npm run build        # → ./dist (static HTML/CSS/JS)
npm run preview      # serve ./dist locally to sanity-check
```

## Project layout

```
website/
├── astro.config.mjs        # site URL + build options
├── tina/config.ts          # TinaCMS schema (the /admin form fields)
├── content/
│   ├── pages/home.json         # ← landing-page content (what Tina edits)
│   ├── install/install.json    # ← install-guide content
│   └── guide/getting-started.json # ← getting-started content
├── public/                 # copied verbatim → /
│   ├── favicon.svg
│   ├── fonts/              # Atkinson Hyperlegible + JetBrains Mono (self-hosted)
│   └── assets/             # horsehead.jpg + screenshots/
└── src/
    ├── data/site.ts        # static config (links/meta) + re-exports JSON content
    ├── styles/global.css   # design tokens + shared classes
    ├── layouts/Layout.astro# <head>, meta/OG tags
    ├── components/         # Nav, Hero, Features, Benchmarks, Downloads, Footer
    └── pages/
        ├── index.astro            # landing page
        ├── install.astro          # install guide (/install)
        └── getting-started.astro  # first-night guide (/getting-started)
```

### Design system

The palette and fonts mirror the Polaris app
([`src/NINA.Polaris/wwwroot/css/app.css`](../src/NINA.Polaris/wwwroot/css/app.css)).
The `:root` tokens in [`src/styles/global.css`](src/styles/global.css) are a
copy of the app's: `--bg-primary #1a1a2e`, `--accent #2196f3`, `--border
#2a2a4a`, `--radius 6px`, etc. Body font is **Atkinson Hyperlegible** and mono
is **JetBrains Mono**, both self-hosted in `public/fonts/` (same OFL-1.1 files
the app vendors). If you re-theme the app, update those tokens here to match.

### Editing content (TinaCMS)

All editorial copy (hero text, the feature rows with screenshots, the benchmark
comparison table, and the download cards) lives in
**`content/pages/home.json`** and is edited visually:

1. `npm run dev`
2. open http://localhost:4321/admin/index.html
3. edit in the form panel with live preview, then **Save**

Saving writes back to `content/pages/home.json`. **Commit & push that file to
publish.** The workflow is fully git-based, no external service, no database.
You can of course also hand-edit the JSON directly.

The field layout (what shows up in the editor) is defined in
[`tina/config.ts`](tina/config.ts). Structural config that isn't editorial copy
(nav links, footer links, repo/donate URLs, SEO meta) stays in code at
[`src/data/site.ts`](src/data/site.ts). Layout/visuals live in each component's
`<style>` block and in `src/styles/global.css` (the `:root` tokens drive the
palette).

> **Editing on the deployed site:** the local workflow above is free and needs
> nothing. To let editors save changes from the *published* URL, add Tina Cloud
> (free tier, set `TINA_CLIENT_ID` + `TINA_TOKEN`) or a self-hosted backend,
> then `npm run build:admin` to ship the `/admin` app. Optional, add later.

## Deploy

Live on **GitHub Pages** at the custom domain **https://polaris-astro.app.br**,
built and published by [`.github/workflows/deploy-website.yml`](../.github/workflows/deploy-website.yml)
on every push that touches `website/`.

### How it works
- The workflow runs `withastro/action` against `./website` (npm ci + `astro
  build`), then `actions/deploy-pages` publishes `dist/`.
- The custom domain is pinned by [`public/CNAME`](public/CNAME), which is copied
  to `dist/CNAME`. `site:` in `astro.config.mjs` is set to the domain so OG and
  canonical URLs are absolute. The site is served at the domain root, so there
  is no `base` prefix.

### One-time setup (repo + DNS)
1. **Repo → Settings → Pages → Build and deployment → Source: GitHub Actions.**
2. **Repo → Settings → Pages → Custom domain:** enter `polaris-astro.app.br`
   (the CNAME file already does this, but set it here too, then tick *Enforce
   HTTPS* once the cert is issued).
3. **DNS at your registrar** (apex domain → GitHub Pages A records):
   ```
   A   @   185.199.108.153
   A   @   185.199.109.153
   A   @   185.199.110.153
   A   @   185.199.111.153
   ```
   (Optionally add the four AAAA records for IPv6: `2606:50c0:8000::153`,
   `8001::153`, `8002::153`, `8003::153`.) DNS + the Let's Encrypt cert can take
   up to ~24 h to go fully green.

After that, every push to `master` under `website/` redeploys automatically. You
can also trigger it by hand from the Actions tab (workflow_dispatch).

### Other hosts
The build is plain static files in `dist/`, so it also drops onto Netlify /
Vercel / Cloudflare Pages (base dir `website`, build `npm run build`, publish
`website/dist`), or into the app's `wwwroot/` (`npm run build`, copy `dist/*`).

## Notes

- This replaced the old zero-build `landing/` page (removed).
- The hero background (`public/assets/horsehead.jpg`) is IC 434 / Horsehead +
  NGC 2024 Flame, used by permission of M. Pugh.
- Fonts under `public/fonts/` are OFL-1.1; attribution in
  `public/fonts/README.md`.
