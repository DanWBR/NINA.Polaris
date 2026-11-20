// Network servers for the Files page: browse SMB shares and open the FITS,
// JPG and PNG files on them in the viewer (files.js), with no Polaris host on.
//
// The SMB work happens in the native polaris-smb plugin (smbj on Android):
// discovery by NetBIOS, the share list, folder listings and downloads to the
// app's cache, which this page then reads through Capacitor.convertFileSrc.
// Passwords go to the plugin, which keeps them encrypted when "Remember" is on;
// with it off they live in this page's memory until it closes.
//
// Saved here (Preferences, or localStorage in a plain browser):
//   servers: [{ id, host, name, label, user, at }]   id is what credentials are filed under
//   recent:  [{ id, share, path, at }]               folders a file was opened from

const STORE_KEY = 'polaris.smb';
const RECENT_MAX = 6;

const $ = (id) => document.getElementById(id);
const els = {
  device: $('deviceSection'), list: $('listSection'),
  recentSection: $('netRecentSection'), recent: $('netRecent'),
  net: $('netSection'), servers: $('netServers'), found: $('netFound'), hint: $('netHint'),
  find: $('netFindBtn'), add: $('netAddBtn'),
  browse: $('browseSection'), up: $('brUp'), crumbs: $('brCrumbs'), refresh: $('brRefresh'),
  status: $('brStatus'), brList: $('brList'),
  dlg: $('credDlg'), form: $('credForm'), dlgTitle: $('credTitle'), dlgMsg: $('credMsg'),
  hostRow: $('credHostRow'), host: $('credHost'), user: $('credUser'), pass: $('credPass'),
  domain: $('credDomain'), remember: $('credRemember'), cancel: $('credCancel'),
};

const Files = window.PolarisFiles;
const Cap = window.Capacitor;
const Prefs = Cap && Cap.Plugins && Cap.Plugins.Preferences;

let Smb = null;
let store = { servers: [], recent: [] };
const memCreds = {};          // id -> { username, password, domain } when not remembered
let browse = null;            // { server, share, path } while a server is open
let listSeq = 0;
let progressHandlers = {};    // download id -> (received, total) => void
let dlCounter = 0;
const defaultHint = els.hint.textContent.trim().replace(/\s+/g, ' ');

// ---------- storage ----------
async function load() {
  try {
    const raw = Prefs ? (await Prefs.get({ key: STORE_KEY })).value : localStorage.getItem(STORE_KEY);
    const v = raw ? JSON.parse(raw) : null;
    if (v && Array.isArray(v.servers)) store = { servers: v.servers, recent: Array.isArray(v.recent) ? v.recent : [] };
  } catch { /* first run, or storage blocked */ }
}

function persist() {
  const raw = JSON.stringify(store);
  try { if (Prefs) Prefs.set({ key: STORE_KEY, value: raw }); else localStorage.setItem(STORE_KEY, raw); } catch { /* best effort */ }
}

const serverById = (id) => store.servers.find((s) => s.id === id);

// ---------- helpers ----------
function el(tag, cls, text) {
  const n = document.createElement(tag);
  if (cls) n.className = cls;
  if (text != null) n.textContent = text;
  return n;
}

function timeAgo(t) {
  if (!t) return '';
  const s = Math.max(0, (Date.now() - t) / 1000);
  if (s < 60) return 'just now';
  if (s < 3600) return Math.round(s / 60) + ' min ago';
  if (s < 86400) return Math.round(s / 3600) + ' h ago';
  return Math.round(s / 86400) + ' d ago';
}

function fmtDate(ms) {
  if (!ms) return '';
  try { return new Date(ms).toLocaleString(undefined, { dateStyle: 'short', timeStyle: 'short' }); } catch { return ''; }
}

const natural = (a, b) => a.localeCompare(b, undefined, { numeric: true, sensitivity: 'base' });
const joinPath = (...parts) => parts.filter(Boolean).join('/');
const label = (s) => s.label || s.name || s.host;

function args(s, extra) {
  return Object.assign({ server: s.id, host: s.host, name: s.name || '' }, memCreds[s.id] || {}, extra || {});
}

