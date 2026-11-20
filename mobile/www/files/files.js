// Offline file viewer for the mobile app.
//
// The rest of the app is a shell around the Polaris UI that a host serves, so
// with every host off there is nothing to show. This page is local to the app:
// it opens FITS (and JPG/PNG) files and renders them on the device. The FITS
// decode, debayer and auto-stretch run in the NINA.Polaris.Wasm module, with the
// same NINA.Image.Portable code the host's FILES preview uses, and the header
// panel groups come from the same FitsHeaderGroups. OpenSeadragon gives the
// pinch, pan and zoom, with the toolbar of the FILES viewer.
//
// A file is an entry { key, name, size, getBytes(onProgress), cancel?, fits? }.
// Files picked on the device, files "opened with" Polaris from another app and
// files on a network share (network.js) all come in this shape. `fits` is set
// when the content says FITS although the name doesn't.
//
// Plain module, no bundler, like connect.js.

const MAX_DIM = 2400;   // the FILES viewer asks the host for 2400 px too
const FILTER_KEY = 'polaris.viewFilter';
const FITS_EXT = /\.(fits?|fts)$/i;
const SHOWABLE_EXT = /\.(fits?|fts|jpe?g|png|webp)$/i;

const $ = (id) => document.getElementById(id);
const els = {
  pickBtn: $('pickBtn'), pickInput: $('pickInput'), pickSkipped: $('pickSkipped'),
  listSection: $('listSection'), listTitle: $('listTitle'), fileList: $('fileList'),
  viewer: $('viewer'), title: $('vTitle'), prev: $('vPrev'), next: $('vNext'), close: $('vClose'),
  busy: $('vBusy'), busyText: $('vBusyText'), error: $('vError'),
  zoomIn: $('vZoomIn'), zoomOut: $('vZoomOut'), home: $('vHome'), full: $('vFull'), exitFull: $('vExitFull'),
  bayer: $('vBayer'), filter: $('vFilter'), info: $('vInfo'),
  hdrBtn: $('vHdrBtn'), headers: $('vHeaders'), hdrTitle: $('vHdrTitle'), hdrBody: $('vHdrBody'),
  hdrFilter: $('vHdrFilter'), hdrClose: $('vHdrClose'),
};

let entries = [];        // the list the viewer pages through
let index = -1;          // entry open in the viewer
let osd = null;
let objectUrl = null;    // blob URL of the image OSD shows
let fitsInfo = null;     // FitsOpen result for the open FITS
let openKey = null;      // entry key whose FITS sits in the WASM heap
let renderSeq = 0;       // drops a render that a newer one has overtaken
let inflight = null;     // entry whose bytes are being fetched, so that it can be cancelled

const isFitsEntry = (e) => e.fits === true || FITS_EXT.test(e.name);

// Fetch an entry's bytes with a progress line in the viewer.
async function bytesOf(e, seq) {
  inflight = e;
  try {
    return await e.getBytes((received, total) => {
      if (seq !== renderSeq) return;
      setBusy(total > 0 ? `Downloading ${fmtSize(received)} of ${fmtSize(total)}…` : `Downloading ${fmtSize(received)}…`);
    });
  } finally {
    if (inflight === e) inflight = null;
  }
}

function cancelInflight() {
  if (inflight && typeof inflight.cancel === 'function') { try { inflight.cancel(); } catch { /* already done */ } }
  inflight = null;
}

// ---------- storage (per-device convenience only) ----------
function prefGet(k) { try { return localStorage.getItem(k); } catch { return null; } }
function prefSet(k, v) { try { localStorage.setItem(k, v); } catch { /* private mode */ } }

