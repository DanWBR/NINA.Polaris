// M0 - connect / discovery screen.
//
// Discovers Polaris hosts advertised over mDNS as `_nina._tcp` (the
// server's MdnsService announces `polaris-app.local:5000`), lets the
// operator pick one or type a host / Relay URL, remembers the last
// choice, then navigates the WebView to that origin. `allowNavigation`
// in capacitor.config keeps the native bridge (and the polaris-onnx
// plugin) alive on the remote Polaris UI.
//
// Runs both inside Capacitor (native plugins available) and in a plain
// browser for quick UI iteration (graceful fallbacks when plugins are
// absent).

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

// Lazy Capacitor plugin handles (undefined in a plain browser).
let Capacitor, Preferences, KeepAwake, ZeroConf;
async function loadPlugins() {
  try {
    ({ Capacitor } = await import('@capacitor/core'));
    ({ Preferences } = await import('@capacitor/preferences'));
    ({ KeepAwake } = await import('@capacitor/keep-awake'));
    ({ ZeroConf } = await import('capacitor-zeroconf'));
  } catch (e) {
    // Plain browser dev: bridge/plugins not bundled. UI still works.
    console.warn('[connect] Capacitor plugins unavailable (browser dev):', e?.message || e);
  }
}

const isNative = () => !!(Capacitor && Capacitor.isNativePlatform && Capacitor.isNativePlatform());

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
      if (result.action !== 'resolved' || !result.service) return;
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
    els.scanHint.textContent = 'Discovery failed: ' + (e?.message || e);
  }
}

async function connect(origin) {
  if (!origin) return;
  els.connectBtn.disabled = true;
  await setLastHost(origin);
  // Keep the screen on for the imaging session once we're in the app.
  try { if (KeepAwake) await KeepAwake.keepAwake(); } catch {}
  try { if (ZeroConf) await ZeroConf.close(); } catch {}
  // Navigate the same WebView to the live Polaris UI. The Capacitor
  // bridge persists via allowNavigation, so native plugins remain.
  window.location.href = origin;
}

function wire() {
  els.rescanBtn.addEventListener('click', scan);
  els.connectBtn.addEventListener('click', () => connect(toOrigin(els.hostInput.value)));
  els.hostInput.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') connect(toOrigin(els.hostInput.value));
  });
}

(async function init() {
  await loadPlugins();
  wire();
  const last = await getLastHost();
  if (last) {
    els.lastLink.textContent = last;
    els.lastLink.onclick = (e) => { e.preventDefault(); connect(last); };
    els.lastRow.hidden = false;
  }
  scan();
})();
