// M0 - connect / discovery screen.
//
// Discovers Polaris hosts advertised over mDNS as `_nina._tcp` (the
// server's MdnsService announces `polaris-app.local:5000`), lets the
// operator pick one or type a host / Relay URL, remembers the last
// choice, then navigates the WebView to that origin. `allowNavigation`
// in capacitor.config keeps the native bridge (and the polaris-onnx
// plugin) alive on the remote Polaris UI.
//
// IMPORTANT: this file is loaded as a plain <script type="module"> with
// NO bundler. So it must NOT `import` the Capacitor packages by bare
// specifier (`@capacitor/core`, ...) -- those don't resolve in the
// WebView and would throw. Native plugins are reached through the global
// bridge `window.Capacitor.Plugins.<Name>` (ZeroConf / Preferences /
// KeepAwake), which Capacitor injects regardless of bundling. In a plain
// desktop browser there is no `window.Capacitor`, so everything falls
// back gracefully and the manual address box still works.

const LAST_HOST_KEY = 'polaris.lastHost';
const MDNS_TYPE = '_nina._tcp.';

const els = {
  hostList: document.getElementById('hostList'),
  scanHint: document.getElementById('scanHint'),
  rescanBtn: document.getElementById('rescanBtn'),
  hostInput: document.getElementById('hostInput'),
  connectBtn: document.getElementById('connectBtn'),
  lastRow: document.getElementById('lastHostRow'),
  lastLink: document.getElementById('lastHostLink'),
};

// Capacitor bridge + plugin handles. Resolved from the global the native
// layer injects; all undefined in a plain browser.
const Cap = (typeof window !== 'undefined') ? window.Capacitor : undefined;
const Plugins = (Cap && Cap.Plugins) ? Cap.Plugins : {};
const ZeroConf = Plugins.ZeroConf;
const Preferences = Plugins.Preferences;
const KeepAwake = Plugins.KeepAwake;

const isNative = () => !!(Cap && typeof Cap.isNativePlatform === 'function' && Cap.isNativePlatform());

async function getLastHost() {
  try {
    if (Preferences) return (await Preferences.get({ key: LAST_HOST_KEY })).value;
    return localStorage.getItem(LAST_HOST_KEY);
  } catch { return null; }
}
async function setLastHost(url) {
  try {
    if (Preferences) await Preferences.set({ key: LAST_HOST_KEY, value: url });
    else localStorage.setItem(LAST_HOST_KEY, url);
  } catch { /* ignore */ }
}

// Normalise a user/discovered host into a full origin URL. Default to
// https (the Pi serves HTTPS on 5000); accept a full URL as-is.
function toOrigin(host, port) {
  let h = (host || '').trim();
  if (!h) return null;
  if (/^https?:\/\//i.test(h)) return h.replace(/\/+$/, '');
  if (port && !h.includes(':')) h = `${h}:${port}`;
  return `https://${h}`.replace(/\/+$/, '');
}

const discovered = new Map(); // origin -> { name, addr }

function renderList() {
  els.hostList.innerHTML = '';
  if (discovered.size === 0) return;
  for (const [origin, info] of discovered) {
    const li = document.createElement('li');
    li.innerHTML =
      `<span><span class="h-name">${info.name}</span><br>` +
      `<span class="h-addr">${info.addr}</span></span><span class="go">→</span>`;
    li.addEventListener('click', () => connect(origin));
    els.hostList.appendChild(li);
  }
}

async function scan() {
  discovered.clear();
  renderList();
  if (!ZeroConf) {
    els.scanHint.textContent =
      'Automatic discovery needs the installed app. Enter the address below.';
    return;
  }
  els.scanHint.innerHTML = '<span class="spinner"></span>Searching the local network…';
  try {
    await ZeroConf.watch({ type: MDNS_TYPE, domain: 'local.' }, (result) => {
      if (!result || result.action !== 'resolved' || !result.service) return;
      const s = result.service;
      const addr = (s.ipv4Addresses && s.ipv4Addresses[0]) || s.hostname;
      if (!addr) return;
      const origin = toOrigin(addr, s.port || 5000);
      discovered.set(origin, { name: s.name || 'Polaris', addr: `${addr}:${s.port || 5000}` });
      els.scanHint.textContent = `${discovered.size} found.`;
      renderList();
    });
    // Stop watching after 8s to save battery; results stay listed.
    setTimeout(() => { try { ZeroConf.close(); } catch {} }, 8000);
    setTimeout(() => {
      if (discovered.size === 0)
        els.scanHint.textContent = 'Nothing found yet. Enter the address below.';
    }, 8500);
  } catch (e) {
    els.scanHint.textContent = 'Discovery failed: ' + (e && e.message ? e.message : e);
  }
}

async function connect(origin) {
  if (!origin) {
    els.scanHint.textContent = 'Enter an address first (e.g. 192.168.0.50:5000).';
    return;
  }
  els.connectBtn.disabled = true;
  await setLastHost(origin);
  // Keep the screen on for the imaging session once we're in the app.
  try { if (KeepAwake) await KeepAwake.keepAwake(); } catch {}
  try { if (ZeroConf) await ZeroConf.close(); } catch {}

  // If we're still on this page a few seconds after navigating, surface
  // the most likely cause. Phones can't resolve mDNS `.local` names in
  // the WebView (ERR_NAME_NOT_RESOLVED), so if the user typed one, point
  // them at the IP; otherwise it's usually the self-signed cert.
  const here = location.href;
  let isDotLocal = false;
  try { isDotLocal = /\.local(?::\d+)?$/i.test(new URL(origin).host); } catch {}
  setTimeout(() => {
    if (location.href === here) {
      els.connectBtn.disabled = false;
      els.scanHint.innerHTML = isDotLocal
        ? `Couldn't open <code>${origin}</code>. Phones can't look up ` +
          `<code>.local</code> names -- use the Pi's IP address instead ` +
          `(e.g. <code>192.168.0.50:5000</code>), or use automatic ` +
          `discovery above (it fills in the IP for you).`
        : `Couldn't open <code>${origin}</code>. If the Pi uses HTTPS with ` +
          `a self-signed certificate the browser may block it -- try ` +
          `<code>http://</code> instead, or install the Pi's certificate.`;
    }
  }, 4000);

  // Navigate the same WebView to the live Polaris UI. The Capacitor
  // bridge persists via allowNavigation, so native plugins remain.
  window.location.assign(origin);
}

function wire() {
  els.rescanBtn.addEventListener('click', scan);
  els.connectBtn.addEventListener('click', () => connect(toOrigin(els.hostInput.value)));
  els.hostInput.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') connect(toOrigin(els.hostInput.value));
  });
}

(function init() {
  // Wire the controls FIRST so Connect always works, even if anything
  // below (plugin calls, discovery) throws.
  wire();

  getLastHost().then((last) => {
    if (last) {
      els.lastLink.textContent = last;
      els.lastLink.onclick = (e) => { e.preventDefault(); connect(last); };
      els.lastRow.hidden = false;
    }
  });

  scan();
})();