// ---------- WASM ----------
let wasmPromise = null;
function wasm() {
  if (!wasmPromise) {
    wasmPromise = import('./vendor/wasm/main.js')
      .then(() => {
        const w = globalThis.NINA && globalThis.NINA.Polaris && globalThis.NINA.Polaris.Wasm;
        if (!w || !w.Interop || typeof w.Interop.FitsOpen !== 'function')
          throw new Error('This build has an older FITS renderer. Update the app.');
        return w.Interop;
      })
      .catch((e) => {
        wasmPromise = null;
        throw new Error(e && e.message && e.message.includes('older') ? e.message
          : 'The FITS renderer is missing from this build of the app.');
      });
  }
  return wasmPromise;
}

// ---------- helpers ----------
function fmtSize(n) {
  if (!(n >= 0)) return '';
  if (n < 1024) return n + ' B';
  if (n < 1024 * 1024) return (n / 1024).toFixed(0) + ' KB';
  if (n < 1024 * 1024 * 1024) return (n / 1024 / 1024).toFixed(1) + ' MB';
  return (n / 1024 / 1024 / 1024).toFixed(2) + ' GB';
}

function setBusy(text) {
  els.busy.hidden = !text;
  if (text) els.busyText.textContent = text;
}

function showError(msg) {
  els.error.hidden = !msg;
  els.error.textContent = msg || '';
}

// Let the busy label paint before a long synchronous WASM call blocks the thread.
const nextPaint = () => new Promise((r) => requestAnimationFrame(() => setTimeout(r, 0)));

// ---------- list ----------
function renderList() {
  els.fileList.innerHTML = '';
  entries.forEach((e, i) => {
    const li = document.createElement('li');
    const name = document.createElement('span');
    name.className = 'f-name';
    name.textContent = e.name;
    const size = document.createElement('span');
    size.className = 'f-size';
    size.textContent = fmtSize(e.size);
    li.appendChild(name);
    li.appendChild(size);
    li.addEventListener('click', () => openViewer(i));
    els.fileList.appendChild(li);
  });
  els.listSection.hidden = entries.length === 0 || !listed;
}

// opts.list = false keeps the "Opened" list hidden: network.js has its own
// folder view and Recent folders for files on a server.
let listed = true;
function setEntries(list, title, opts) {
  entries = list;
  listed = !(opts && opts.list === false);
  els.listTitle.textContent = title;
  renderList();
}

// ---------- viewer ----------
function ensureOsd() {
  if (osd) return osd;
  /* global OpenSeadragon */
  osd = OpenSeadragon({
    id: 'osd',
    showNavigationControl: false,
    showNavigator: true,
    navigatorPosition: 'BOTTOM_RIGHT',
    navigatorHeight: '70px',
    navigatorWidth: '90px',
    visibilityRatio: 0.5,
    minZoomImageRatio: 0.8,
    maxZoomPixelRatio: 4.0,
    // Same rule as the FILES viewer: nearest by default, so that the view
    // shows real pixels and never invents structure that is not in the frame.
    imageSmoothingEnabled: els.filter.value !== 'nearest',
    gestureSettingsMouse: { clickToZoom: false },
    gestureSettingsTouch: { clickToZoom: false, pinchToZoom: true, flickEnabled: true },
    animationTime: 0.4,
    springStiffness: 8,
  });
  osd.addHandler('open-failed', (ev) => showError('Could not show the image: ' + ((ev && ev.message) || 'unknown error')));
  osd.addHandler('full-page', (ev) => { els.exitFull.hidden = !ev.fullPage; });
  return osd;
}

function showUrl(url) {
  if (objectUrl && objectUrl !== url) URL.revokeObjectURL(objectUrl);
  objectUrl = url;
  ensureOsd().open({ type: 'image', url });
}

async function rgbaToUrl(rgba, w, h) {
  const canvas = document.createElement('canvas');
  canvas.width = w;
  canvas.height = h;
  const ctx = canvas.getContext('2d');
  ctx.putImageData(new ImageData(new Uint8ClampedArray(rgba.buffer, rgba.byteOffset, rgba.length), w, h), 0, 0);
  // JPEG at the quality the host uses for its preview: the pixels are already
  // stretched to 8 bits, and a PNG of a 4000 px frame takes seconds to encode.
  const blob = await new Promise((res) => canvas.toBlob(res, 'image/jpeg', 0.9));
  if (!blob) throw new Error('The browser could not encode the rendered image.');
  return URL.createObjectURL(blob);
}

