// onnx-worker.js — runs the GraXpert ONNX pipelines (BGE / Denoise / Decon)
// off the main thread, in a Worker the parent TERMINATES after a short idle.
//
// Why: ONNX inference (esp. the WASM backend used when there's no WebGPU —
// e.g. the Android WebView in the APK) grows the WASM linear heap to fit the
// work, and that heap NEVER shrinks for the life of the realm. So a tab that
// ran one BGE stays pinned at ~1 GB forever. Running it in a Worker means the
// parent can `worker.terminate()` once idle, which frees the whole heap back
// to the OS — the tab drops back to its ~50 MB baseline.
//
// Design choice: this worker runs the EXISTING, UNMODIFIED onnx-pipelines.js.
// A Worker has no `window`/`document`/`sessionStorage`, and that file loads
// ORT via a <script> tag + reads the auth token from storage. We provide tiny
// shims BEFORE importing it:
//   - window  -> self                (so its `window.ort` / `window.Onnx*` work)
//   - document.head.appendChild(s)   -> importScripts(s.src) (its ORT loader)
//   - sessionStorage/localStorage    -> return the per-job token from the parent
// The parent only routes here for the WASM backend, so we never touch WebGPU.

'use strict';

let _token = null;

// --- DOM / storage shims so the unmodified onnx-pipelines.js runs here ------
self.window = self;
self.document = {
    head: {
        appendChild(el) {
            // onnx-pipelines.js' loadOrtWeb() builds a <script src=...>, sets
            // onload/onerror, then appends it. Translate that to importScripts
            // (synchronous in a worker) and fire the callbacks so its loader
            // resolves / falls back to the wasm-only bundle exactly as on the page.
            try { importScripts(el.src); if (el.onload) el.onload(); }
            catch (e) { if (el.onerror) el.onerror(e); }
        }
    },
    createElement() {
        let _src = '';
        return { set src(v) { _src = v; }, get src() { return _src; }, onload: null, onerror: null };
    }
};
const _storageShim = {
    getItem: (k) => (k === 'polaris_token' ? _token : null),
    setItem() { }, removeItem() { }, clear() { }
};
self.sessionStorage = _storageShim;
self.localStorage = _storageShim;

let _pipelinesLoaded = false;
function ensurePipelines() {
    if (_pipelinesLoaded) return;
    importScripts('/js/onnx-pipelines.js?v=20260701-detaillog3');   // sets self.OnnxRegistry
    _pipelinesLoaded = true;
}

self.onmessage = async (ev) => {
    const m = ev.data || {};
    const id = m.id;
    _token = m.token || null;
    try {
        ensurePipelines();
        const reg = self.OnnxRegistry;
        if (!reg) throw new Error('OnnxRegistry failed to load in worker');
        const P = ({ bge: reg.BgePipeline, denoise: reg.DenoisePipeline, decon: reg.DeconPipeline })[m.kind];
        if (!P) throw new Error('worker cannot run pipeline: ' + m.kind);

        const Ctor = self[m.pixelsCtor] || Uint16Array;
        const pixels = new Ctor(m.pixels);

        const opts = Object.assign({}, m.opts, {
            // Force WASM: the parent only routes here when WebGPU is absent,
            // and we never want a WebGPU context inside the worker.
            useGpu: false,
            onProgress: (phase, frac) => {
                try { self.postMessage({ id, type: 'progress', phase, frac }); } catch { /* ignore */ }
            }
        });

        const result = await new P().run(pixels, m.width, m.height, opts);

        // Transfer the big output buffers back (zero-copy) where present.
        const transfer = [];
        if (result && result.pixels && result.pixels.buffer) transfer.push(result.pixels.buffer);
        if (result && result.background && result.background.buffer) transfer.push(result.background.buffer);
        self.postMessage({ id, type: 'done', result }, transfer);
    } catch (e) {
        self.postMessage({ id, type: 'error', message: (e && e.message) || String(e) });
    }
};
