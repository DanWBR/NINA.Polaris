// @ts-check
import { defineConfig } from 'astro/config';

// Deployed to GitHub Pages on the custom domain polaris-astro.app.br,
// served at the domain root, so no `base` prefix is needed. `site` is
// used for absolute URLs in <meta>/OG tags and the canonical link.
//
// The custom domain is pinned by public/CNAME (copied to dist/CNAME).
// If you ever move off the custom domain to a project page
// (https://danwbr.github.io/NINA.Polaris/), set base: '/NINA.Polaris/'
// and make internal links base-aware.
export default defineConfig({
  site: 'https://polaris-astro.app.br',
  build: {
    inlineStylesheets: 'auto',
  },
});