function describe(e) {
  const parts = [fmtSize(e.size)];
  if (fitsInfo) {
    parts.unshift(`${fitsInfo.width} × ${fitsInfo.height}`);
    parts.push(fitsInfo.channels >= 3 ? 'RGB' : (fitsInfo.bayer && fitsInfo.bayer !== 'None' ? 'Bayer ' + fitsInfo.bayer : 'mono'));
    parts.push(fitsInfo.bitDepth + '-bit');
  }
  return parts.filter(Boolean).join(' · ');
}

async function renderCurrent() {
  const e = entries[index];
  if (!e) return;
  const seq = ++renderSeq;
  showError('');
  els.title.textContent = e.name;
  els.prev.disabled = index <= 0;
  els.next.disabled = index >= entries.length - 1;
  const isFits = isFitsEntry(e);
  els.bayer.hidden = !isFits;
  els.hdrBtn.hidden = !isFits;
  if (!isFits) { els.headers.hidden = true; fitsInfo = null; }

  try {
    if (!isFits) {
      setBusy('Loading…');
      const buf = await bytesOf(e, seq);
      if (seq !== renderSeq) return;
      showUrl(URL.createObjectURL(new Blob([buf])));
      els.info.textContent = describe(e);
      return;
    }

    const W = await wasm();
    if (openKey !== e.key) {
      setBusy('Reading…');
      const buf = await bytesOf(e, seq);
      if (seq !== renderSeq) return;
      setBusy('Decoding…');
      await nextPaint();
      const info = JSON.parse(W.FitsOpen(new Uint8Array(buf)));
      if (seq !== renderSeq) return;
      if (info.error) { openKey = null; fitsInfo = null; throw new Error('Not a FITS this viewer can read: ' + info.error); }
      fitsInfo = info;
      openKey = e.key;
      renderHeaders();
      // Only a mono frame can be debayered; a colour cube ignores the override.
      els.bayer.disabled = info.channels >= 3;
    }
    setBusy('Stretching…');
    await nextPaint();
    const rgba = W.FitsRender(MAX_DIM, els.bayer.value || '');
    const [w, h] = W.FitsGetRenderDims();
    if (seq !== renderSeq) return;
    if (!w || !h) throw new Error('The renderer returned an empty image.');
    const url = await rgbaToUrl(rgba, w, h);
    if (seq !== renderSeq) { URL.revokeObjectURL(url); return; }
    showUrl(url);
    els.info.textContent = describe(e);
  } catch (err) {
    if (seq === renderSeq && !(err && err.cancelled)) showError((err && err.message) || String(err));
  } finally {
    if (seq === renderSeq) setBusy('');
  }
}

function openViewer(i) {
  index = i;
  els.viewer.hidden = false;
  document.body.classList.add('viewing');
  els.headers.hidden = prefGet('polaris.fitsHeadersVisible') !== '1';
  renderCurrent();
}

async function closeViewer() {
  renderSeq++;
  cancelInflight();
  els.viewer.hidden = true;
  document.body.classList.remove('viewing');
  setBusy('');
  if (osd) { try { osd.setFullPage(false); osd.close(); } catch { /* already closed */ } }
  if (objectUrl) { URL.revokeObjectURL(objectUrl); objectUrl = null; }
  // Give the WASM heap back: a 100 MB frame must not stay resident.
  if (openKey) {
    openKey = null;
    fitsInfo = null;
    try { (await wasm()).FitsRelease(); } catch { /* renderer never loaded */ }
  }
}

function step(d) {
  const j = index + d;
  if (j < 0 || j >= entries.length) return;
  cancelInflight();
  index = j;
  renderCurrent();
}

