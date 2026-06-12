import { defineConfig } from 'tinacms';

// TinaCMS schema for the landing page. Content lives in
// content/pages/home.json and is edited visually at /admin.
//
// Local/git workflow (free, no Tina Cloud): `npm run dev` starts the editor;
// your edits are written to the JSON files, then commit & push to publish.
// To later enable editing on the *deployed* site, set TINA_CLIENT_ID +
// TINA_TOKEN (Tina Cloud free tier) or wire a self-hosted backend.
const branch =
  process.env.TINA_BRANCH ||
  process.env.GITHUB_BRANCH ||
  process.env.HEAD ||
  'master';

export default defineConfig({
  branch,
  clientId: process.env.TINA_CLIENT_ID || null,
  token: process.env.TINA_TOKEN || null,

  build: {
    outputFolder: 'admin',
    publicFolder: 'public',
  },
  media: {
    tina: {
      mediaRoot: 'assets',
      publicFolder: 'public',
    },
  },

  schema: {
    collections: [
      {
        name: 'page',
        label: 'Landing page',
        path: 'content/pages',
        format: 'json',
        // Single landing page, don't let editors add/delete page docs.
        ui: {
          allowedActions: { create: false, delete: false },
        },
        fields: [
          {
            type: 'object',
            name: 'hero',
            label: 'Hero',
            fields: [
              { type: 'string', name: 'eyebrow', label: 'Eyebrow (small label)' },
              { type: 'string', name: 'titleLine1', label: 'Title line 1' },
              { type: 'string', name: 'titleLine2', label: 'Title line 2' },
              { type: 'string', name: 'titleAccent', label: 'Title line 3 (accent colour)' },
              { type: 'string', name: 'lede', label: 'Lede paragraph', ui: { component: 'textarea' } },
            ],
          },
          {
            type: 'object',
            name: 'stats',
            label: 'Hero stats',
            list: true,
            ui: { itemProps: (i) => ({ label: `${i?.value ?? ''} ${i?.label ?? ''}` }) },
            fields: [
              { type: 'string', name: 'value', label: 'Value' },
              { type: 'string', name: 'label', label: 'Label' },
            ],
          },
          {
            type: 'object',
            name: 'featuresSection',
            label: 'Features heading',
            fields: [
              { type: 'string', name: 'title', label: 'Title' },
              { type: 'string', name: 'sub', label: 'Subtitle', ui: { component: 'textarea' } },
            ],
          },
          {
            type: 'object',
            name: 'features',
            label: 'Feature rows',
            list: true,
            ui: { itemProps: (i) => ({ label: i?.title }) },
            fields: [
              { type: 'string', name: 'icon', label: 'Icon (emoji)' },
              { type: 'string', name: 'title', label: 'Title' },
              { type: 'string', name: 'body', label: 'Body', ui: { component: 'textarea' } },
              { type: 'image', name: 'image', label: 'Screenshot' },
            ],
          },
          {
            type: 'object',
            name: 'benchmarks',
            label: 'Benchmarks',
            fields: [
              { type: 'string', name: 'title', label: 'Title' },
              { type: 'string', name: 'sub', label: 'Subtitle', ui: { component: 'textarea' } },
              {
                type: 'object',
                name: 'rows',
                label: 'Devices (table rows)',
                list: true,
                ui: { itemProps: (i) => ({ label: `${i?.device ?? ''}: ${i?.score ?? ''}` }) },
                fields: [
                  { type: 'string', name: 'device', label: 'Device' },
                  { type: 'string', name: 'url', label: 'Product page URL (optional)' },
                  { type: 'string', name: 'cores', label: 'Cores / threads' },
                  { type: 'string', name: 'score', label: 'Polaris score' },
                  { type: 'string', name: 'stacking', label: 'Stacking (Mpx/s)' },
                  { type: 'string', name: 'capture', label: 'Capture (Mpx/s)' },
                  { type: 'string', name: 'memory', label: 'Memory bandwidth' },
                  { type: 'boolean', name: 'highlight', label: 'Highlight this row' },
                ],
              },
              { type: 'string', name: 'note', label: 'Footnote', ui: { component: 'textarea' } },
              { type: 'string', name: 'docUrl', label: 'Full benchmark docs URL' },
            ],
          },
          {
            type: 'object',
            name: 'downloadsSection',
            label: 'Download heading',
            fields: [
              { type: 'string', name: 'title', label: 'Title' },
              { type: 'string', name: 'sub', label: 'Subtitle', ui: { component: 'textarea' } },
            ],
          },
          {
            type: 'object',
            name: 'downloads',
            label: 'Download cards',
            list: true,
            ui: { itemProps: (i) => ({ label: i?.platform }) },
            fields: [
              { type: 'string', name: 'platform', label: 'Platform' },
              { type: 'string', name: 'badge', label: 'Badge (file type)' },
              { type: 'string', name: 'url', label: 'Direct download URL (optional)' },
              { type: 'string', name: 'command', label: 'Install commands', ui: { component: 'textarea' } },
              { type: 'string', name: 'note', label: 'Note', ui: { component: 'textarea' } },
            ],
          },
        ],
      },
      {
        name: 'install',
        label: 'Install guide',
        path: 'content/install',
        format: 'json',
        ui: { allowedActions: { create: false, delete: false } },
        fields: [
          { type: 'string', name: 'title', label: 'Page title' },
          { type: 'string', name: 'lede', label: 'Intro paragraph', ui: { component: 'textarea' } },
          {
            type: 'object',
            name: 'requirements',
            label: 'System requirements',
            fields: [
              { type: 'string', name: 'title', label: 'Title' },
              { type: 'string', name: 'sub', label: 'Subtitle', ui: { component: 'textarea' } },
              { type: 'string', name: 'ramNote', label: 'RAM callout', ui: { component: 'textarea' } },
              { type: 'string', name: 'recommendedNote', label: 'Recommended-board callout', ui: { component: 'textarea' } },
              {
                type: 'object',
                name: 'rows',
                label: 'Hardware rows',
                list: true,
                ui: { itemProps: (i) => ({ label: i?.host }) },
                fields: [
                  { type: 'string', name: 'host', label: 'Host' },
                  { type: 'string', name: 'ram', label: 'RAM' },
                  { type: 'string', name: 'status', label: 'Status' },
                  { type: 'boolean', name: 'highlight', label: 'Highlight row (GPU + NPU board)' },
                  { type: 'string', name: 'notes', label: 'Notes', ui: { component: 'textarea' } },
                ],
              },
            ],
          },
          {
            type: 'object',
            name: 'images',
            label: 'Ready-to-flash images',
            fields: [
              { type: 'string', name: 'title', label: 'Title' },
              { type: 'string', name: 'sub', label: 'Subtitle', ui: { component: 'textarea' } },
              {
                type: 'object',
                name: 'items',
                label: 'Image cards',
                list: true,
                ui: { itemProps: (i) => ({ label: i?.device }) },
                fields: [
                  { type: 'string', name: 'device', label: 'Device' },
                  { type: 'string', name: 'badge', label: 'Badge (arch)' },
                  { type: 'string', name: 'url', label: 'Download URL (empty = Coming soon)' },
                  { type: 'string', name: 'size', label: 'File size (optional)' },
                  { type: 'string', name: 'note', label: 'Note (optional)' },
                ],
              },
            ],
          },
          {
            type: 'object',
            name: 'tldr',
            label: 'Bare-minimum table',
            fields: [
              { type: 'string', name: 'title', label: 'Title' },
              { type: 'string', name: 'note', label: 'Note', ui: { component: 'textarea' } },
              {
                type: 'object',
                name: 'rows',
                label: 'Rows',
                list: true,
                ui: { itemProps: (i) => ({ label: i?.label }) },
                fields: [
                  { type: 'string', name: 'label', label: 'Label' },
                  { type: 'string', name: 'windows', label: 'Windows' },
                  { type: 'string', name: 'linux', label: 'Linux' },
                ],
              },
            ],
          },
          {
            type: 'object',
            name: 'platformsSection',
            label: 'Platforms heading',
            fields: [
              { type: 'string', name: 'title', label: 'Title' },
              { type: 'string', name: 'sub', label: 'Subtitle', ui: { component: 'textarea' } },
            ],
          },
          {
            type: 'object',
            name: 'platforms',
            label: 'Platforms',
            list: true,
            ui: { itemProps: (i) => ({ label: i?.name }) },
            fields: [
              { type: 'string', name: 'name', label: 'Name' },
              { type: 'string', name: 'badge', label: 'Badge' },
              { type: 'string', name: 'summary', label: 'Summary', ui: { component: 'textarea' } },
              { type: 'string', name: 'steps', label: 'Install commands', ui: { component: 'textarea' } },
              { type: 'string', name: 'notes', label: 'Notes', ui: { component: 'textarea' } },
            ],
          },
          {
            type: 'object',
            name: 'firewall',
            label: 'Firewall',
            fields: [
              { type: 'string', name: 'title', label: 'Title' },
              { type: 'string', name: 'sub', label: 'Subtitle', ui: { component: 'textarea' } },
              { type: 'string', name: 'windows', label: 'Windows commands', ui: { component: 'textarea' } },
              { type: 'string', name: 'linux', label: 'Linux commands', ui: { component: 'textarea' } },
              { type: 'string', name: 'note', label: 'Note', ui: { component: 'textarea' } },
            ],
          },
          {
            type: 'object',
            name: 'optionalSection',
            label: 'Optional components heading',
            fields: [
              { type: 'string', name: 'title', label: 'Title' },
              { type: 'string', name: 'sub', label: 'Subtitle', ui: { component: 'textarea' } },
            ],
          },
          {
            type: 'object',
            name: 'optionalGroups',
            label: 'Optional components',
            list: true,
            ui: { itemProps: (i) => ({ label: i?.name }) },
            fields: [
              { type: 'string', name: 'name', label: 'Group name' },
              { type: 'string', name: 'intro', label: 'Intro', ui: { component: 'textarea' } },
              {
                type: 'object',
                name: 'items',
                label: 'Components',
                list: true,
                ui: { itemProps: (i) => ({ label: i?.name }) },
                fields: [
                  { type: 'string', name: 'name', label: 'Name' },
                  { type: 'string', name: 'badge', label: 'Badge' },
                  { type: 'string', name: 'purpose', label: 'Purpose', ui: { component: 'textarea' } },
                  { type: 'string', name: 'url', label: 'Link URL' },
                  { type: 'string', name: 'windows', label: 'Windows install' },
                  { type: 'string', name: 'linux', label: 'Linux install' },
                ],
              },
            ],
          },
        ],
      },
      {
        name: 'guide',
        label: 'Getting started',
        path: 'content/guide',
        format: 'json',
        ui: { allowedActions: { create: false, delete: false } },
        fields: [
          { type: 'string', name: 'title', label: 'Page title' },
          { type: 'string', name: 'lede', label: 'Intro paragraph', ui: { component: 'textarea' } },
          {
            type: 'object',
            name: 'steps',
            label: 'Steps',
            list: true,
            ui: { itemProps: (i) => ({ label: i?.title }) },
            fields: [
              { type: 'string', name: 'icon', label: 'Icon (emoji)' },
              { type: 'string', name: 'title', label: 'Title' },
              { type: 'string', name: 'body', label: 'Body', ui: { component: 'textarea' } },
              { type: 'string', name: 'tip', label: 'Tip', ui: { component: 'textarea' } },
              { type: 'image', name: 'image', label: 'Screenshot' },
            ],
          },
          {
            type: 'object',
            name: 'closing',
            label: 'Closing',
            fields: [
              { type: 'string', name: 'title', label: 'Title' },
              { type: 'string', name: 'sub', label: 'Subtitle', ui: { component: 'textarea' } },
              { type: 'string', name: 'docUrl', label: 'Full docs URL' },
            ],
          },
        ],
      },
    ],
  },
});
