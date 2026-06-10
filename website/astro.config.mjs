// @ts-check
import { defineConfig } from 'astro/config';

// Static output works on any host (GitHub Pages, Netlify, Vercel,
// Cloudflare Pages, or bundled into the app's wwwroot/).
//
// `site` is used for absolute URLs in <meta> tags and the sitemap.
// Update it once you pick a domain. If you deploy to a GitHub Pages
// *project* page (https://user.github.io/NINA.Polaris/), also set
// `base: '/NINA.Polaris/'` below.
export default defineConfig({
  site: 'https://ninapolaris.app',
  // base: '/NINA.Polaris/',
  build: {
    inlineStylesheets: 'auto',
  },
});