// ---------- header panel ----------
function renderHeaders() {
  els.hdrBody.innerHTML = '';
  if (!fitsInfo) return;
  els.hdrTitle.textContent = `FITS header (${fitsInfo.totalCards})`;
  const q = els.hdrFilter.value.trim().toUpperCase();
  for (const g of fitsInfo.groups || []) {
    const cards = (g.cards || []).filter((c) => !q
      || c.keyword.toUpperCase().includes(q)
      || String(c.value).toUpperCase().includes(q)
      || String(c.comment || '').toUpperCase().includes(q));
    if (cards.length === 0) continue;
    const h = document.createElement('div');
    h.className = 'v-hdr-group';
    h.textContent = g.name;
    els.hdrBody.appendChild(h);
    for (const c of cards) {
      const row = document.createElement('div');
      row.className = 'v-hdr-card';
      const k = document.createElement('span');
      k.className = 'k';
      k.textContent = c.keyword;
      const v = document.createElement('span');
      v.className = 'v';
      v.textContent = String(c.value).replace(/^'(.*)'$/, '$1').trim();
      row.appendChild(k);
      row.appendChild(v);
      if (c.comment) {
        const m = document.createElement('span');
        m.className = 'c';
        m.textContent = c.comment;
        row.appendChild(m);
      }
      els.hdrBody.appendChild(row);
    }
  }
}

function toggleHeaders(show) {
  els.headers.hidden = !show;
  prefSet('polaris.fitsHeadersVisible', show ? '1' : '0');
}

// ---------- wiring ----------
function wire() {
  els.pickBtn.addEventListener('click', () => els.pickInput.click());
  els.pickInput.addEventListener('change', () => {
    const picked = Array.from(els.pickInput.files || []);
    els.pickInput.value = '';
    if (picked.length === 0) return;
    const files = picked.filter((f) => SHOWABLE_EXT.test(f.name));
    const skipped = picked.length - files.length;
    els.pickSkipped.hidden = skipped === 0;
    els.pickSkipped.textContent = skipped === 0 ? ''
      : skipped === 1 ? '1 file was skipped because it is not a FITS, JPG, PNG or WebP file.'
      : skipped + ' files were skipped because they are not FITS, JPG, PNG or WebP files.';
    if (files.length === 0) return;
    files.sort((a, b) => a.name.localeCompare(b.name, undefined, { numeric: true }));
    setEntries(files.map((f, i) => ({
      key: 'local:' + i + ':' + f.name + ':' + f.size + ':' + f.lastModified,
      name: f.name,
      size: f.size,
      getBytes: () => f.arrayBuffer(),
    })), 'Opened on this device');
    openViewer(0);
  });

  els.close.addEventListener('click', closeViewer);
  els.prev.addEventListener('click', () => step(-1));
  els.next.addEventListener('click', () => step(1));
  els.zoomIn.addEventListener('click', () => { if (osd) { osd.viewport.zoomBy(1.5); osd.viewport.applyConstraints(); } });
  els.zoomOut.addEventListener('click', () => { if (osd) { osd.viewport.zoomBy(1 / 1.5); osd.viewport.applyConstraints(); } });
  els.home.addEventListener('click', () => { if (osd) osd.viewport.goHome(); });
  els.full.addEventListener('click', () => { if (osd) osd.setFullPage(!osd.isFullPage()); });
  els.exitFull.addEventListener('click', () => { if (osd) osd.setFullPage(false); });
  els.bayer.addEventListener('change', renderCurrent);
  els.filter.value = prefGet(FILTER_KEY) === 'smooth' ? 'smooth' : 'nearest';
  els.filter.addEventListener('change', () => {
    prefSet(FILTER_KEY, els.filter.value);
    if (osd && osd.drawer && typeof osd.drawer.setImageSmoothingEnabled === 'function')
      osd.drawer.setImageSmoothingEnabled(els.filter.value !== 'nearest');
  });
  els.hdrBtn.addEventListener('click', () => toggleHeaders(els.headers.hidden));
  els.hdrClose.addEventListener('click', () => toggleHeaders(false));
  els.hdrFilter.addEventListener('input', renderHeaders);

  document.addEventListener('keydown', (e) => {
    if (els.viewer.hidden) return;
    if (e.target && e.target.tagName === 'INPUT') return;
    if (e.key === 'ArrowLeft') step(-1);
    else if (e.key === 'ArrowRight') step(1);
    else if (e.key === 'Escape') {
      if (osd && osd.isFullPage()) osd.setFullPage(false); else closeViewer();
    }
  });

  // Android back button: leave full page, then the viewer, then the page.
  const App = window.Capacitor && window.Capacitor.Plugins && window.Capacitor.Plugins.App;
  if (App && typeof App.addListener === 'function') {
    App.addListener('backButton', () => {
      if (!els.viewer.hidden) {
        if (osd && osd.isFullPage()) osd.setFullPage(false); else closeViewer();
      } else if (!(api.onBack && api.onBack())) {
        location.href = '../index.html';
      }
    });
    // "Open with Polaris" while this page is already showing.
    App.addListener('appUrlOpen', (ev) => { if (ev && ev.url) openUri(ev.url); });
  }
}

