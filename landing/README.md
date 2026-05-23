# Landing page

Stand-alone marketing page for N.I.N.A. Polaris. No build step, no
dependencies — just an HTML file + a CSS file + one background image.

## Files

- `index.html` — the page
- `styles.css` — all styles (dark theme matching the app)
- `assets/horsehead.jpg` — hero background (IC 434 / Horsehead + NGC 2024 Flame)
  **You need to drop the JPG here yourself** — the page falls back to a
  deep-space gradient when the file is missing, so it's never broken,
  but the hero looks much better with the real image.

## Preview locally

Any static file server works. Two one-liners:

```bash
# Python
python3 -m http.server -d landing 8080
# Then open http://localhost:8080/

# Node
npx serve landing
```

Or just open `landing/index.html` directly in a browser — the file://
protocol is fine since there's no JS fetching anything.

## Deploy

Three options:

1. **GitHub Pages** — point Pages at the `landing/` folder in this repo
2. **Static host** — upload the three files to Netlify / Vercel / S3 / Cloudflare Pages
3. **Bundled with the app** — copy `landing/*` into `src/NINA.Polaris/wwwroot/`
   and it's served at `http://your-rig:5000/index.html` automatically

## Editing

The HTML is self-documenting. To change a feature card, edit the
`<article class="feature">` blocks in `index.html`. To swap the
background image, replace `assets/horsehead.jpg` (keep the filename
or update both `index.html` `<meta property="og:image">` and the two
CSS `background-image: url("assets/...")` rules).
