// M0 - connect / discovery screen + tabbed multi-instance.
//
// Discovers Polaris hosts advertised over mDNS as `_nina._tcp` (the
// server's MdnsService announces `polaris-app.local:5000`), lets the
// operator check one or more, then loads each in its own full-screen
// <iframe> and switches between them with a top tab bar. Each instance is
// an independent, live session (kept loaded while hidden). `allowNavigation`
// in capacitor.config keeps the native bridge (and the polaris-onnx plugin)
// alive on every remote Polaris origin.
//
// IMPORTANT: this file is loaded as a plain <script type="module"> with
// NO bundler. So it must NOT `import` the Capacitor packages by bare
// specifier (`@capacitor/core`, ...) -- those don't resolve in the
// WebView and would throw. Native plugins are reached through the global
// bridge `window.Capacitor.Plugins.<Name>` (ZeroConf / Preferences /
// KeepAwake / App), which Capacitor injects regardless of bundling. In a
// plain desktop browser there is no `window.Capacitor`, so everything
// falls back gracefully and the manual address box still works.

const LAST_HOST_KEY = 'polaris.lastHost';
const LAST_SET_KEY = 'polaris.lastSet';
const MDNS_TYPE = '_nina._tcp.';

const els = {
  hostList: document.getElementById('hostList'),
  scanHint: document.getElementById('scanHint'),
  rescanBtn: document.getElementById('rescanBtn'),
  hostInput: document.getElementById('hostInput'),
  connectBtn: document.getElementById('connectBtn'),
  openSelectedBtn: document.getElementById('openSelectedBtn'),
  reopenRow: document.getElementById('reopenRow'),
  reopenLink: document.getElementById('reopenLink'),
  lastRow: document.getElementById('lastHostRow'),
  lastLink: document.getElementById('lastHostLink'),
  tabBar: document.getElementById('tabBar'),
  frames: document.getElementById('frames'),
  pickerDoneBtn: document.getElementById('pickerDoneBtn'),
};

// Capacitor bridge + plugin handles. Resolved from the global the native
// layer injects; all undefined in a plain browser.
const Cap = (typeof window !== 'undefined') ? window.Capacitor : undefined;
const Plugins = (Cap && Cap.Plugins) ? Cap.Plugins : {};
const ZeroConf = Plugins.ZeroConf;
const Preferences = Plugins.Preferences;
const KeepAwake = Plugins.KeepAwake;
const AppPlugin = Plugins.App;

// @capacitor/geolocation. Resolve from the bridge (Plugins) or via
// registerPlugin so it works whether or not Capacitor pre-populated
// Plugins. Undefined in a plain browser → the iframe falls back to the
// WebView's own navigator.geolocation.
const Geolocation = Plugins.Geolocation
  || ((Cap && typeof Cap.registerPlugin === 'function')
        ? Cap.registerPlugin('Geolocation') : undefined);

// ---------- geolocation bridge (parent side) ----------
// The Polaris UI runs in a cross-origin <iframe>, where navigator.geolocation
// is unreliable inside the Android WebView. So the iframe asks US (the app
// origin, where Capacitor plugins live) for the device location. We ack
// immediately (so the child knows a native host is present and doesn't wait
// the full timeout when it isn't), then resolve with the native fix. This
// triggers the Android runtime location-permission prompt on first use.
window.addEventListener('message', async (ev) => {
  const d = ev.data;
  if (!d || d.__polarisGeoReq !== true) return;
  const id = d.id;
  const post = (msg) => { try { ev.source.postMessage(msg, '*'); } catch { /* ignore */ } };
  // Let the child know native geolocation is available here.
  post({ __polarisGeoAck: true, id });
  const reply = (m) => post(Object.assign({ __polarisGeoRes: true, id }, m));
  try {
    if (!Geolocation) { reply({ ok: false, error: 'Geolocation plugin unavailable' }); return; }
    // Ask for permission (shows the OS prompt the first time). Ignore the
    // result and let getCurrentPosition surface the real error if denied.
    try { await Geolocation.requestPermissions({ permissions: ['location'] }); } catch { /* ignore */ }
    const pos = await Geolocation.getCurrentPosition(
      { enableHighAccuracy: false, timeout: 20000, maximumAge: 60000 });
    reply({
      ok: true,
      lat: pos.coords.latitude,
      lon: pos.coords.longitude,
      alt: (pos.coords.altitude != null) ? pos.coords.altitude : 0,
    });
  } catch (e) {
    reply({ ok: false, error: (e && e.message) || String(e) });
  }
});

async function prefGet(key) {
  try {
    if (Preferences) return (await Preferences.get({ key })).value;
    return localStorage.getItem(key);
  } catch { return null; }
}
async function prefSet(key, value) {
  try {
    if (Preferences) await Preferences.set({ key, value });
    else localStorage.setItem(key, value);
  } catch { /* ignore */ }
}