// ---------- "Open with Polaris" ----------
const Cap = window.Capacitor;
const Smb = Cap && typeof Cap.isPluginAvailable === 'function' && Cap.isPluginAvailable('PolarisSmb')
  ? (Cap.Plugins.PolarisSmb || Cap.registerPlugin('PolarisSmb')) : null;

const looksLikeFits = (buf) => {
  const b = new Uint8Array(buf, 0, Math.min(9, buf.byteLength));
  return String.fromCharCode(...b) === 'SIMPLE  =';
};

// Open a content:// or file:// URI another app handed over. Capacitor serves
// both to the page through convertFileSrc, so the bytes never cross the bridge.
async function openUri(uri) {
  if (!/^(content|file):/i.test(uri)) return;
  // connect.js checks this so that coming back to it doesn't open the file again.
  try { sessionStorage.setItem('polaris.openedLaunchUrl', uri); } catch { /* private mode */ }
  let name = '', size = -1;
  if (Smb) { try { ({ name, size } = await Smb.describeUri({ uri })); } catch { /* keep the fallback */ } }
  if (!name) name = decodeURIComponent(uri.split('/').pop() || 'file');
  const url = window.Capacitor.convertFileSrc(uri);
  let cached = null;
  const e = {
    key: 'uri:' + uri,
    name,
    size,
    getBytes: async () => {
      if (cached) { const b = cached; cached = null; return b; }
      const r = await fetch(url);
      if (!r.ok) throw new Error('Could not read the file (' + r.status + ').');
      return r.arrayBuffer();
    },
  };
  if (!SHOWABLE_EXT.test(name)) {
    // Providers often drop the extension; look at the content instead.
    try { cached = await e.getBytes(); } catch (err) { cached = null; }
    if (!cached || !looksLikeFits(cached)) {
      els.pickSkipped.hidden = false;
      els.pickSkipped.textContent = `Polaris can't show “${name}” because it is not a FITS, JPG, PNG or WebP file.`;
      return;
    }
    e.fits = true;
  }
  if (!els.viewer.hidden) await closeViewer();
  setEntries([e], 'Opened from another app');
  openViewer(0);
}

wire();

const api = { setEntries, renderList, openViewer, closeViewer, wasm, fmtSize, SHOWABLE_EXT, FITS_EXT, onBack: null, openUri };
window.PolarisFiles = api;

// connect.js sends an "Open with" file here as #open=<uri>.
{
  const m = /[#&]open=([^&]+)/.exec(location.hash);
  if (m) {
    history.replaceState(null, '', location.pathname + location.search);
    openUri(decodeURIComponent(m[1]));
  }
}
