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
  switchBtn: document.getElementById('switchDeviceBtn'),
};

// Capacitor bridge + plugin handles. Resolved from the global the native
// layer injects; all undefined in a plain browser.
const Cap = (typeof window !== 'undefined') ? window.Capacitor : undefined;
const Plugins = (Cap && Cap.Plugins) ? Cap.Plugins : {};
const ZeroConf = Plugins.ZeroConf;
const Preferences = Plugins.Preferences;
const KeepAwake = Plugins.KeepAwake;
const AppPlugin = Plugins.App;

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
      // Prefer the human-set label the server advertises in its TXT
      // record ("Telescope on the balcony"); fall back to the mDNS name.
      const friendly = (s.txtRecord && s.txtRecord.friendly) || s.name || 'Polaris';
      discovered.set(origin, { name: friendly, addr: `${addr}:${s.port || 5000}` });
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

  // Load the Polaris UI in a FULL-SCREEN IFRAME instead of navigating.
  // Navigating away would drop the Capacitor plugin bridge (plugins only
  // work on the app origin), which is exactly what broke native GraXpert.
  // By keeping this page (app origin) as the parent and loading the Pi UI
  // as a child, the parent keeps the native plugins; the injected
  // onnx-native-shim in the iframe RPCs inference back here via
  // postMessage. The Pi UI itself is unchanged.
  let frame = document.getElementById('polarisFrame');
  if (!frame) {
    frame = document.createElement('iframe');
    frame.id = 'polarisFrame';
    frame.setAttribute('allow',
      'fullscreen; accelerometer; gyroscope; magnetometer; ' +
      'camera; microphone; clipboard-read; clipboard-write');
    document.body.appendChild(frame);
  }
  frame.src = origin;
  document.body.classList.add('connected');
  if (els.switchBtn) els.switchBtn.hidden = false;
}

// Leave the current instance and return to the picker so the operator can
// jump to another rig/device. Tears down the iframe (drops the Polaris
// session/WebSocket), re-runs discovery, and re-shows the last-used row.
function disconnect() {
  const frame = document.getElementById('polarisFrame');
  if (frame) frame.src = 'about:blank';
  document.body.classList.remove('connected');
  if (els.switchBtn) els.switchBtn.hidden = true;
  els.connectBtn.disabled = false;
  // Battery: the session is over, let the screen sleep again until the
  // operator picks the next device (connect() re-acquires the lock).
  try { if (KeepAwake) KeepAwake.allowSleep(); } catch {}
  getLastHost().then((last) => {
    if (last) {
      els.lastLink.textContent = last;
      els.lastLink.onclick = (e) => { e.preventDefault(); connect(last); };
      els.lastRow.hidden = false;
    }
  });
  scan();
}

// The "Devices" floating button: a tap returns to the picker; a drag
// repositions it so it never permanently blocks part of the Polaris UI.
function wireSwitchButton() {
  const btn = els.switchBtn;
  if (!btn) return;
  let active = false, moved = false, sx = 0, sy = 0, ox = 0, oy = 0;
  btn.addEventListener('pointerdown', (e) => {
    active = true; moved = false;
    sx = e.clientX; sy = e.clientY;
    const r = btn.getBoundingClientRect();
    ox = r.left; oy = r.top;
    try { btn.setPointerCapture(e.pointerId); } catch {}
  });
  btn.addEventListener('pointermove', (e) => {
    if (!active) return;
    const dx = e.clientX - sx, dy = e.clientY - sy;
    if (!moved && Math.hypot(dx, dy) > 6) { moved = true; btn.classList.add('dragging'); }
    if (!moved) return;
    const w = btn.offsetWidth, h = btn.offsetHeight;
    const nx = Math.max(4, Math.min(window.innerWidth - w - 4, ox + dx));
    const ny = Math.max(4, Math.min(window.innerHeight - h - 4, oy + dy));
    btn.style.left = nx + 'px';
    btn.style.top = ny + 'px';
    btn.style.right = 'auto';
    btn.style.bottom = 'auto';
  });
  const end = (e) => {
    if (!active) return;
    active = false;
    btn.classList.remove('dragging');
    try { btn.releasePointerCapture(e.pointerId); } catch {}
    if (!moved) disconnect();          // a clean tap = leave the instance
  };
  btn.addEventListener('pointerup', end);
  btn.addEventListener('pointercancel', end);
}

// Android hardware back button: while an instance is loaded, back returns
// to the picker (matches the floating button) instead of killing the app;
// on the picker itself it exits. Without a listener Capacitor would just
// navigate the WebView history / close the app, jumping straight out of a
// live session.
function wireHardwareBack() {
  if (!AppPlugin || typeof AppPlugin.addListener !== 'function') return;
  AppPlugin.addListener('backButton', () => {
    if (document.body.classList.contains('connected')) {
      disconnect();
    } else {
      try { AppPlugin.exitApp(); } catch {}
    }
  });
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
  wireSwitchButton();
  wireHardwareBack();

  getLastHost().then((last) => {
    if (last) {
      els.lastLink.textContent = last;
      els.lastLink.onclick = (e) => { e.preventDefault(); connect(last); };
      els.lastRow.hidden = false;
    }
  });

  scan();
})();