// The plugin's address for the server worked, so remember it, unless the user
// typed a name, which keeps working wherever DHCP moves the server.
function noteAddress(s, address, name) {
  let changed = false;
  if (name && !s.name) { s.name = name; changed = true; }
  const typedName = !/^\d{1,3}(\.\d{1,3}){3}$/.test(s.host);
  if (address && !typedName && s.host !== address) { s.host = address; changed = true; }
  if (changed) persist();
}

function errorText(err, s, share) {
  const who = s ? label(s) : 'The server';
  switch (err && err.code) {
    case 'AUTH': return `${who} didn't accept the user name and password.`;
    case 'UNREACHABLE': return `Polaris can't reach ${who}${s && s.host !== label(s) ? ' at ' + s.host : ''}. Check that this device is on the same network as the server.`;
    case 'NO_SHARE': return `${who} doesn't have a shared folder called “${share}”.`;
    case 'NOT_FOUND': return 'This folder isn\'t on the server anymore.';
    default: return (err && err.message) || String(err);
  }
}

// ---------- credentials dialog ----------
let dlgResolve = null;

// Ask for sign-in details. With askHost, the dialog also asks for the server.
// Resolves with { host?, username, password, domain, remember } or null.
function askCredentials({ title, message, askHost, host, user }) {
  els.dlgTitle.textContent = title;
  els.dlgMsg.hidden = !message;
  els.dlgMsg.textContent = message || '';
  els.hostRow.hidden = !askHost;
  els.host.value = host || '';
  els.user.value = user || '';
  els.pass.value = '';
  els.domain.value = '';
  els.remember.checked = true;
  els.dlg.hidden = false;
  setTimeout(() => (askHost && !host ? els.host : els.user).focus(), 50);
  return new Promise((res) => { dlgResolve = res; });
}

function closeDialog(value) {
  els.dlg.hidden = true;
  els.pass.value = '';
  const r = dlgResolve;
  dlgResolve = null;
  if (r) r(value);
}

// ---------- server and recent lists ----------
function renderServers() {
  els.servers.innerHTML = '';
  const sorted = [...store.servers].sort((a, b) => (b.at || 0) - (a.at || 0));
  for (const s of sorted) {
    const li = el('li', 'srv');
    li.appendChild(el('span', 'f-ico', '🖧'));
    const text = el('span', 'f-name');
    text.appendChild(el('span', null, label(s)));
    const sub = [s.host !== label(s) ? s.host : '', s.user ? s.user : 'guest'].filter(Boolean).join(' · ');
    text.appendChild(el('span', 'f-sub', sub));
    li.appendChild(text);
    const key = el('button', 'icon-btn', '🔑');
    key.type = 'button';
    key.title = 'Change the user name and password';
    key.addEventListener('click', (ev) => { ev.stopPropagation(); signIn(s, null); });
    const del = el('button', 'icon-btn', '✕');
    del.type = 'button';
    del.title = 'Remove this server';
    del.addEventListener('click', (ev) => { ev.stopPropagation(); removeServer(s); });
    li.appendChild(key);
    li.appendChild(del);
    li.addEventListener('click', () => openServer(s));
    els.servers.appendChild(li);
  }
}

function renderRecent() {
  els.recent.innerHTML = '';
  const items = store.recent.filter((r) => serverById(r.id));
  els.recentSection.hidden = items.length === 0 || !Smb;
  for (const r of items) {
    const s = serverById(r.id);
    const li = el('li');
    li.appendChild(el('span', 'f-ico', '📁'));
    const text = el('span', 'f-name');
    text.appendChild(el('span', null, [label(s), r.share, ...r.path.split('/').filter(Boolean)].join(' › ')));
    text.appendChild(el('span', 'f-sub', timeAgo(r.at)));
    li.appendChild(text);
    li.addEventListener('click', () => openServer(s, r.share, r.path));
    els.recent.appendChild(li);
  }
}

function noteRecentFolder(s, share, path) {
  store.recent = store.recent.filter((r) => !(r.id === s.id && r.share === share && r.path === path));
  store.recent.unshift({ id: s.id, share, path, at: Date.now() });
  store.recent.length = Math.min(store.recent.length, RECENT_MAX);
  s.at = Date.now();
  persist();
  renderRecent();
}