const getLastHost = () => prefGet(LAST_HOST_KEY);
const setLastHost = (url) => prefSet(LAST_HOST_KEY, url);
async function getLastSet() {
  const raw = await prefGet(LAST_SET_KEY);
  if (!raw) return [];
  try { const a = JSON.parse(raw); return Array.isArray(a) ? a : []; } catch { return []; }
}
const setLastSet = (origins) => prefSet(LAST_SET_KEY, JSON.stringify(origins));

// Normalise a user/discovered host into a full origin URL. Default to
// https (the Pi serves HTTPS on 5000); accept a full URL as-is.
function toOrigin(host, port) {
  let h = (host || '').trim();
  if (!h) return null;
  if (/^https?:\/\//i.test(h)) return h.replace(/\/+$/, '');
  if (port && !h.includes(':')) h = `${h}:${port}`;
  return `https://${h}`.replace(/\/+$/, '');
}

// Short human label for a manually-typed origin (falls back to the host).
function hostLabel(origin) {
  try { return new URL(origin).host; } catch { return origin; }
}

const discovered = new Map();        // origin -> { name, addr }
const selected = new Set();          // origins ticked on the picker
const instances = new Map();         // origin -> { origin, name, frame }
let activeOrigin = null;

// ---------- discovery ----------

function renderList() {
  els.hostList.innerHTML = '';
  for (const [origin, info] of discovered) {
    const li = document.createElement('li');
    li.className = selected.has(origin) ? 'selected' : '';
    const open = instances.has(origin);
    li.innerHTML =
      `<input type="checkbox" class="host-check"${selected.has(origin) ? ' checked' : ''}>` +
      `<span class="h-main"><span class="h-name">${info.name}</span>` +
      (open ? '<span class="h-open">● open</span>' : '') +
      `<br><span class="h-addr">${info.addr}</span></span>`;
    li.addEventListener('click', () => toggleSelected(origin));
    els.hostList.appendChild(li);
  }
  updateOpenButton();
}

function toggleSelected(origin) {
  if (selected.has(origin)) selected.delete(origin); else selected.add(origin);
  renderList();
}

function updateOpenButton() {
  const n = selected.size;
  els.openSelectedBtn.hidden = n === 0;
  els.openSelectedBtn.textContent = n > 1 ? `Open ${n} instances` : 'Open';
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
      const friendly = (s.txtRecord && s.txtRecord.friendly) || s.name || 'Polaris';
      discovered.set(origin, { name: friendly, addr: `${addr}:${s.port || 5000}` });
      els.scanHint.textContent = `${discovered.size} found.`;
      renderList();
    });
    setTimeout(() => { try { ZeroConf.close(); } catch {} }, 8000);
    setTimeout(() => {
      if (discovered.size === 0)
        els.scanHint.textContent = 'Nothing found yet. Enter the address below.';
    }, 8500);
  } catch (e) {
    els.scanHint.textContent = 'Discovery failed: ' + (e && e.message ? e.message : e);
  }
}

// ---------- instances / tabs ----------

function addInstance(origin, name, { activate = false } = {}) {
  if (!origin) return;
  if (!instances.has(origin)) {
    const frame = document.createElement('iframe');
    frame.className = 'instance-frame';
    frame.setAttribute('allow',
      'fullscreen; accelerometer; gyroscope; magnetometer; ' +
      'geolocation; camera; microphone; clipboard-read; clipboard-write');
    frame.src = origin;
    els.frames.appendChild(frame);
    instances.set(origin, { origin, name: name || hostLabel(origin), frame });
    // Keep the screen on for the imaging session once the first instance opens.
    if (instances.size === 1) { try { if (KeepAwake) KeepAwake.keepAwake(); } catch {} }
    persistSet();
  }
  renderTabs();
  if (activate || !activeOrigin) activateTab(origin);
}

function activateTab(origin) {
  const inst = instances.get(origin);
  if (!inst) return;
  activeOrigin = origin;
  for (const [, i] of instances) i.frame.classList.toggle('active', i === inst);
  document.body.classList.add('connected');
  hidePicker();
  renderTabs();
}

function closeTab(origin) {
  const inst = instances.get(origin);
  if (!inst) return;
  try { inst.frame.src = 'about:blank'; } catch {}
  inst.frame.remove();
  instances.delete(origin);
  persistSet();
  if (instances.size === 0) {
    // Nothing left: tear down to the picker as the base screen.
    activeOrigin = null;
    document.body.classList.remove('connected');
    document.body.classList.remove('picker-open');
    try { if (KeepAwake) KeepAwake.allowSleep(); } catch {}
    renderTabs();
    refreshPickerExtras();
    scan();
    return;
  }
  if (activeOrigin === origin) {
    activateTab(instances.keys().next().value);   // neighbour
  } else {
    renderTabs();
  }
}

