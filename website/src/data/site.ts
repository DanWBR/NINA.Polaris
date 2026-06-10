// Single source of truth for the site's content + links.
// Edit here and every component updates.

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
};

export const navLinks = [
  { label: 'Features', href: '#features' },
  { label: 'Screenshots', href: '#screenshots' },
  { label: 'Get started', href: '#getting-started' },
  { label: 'Stack', href: '#stack' },
  { label: 'GitHub ↗', href: site.repo, external: true },
];

export const stats = [
  { value: '181', label: 'unit tests' },
  { value: '30+', label: 'sequencer instructions' },
  { value: '9', label: 'device types' },
  { value: '0', label: 'cloud dependencies' },
];

export const features = [
  {
    icon: '⚙️',
    title: '9 device types',
    body: 'Camera, mount, focuser, filter wheel, rotator, dome, flat panel, weather, guider — over INDI or ASCOM/Alpaca, with auto-discovery on the LAN.',
  },
  {
    icon: '🎯',
    title: 'Plate-solve & Slew-and-Center',
    body: 'ASTAP, PlateSolve3, Astrometry.net (online + local), primary + blind fallback. Auto-pushes the solved focal length back into the rig.',
  },
  {
    icon: '📡',
    title: 'PHD2 guiding, fully managed',
    body: 'JSON-RPC client with live RA/Dec chart, dither, settle params, profile switcher. Auto-launches PHD2 at startup if you opt in.',
  },
  {
    icon: '🌲',
    title: 'Advanced Sequencer (tree)',
    body: 'Containers, loop conditions, event triggers (auto-focus on temp/HFR, meridian flip, dither, safety abort). Drag-drop tree editor in the browser.',
  },
  {
    icon: '🧩',
    title: 'Mosaic planner',
    body: 'N×M grid with cos(δ) correction + serpentine slew order. One click lowers the plan into a runnable sequence.',
  },
  {
    icon: '📸',
    title: 'Live stack & adaptive stream',
    body: 'EAA-style live stacking over WebSocket with LZ4 compression. Auto-downgrades raw → JPEG when WiFi gets flaky.',
  },
  {
    icon: '🌐',
    title: 'Remote relay (no port-forward)',
    body: "Outbound WebSocket to a tiny relay server gives you internet access without DDNS. Per-tenant quotas, mTLS, web admin, built-in Let's Encrypt.",
  },
  {
    icon: '🔌',
    title: 'Plugin system',
    body: 'Drop a .NET .dll into plugins/, ship custom sequencer instructions. Isolated load context — bad plugins can’t take the host down.',
  },
  {
    icon: '🛰️',
    title: 'Offline sky viewer',
    body: 'Canvas-based starfield with the brightest stars + constellations + DSO catalog. Works without internet — perfect for dark-site observatories.',
  },
];

// Screenshot gallery. Drop the real PNG/JPG into public/assets/screenshots/
// using the filename in `src`. Until the file exists, a styled placeholder
// with the title is shown — the section never looks broken.
export const screenshots = [
  {
    src: '/assets/screenshots/sequencer.png',
    title: 'Advanced Sequencer',
    caption: 'Drag-and-drop tree editor with containers, triggers, and loop conditions.',
  },
  {
    src: '/assets/screenshots/live-view.png',
    title: 'Live view & stacking',
    caption: 'EAA-style live stack with client-side stretch over a bandwidth-adaptive stream.',
  },
  {
    src: '/assets/screenshots/sky-map.png',
    title: 'Sky map & atlas',
    caption: 'Offline starfield with constellations and a searchable DSO catalog.',
  },
  {
    src: '/assets/screenshots/guiding.png',
    title: 'PHD2 guiding',
    caption: 'Live RA/Dec chart, dither and settle controls, profile switching.',
  },
  {
    src: '/assets/screenshots/rigs.png',
    title: 'Equipment rigs',
    caption: 'Auto-discovered INDI / ASCOM Alpaca devices, organised per rig.',
  },
  {
    src: '/assets/screenshots/focus.png',
    title: 'Auto-focus (V-curve)',
    caption: 'HFR vs. position curve fitting with hyperbolic / parabolic models.',
  },
];

export const quickStart = [
  {
    tag: 'RPi',
    title: 'Raspberry Pi 4 / 5',
    code: `git clone https://github.com/DanWBR/NINA.Polaris.git
cd NINA.Polaris
./deploy/publish-linux-arm64.sh
./publish/linux-arm64/NINA.Polaris`,
    note: 'Then point any browser at http://nina-<hostname>.local:5000 — the mDNS announcer ships built-in.',
  },
  {
    tag: 'Windows',
    title: 'Windows mini PC',
    code: `git clone https://github.com/DanWBR/NINA.Polaris.git
cd NINA.Polaris
.\\deploy\\publish-win-x64.ps1
.\\publish\\win-x64\\NINA.Polaris.exe`,
    note: 'Or -InstallService for a Windows Service that survives reboots. Discovers ASCOM Remote (Alpaca) drivers on the LAN automatically.',
  },
  {
    tag: 'Docker',
    title: 'Container',
    code: `docker run -d --network host \\
  -v $(pwd)/config:/config \\
  -v $(pwd)/images:/images \\
  ghcr.io/danwbr/nina-polaris:latest`,
    note: 'Multi-arch image (arm64 + amd64). The compose file in the repo includes indiserver in the same stack.',
  },
];

export const stack = [
  { name: 'ASP.NET Core 10', rest: 'Minimal API, single-binary publish, runs on Linux ARM64 / x64 / Windows' },
  { name: 'Alpine.js + plain JS', rest: 'the UI is ~3700 lines, no build step, hot-editable' },
  { name: 'WebSockets', rest: 'binary image relay (LZ4 frames) + status broadcast at 1 Hz' },
  { name: 'INDI + ASCOM Alpaca', rest: 'INDI over TCP/XML and Alpaca over HTTP, auto-discovery for both' },
  { name: 'Chart.js', rest: 'guiding / focus / HFR / temperature plots' },
  { name: 'WebGL2', rest: 'shader pipeline for client-side debayer + MTF stretch (with CPU fallback)' },
  { name: 'LettuceEncrypt', rest: "the relay server's built-in Let's Encrypt" },
  { name: 'Zero cloud dependencies', rest: 'catalog, FITS writing, plate-solve dispatch, sequencer engine — all in the binary' },
];

export const footerCols = [
  {
    title: 'Project',
    links: [
      { label: 'GitHub repo', href: site.repo },
      { label: 'README', href: site.readme },
      { label: 'Issues / requests', href: site.issues },
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