async function removeServer(s) {
  if (!window.confirm(`Remove ${label(s)}? Polaris also forgets its password.`)) return;
  store.servers = store.servers.filter((x) => x !== s);
  store.recent = store.recent.filter((r) => r.id !== s.id);
  delete memCreds[s.id];
  persist();
  try { await Smb.forgetCredentials({ server: s.id }); } catch { /* nothing saved */ }
  renderServers();
  renderRecent();
}

// Store new sign-in details for a server and open it. Returns false on cancel.
async function signIn(s, message, user) {
  const c = await askCredentials({ title: `Sign in to ${label(s)}`, message, user: user != null ? user : (s.user || '') });
  if (!c) return false;
  if (c.remember) delete memCreds[s.id];
  else memCreds[s.id] = { username: c.username, password: c.password, domain: c.domain };
  try {
    const r = await Smb.listShares(args(s, c.remember ? { username: c.username, password: c.password, domain: c.domain, remember: true } : {}));
    s.user = c.username;
    noteAddress(s, r.address, r.name);
    persist();
    renderServers();
    showShares(s, r.shares);
    return true;
  } catch (err) {
    if (err && err.code === 'AUTH') return signIn(s, errorText(err, s), c.username);
    showBrowse(s, null, '');
    setStatus(errorText(err, s));
    return true;
  }
}

// ---------- discovery ----------
async function find() {
  els.find.disabled = true;
  els.hint.textContent = 'Looking for servers on this network…';
  els.found.innerHTML = '';
  try {
    const { servers, onNetwork } = await Smb.discover({ timeoutMs: 1500 });
    const fresh = [];
    for (const f of servers) {
      const saved = store.servers.find((s) => s.name && s.name.toUpperCase() === f.name.toUpperCase());
      if (saved) noteAddress(saved, f.address);
      else fresh.push(f);
    }
    renderServers();
    for (const f of fresh) {
      const li = el('li');
      li.appendChild(el('span', 'f-ico', '＋'));
      const text = el('span', 'f-name');
      text.appendChild(el('span', null, f.name));
      text.appendChild(el('span', 'f-sub', f.address));
      li.appendChild(text);
      li.addEventListener('click', () => {
        const s = { id: f.name.toUpperCase(), host: f.address, name: f.name, label: f.name, user: '', at: Date.now() };
        if (!serverById(s.id)) store.servers.push(s);
        persist();
        els.found.innerHTML = '';
        els.hint.textContent = defaultHint;
        renderServers();
        openServer(serverById(s.id));
      });
      els.found.appendChild(li);
    }
    els.hint.textContent = !onNetwork ? 'This device isn\'t on a Wi-Fi or wired network.'
      : servers.length === 0 ? 'Polaris didn\'t find a server that answers by name. Use “Add server…” and type its address.'
      : fresh.length === 0 ? 'Every server Polaris found is already in the list.'
      : 'Tap a server to open it.';
  } catch (err) {
    els.hint.textContent = errorText(err);
  } finally {
    els.find.disabled = false;
  }
}