function renderTabs() {
  const bar = els.tabBar;
  bar.innerHTML = '';
  if (instances.size === 0) { bar.hidden = true; return; }
  bar.hidden = false;
  for (const [origin, inst] of instances) {
    const tab = document.createElement('div');
    tab.className = 'tab' + (origin === activeOrigin ? ' active' : '');
    const label = document.createElement('span');
    label.className = 'tab-label';
    label.textContent = inst.name;
    label.addEventListener('click', () => activateTab(origin));
    const close = document.createElement('button');
    close.className = 'tab-close';
    close.type = 'button';
    close.textContent = '×';
    close.setAttribute('aria-label', 'Close ' + inst.name);
    close.addEventListener('click', (e) => { e.stopPropagation(); closeTab(origin); });
    tab.appendChild(label);
    tab.appendChild(close);
    bar.appendChild(tab);
  }
  const add = document.createElement('button');
  add.className = 'tab-add';
  add.type = 'button';
  add.textContent = '＋';
  add.setAttribute('aria-label', 'Add instance');
  add.addEventListener('click', showPicker);
  bar.appendChild(add);
}

// Open all checked instances at once.
function openSelected() {
  const origins = Array.from(selected);
  if (origins.length === 0) return;
  origins.forEach((o, i) => addInstance(o, discovered.get(o)?.name, { activate: i === 0 }));
  setLastHost(origins[origins.length - 1]);
  selected.clear();
  hidePicker();
  maybeWarnMemory();
}

function maybeWarnMemory() {
  if (instances.size >= 4) {
    els.scanHint.textContent =
      `${instances.size} live instances — this can use a lot of memory; weak phones may reload background tabs.`;
  }
}

function persistSet() { setLastSet(Array.from(instances.keys())); }

// ---------- picker overlay (non-destructive) ----------

function showPicker() {
  document.body.classList.add('picker-open');
  els.pickerDoneBtn.hidden = instances.size === 0;   // only offer "back" when tabs exist
  refreshPickerExtras();
  try { if (ZeroConf) scan(); } catch {}
}

function hidePicker() {
  document.body.classList.remove('picker-open');
}

// Refresh the "Last used" + "Reopen last (N)" rows on the picker.
async function refreshPickerExtras() {
  const last = await getLastHost();
  if (last && !instances.has(last)) {
    els.lastLink.textContent = last;
    els.lastLink.onclick = (e) => { e.preventDefault(); addInstance(last, null, { activate: true }); };
    els.lastRow.hidden = false;
  } else {
    els.lastRow.hidden = true;
  }
  const set = (await getLastSet()).filter(o => !instances.has(o));
  if (set.length > 0) {
    els.reopenLink.textContent = `Reopen last ${set.length > 1 ? set.length + ' instances' : 'instance'}`;
    els.reopenLink.onclick = (e) => {
      e.preventDefault();
      set.forEach((o, i) => addInstance(o, null, { activate: i === 0 }));
      maybeWarnMemory();
    };
    els.reopenRow.hidden = false;
  } else {
    els.reopenRow.hidden = true;
  }
}

// ---------- hardware back ----------

function wireHardwareBack() {
  if (!AppPlugin || typeof AppPlugin.addListener !== 'function') return;
  AppPlugin.addListener('backButton', () => {
    if (document.body.classList.contains('picker-open') && instances.size > 0) {
      hidePicker();                      // back to the active tab
    } else if (instances.size > 0) {
      showPicker();                      // summon the picker over running tabs
    } else {
      try { AppPlugin.exitApp(); } catch {}
    }
  });
}

function wire() {
  els.rescanBtn.addEventListener('click', scan);
  els.openSelectedBtn.addEventListener('click', openSelected);
  els.pickerDoneBtn.addEventListener('click', () => { if (activeOrigin) activateTab(activeOrigin); });
  const manual = () => {
    const origin = toOrigin(els.hostInput.value);
    if (!origin) { els.scanHint.textContent = 'Enter an address first (e.g. 192.168.0.50:5000).'; return; }
    setLastHost(origin);
    addInstance(origin, hostLabel(origin), { activate: true });
  };
  els.connectBtn.addEventListener('click', manual);
  els.hostInput.addEventListener('keydown', (e) => { if (e.key === 'Enter') manual(); });
}

(function init() {
  // Wire the controls FIRST so Connect always works, even if anything
  // below (plugin calls, discovery) throws.
  wire();
  wireHardwareBack();
  refreshPickerExtras();
  scan();
})();