async function addServer() {
  const c = await askCredentials({ title: 'Add a network server', askHost: true });
  if (!c || !c.host) return;
  const host = c.host.replace(/^smb:\/\//i, '').replace(/^\\\\/, '').split(/[\\/]/)[0];
  const id = host.toLowerCase();
  let s = serverById(id);
  if (!s) {
    s = { id, host, name: '', label: host, user: c.username, at: Date.now() };
    store.servers.push(s);
  }
  if (!c.remember) memCreds[id] = { username: c.username, password: c.password, domain: c.domain };
  showBrowse(s, null, '');
  setStatus('Connecting…');
  try {
    const r = await Smb.listShares(args(s, c.remember ? { username: c.username, password: c.password, domain: c.domain, remember: true } : {}));
    s.user = c.username;
    noteAddress(s, r.address, r.name);
    persist();
    renderServers();
    showShares(s, r.shares);
  } catch (err) {
    persist();
    renderServers();
    if (err && err.code === 'AUTH') { await signIn(s, errorText(err, s), c.username); return; }
    setStatus(errorText(err, s));
  }
}

// ---------- browsing ----------
function setStatus(text) {
  els.status.hidden = !text;
  els.status.textContent = text || '';
}

function showBrowse(s, share, path) {
  browse = { server: s, share, path };
  els.device.hidden = true;
  els.list.hidden = true;
  els.net.hidden = true;
  els.recentSection.hidden = true;
  els.browse.hidden = false;
  els.brList.innerHTML = '';
  renderCrumbs();
  window.scrollTo(0, 0);
}

function leaveBrowse() {
  browse = null;
  listSeq++;
  els.browse.hidden = true;
  els.device.hidden = false;
  els.net.hidden = false;
  Files.renderList();
  renderRecent();
}

function renderCrumbs() {
  els.crumbs.innerHTML = '';
  if (!browse) return;
  const { server: s, share, path } = browse;
  const parts = [{ text: label(s), go: () => openServer(s) }];
  if (share) parts.push({ text: share, go: () => openServer(s, share, '') });
  const segs = (path || '').split('/').filter(Boolean);
  segs.forEach((seg, i) => parts.push({ text: seg, go: () => openServer(s, share, segs.slice(0, i + 1).join('/')) }));
  parts.forEach((p, i) => {
    if (i) els.crumbs.appendChild(el('span', 'crumb-sep', '›'));
    const b = el('button', 'crumb' + (i === parts.length - 1 ? ' here' : ''), p.text);
    b.type = 'button';
    b.addEventListener('click', p.go);
    els.crumbs.appendChild(b);
  });
  els.crumbs.scrollLeft = els.crumbs.scrollWidth;
}

function showShares(s, shares) {
  if (!browse || browse.server !== s) showBrowse(s, null, '');
  browse.share = null;
  browse.path = '';
  renderCrumbs();
  setStatus(shares.length ? '' : `${label(s)} doesn't share any folders with this user.`);
  els.brList.innerHTML = '';
  for (const sh of [...shares].sort((a, b) => natural(a.name, b.name))) {
    const li = el('li');
    li.appendChild(el('span', 'f-ico', '🗄️'));
    const text = el('span', 'f-name');
    text.appendChild(el('span', null, sh.name));
    if (sh.comment) text.appendChild(el('span', 'f-sub', sh.comment));
    li.appendChild(text);
    li.addEventListener('click', () => openServer(s, sh.name, ''));
    els.brList.appendChild(li);
  }
}

async function openServer(s, share, path) {
  const seq = ++listSeq;
  showBrowse(s, share || null, path || '');
  setStatus('Connecting…');
  try {
    if (!share) {
      const r = await Smb.listShares(args(s));
      if (seq !== listSeq) return;
      noteAddress(s, r.address, r.name);
      showShares(s, r.shares);
      return;
    }
    const r = await Smb.list(args(s, { share, path: path || '' }));
    if (seq !== listSeq) return;
    noteAddress(s, r.address);
    showFolder(s, share, path || '', r.entries);
  } catch (err) {
    if (seq !== listSeq) return;
    if (err && err.code === 'AUTH') {
      setStatus('');
      const ok = await signIn(s, `${label(s)} asked for a user name and password.`);
      if (ok && share) openServer(s, share, path);
      else if (!ok) leaveBrowse();
      return;
    }
    setStatus(errorText(err, s, share));
  }
}

function showFolder(s, share, path, entries) {
  els.brList.innerHTML = '';
  const dirs = entries.filter((e) => e.dir).sort((a, b) => natural(a.name, b.name));
  const files = entries.filter((e) => !e.dir).sort((a, b) => natural(a.name, b.name));
  const showable = files.filter((f) => Files.SHOWABLE_EXT.test(f.name));
  setStatus(entries.length === 0 ? 'This folder is empty.' : '');

  for (const d of dirs) {
    const li = el('li');
    li.appendChild(el('span', 'f-ico', '📁'));
    li.appendChild(el('span', 'f-name', d.name));
    li.addEventListener('click', () => openServer(s, share, joinPath(path, d.name)));
    els.brList.appendChild(li);
  }

  const viewerEntries = showable.map((f) => remoteEntry(s, share, path, f));
  for (const f of files) {
    const i = showable.indexOf(f);
    const li = el('li', i < 0 ? 'dim' : '');
    li.appendChild(el('span', 'f-ico', Files.FITS_EXT.test(f.name) ? '✦' : i < 0 ? '·' : '🖼️'));
    const text = el('span', 'f-name');
    text.appendChild(el('span', null, f.name));
    text.appendChild(el('span', 'f-sub', fmtDate(f.modified)));
    li.appendChild(text);
    li.appendChild(el('span', 'f-size', Files.fmtSize(f.size)));
    if (i >= 0) {
      li.addEventListener('click', () => {
        noteRecentFolder(s, share, path);
        Files.setEntries(viewerEntries, `${label(s)} › ${[share, ...path.split('/').filter(Boolean)].join(' › ')}`, { list: false });
        Files.openViewer(i);
      });
    }
    els.brList.appendChild(li);
  }
}

// A viewer entry for a file on the share. The bytes are downloaded to the app's
// cache by the plugin and read back through convertFileSrc.
function remoteEntry(s, share, path, f) {
  let currentId = null;
  return {
    key: `smb:${s.id}/${share}/${joinPath(path, f.name)}:${f.size}:${f.modified}`,
    name: f.name,
    size: f.size,
    cancel: () => { if (currentId) Smb.cancelDownload({ id: currentId }); },
    getBytes: async (onProgress) => {
      const id = 'dl' + (++dlCounter);
      currentId = id;
      if (onProgress) progressHandlers[id] = onProgress;
      let r;
      try {
        r = await Smb.download(args(s, { share, path: joinPath(path, f.name), id }));
      } catch (err) {
        if (err && err.code === 'CANCELLED') throw Object.assign(new Error('cancelled'), { cancelled: true });
        throw new Error(errorText(err, s, share));
      } finally {
        delete progressHandlers[id];
        if (currentId === id) currentId = null;
      }
      try {
        const resp = await fetch(window.Capacitor.convertFileSrc(r.path));
        if (!resp.ok) throw new Error('Could not read the downloaded file (' + resp.status + ').');
        return await resp.arrayBuffer();
      } finally {
        Smb.releaseFile({ path: r.path }).catch(() => {});
      }
    },
  };
}

function up() {
  if (!browse) return false;
  const { server: s, share, path } = browse;
  if (path) openServer(s, share, path.split('/').slice(0, -1).join('/'));
  else if (share) openServer(s);
  else leaveBrowse();
  return true;
}

// ---------- wiring ----------
function wire() {
  els.find.addEventListener('click', find);
  els.add.addEventListener('click', addServer);
  els.up.addEventListener('click', up);
  els.refresh.addEventListener('click', () => { if (browse) openServer(browse.server, browse.share, browse.path); });
  els.cancel.addEventListener('click', () => closeDialog(null));
  els.dlg.addEventListener('click', (ev) => { if (ev.target === els.dlg) closeDialog(null); });
  els.form.addEventListener('submit', (ev) => {
    ev.preventDefault();
    if (!els.hostRow.hidden && !els.host.value.trim()) { els.host.focus(); return; }
    closeDialog({
      host: els.host.value.trim(),
      username: els.user.value.trim(),
      password: els.pass.value,
      domain: els.domain.value.trim(),
      remember: els.remember.checked,
    });
  });
  // Android back: the dialog, then one folder up, then files.js leaves the page.
  Files.onBack = () => {
    if (!els.dlg.hidden) { closeDialog(null); return true; }
    return up();
  };
}

// Start with a plugin (the native one, or a stand-in for the page's own tests).
async function init(plugin) {
  Smb = plugin;
  await load();
  if (!Smb) {
    els.find.hidden = true;
    els.add.hidden = true;
    els.hint.textContent = 'Network servers work in the Polaris app for Android, iPhone and iPad.';
  } else {
    els.find.hidden = false;
    els.add.hidden = false;
    if (Smb.addListener && !init.listening) {
      init.listening = true;
      Smb.addListener('downloadProgress', (ev) => {
        const h = ev && progressHandlers[ev.id];
        if (h) h(ev.received, ev.total);
      });
    }
  }
  renderServers();
  renderRecent();
}

wire();
init(Cap && typeof Cap.isPluginAvailable === 'function' && Cap.isPluginAvailable('PolarisSmb')
  ? (Cap.Plugins.PolarisSmb || Cap.registerPlugin('PolarisSmb')) : null);

window.PolarisNet = { init, progress: (id, received, total) => { const h = progressHandlers[id]; if (h) h(received, total); } };
