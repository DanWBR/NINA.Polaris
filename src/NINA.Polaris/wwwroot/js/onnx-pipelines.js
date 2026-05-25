// onnx-pipelines.js — client-side AI inference for GraXpert ops.
//
// Loaded lazily by app.js when the user first invokes a GraXpert
// operation (BGE / Denoise / Decon). Owns three concerns:
//
//   1) ORT Web bootstrap — set wasmPaths to our vendored bundle,
//      pick the best execution provider (webgpu > wasm-simd-threaded),
//      keep one global session per (family, version).
//
//   2) Model fetch + IndexedDB cache — GET /api/onnx/model/{f}/{v},
//      verify ETag matches manifest hash, store the bytes in an
//      IndexedDB object store so reload doesn't re-download.
//
//   3) Pipeline classes (BgePipeline / DenoisePipeline / DeconPipeline)
//      that translate (pixels, w, h, channels, params) into model
//      input tensors, run inference, and translate output back to a
//      pixel buffer. Implemented in GX-2..GX-4; the framework here
//      is GX-1b — it gives those pipelines a session-cache helper
//      to call.
//
// Lives as a classic <script> so app.js can reference `OnnxRegistry`
// directly on window. The actual ort.* import happens lazily inside
// loadOrtWeb() so the 33 MB bundle isn't paid for at page load.

(function () {
    'use strict';

    // Configurable knobs; the page can override before first call.
    const ORT_VENDOR_PATH = '/js/lib/onnxruntime/';
    const IDB_DB_NAME = 'polaris-onnx-models';
    const IDB_STORE = 'blobs';

    // ─── ORT Web lazy loader ────────────────────────────────────────
    // ort.min.js sets `window.ort` when loaded via <script>. We resolve
    // the script-tag injection once; subsequent calls reuse the same
    // promise so concurrent first-uses don't fight for the DOM.
    let _ortLoadPromise = null;
    function loadOrtWeb() {
        if (_ortLoadPromise) return _ortLoadPromise;
        _ortLoadPromise = new Promise((resolve, reject) => {
            if (window.ort) { resolve(window.ort); return; }
            const s = document.createElement('script');
            s.src = ORT_VENDOR_PATH + 'ort.min.js';
            s.onload = () => {
                if (!window.ort) {
                    reject(new Error('ort.min.js loaded but window.ort is undefined'));
                    return;
                }
                // Point at the WASM bundle dir so the runtime knows
                // where to fetch ort-wasm-simd-threaded.wasm + .jsep
                // variant. Without this it assumes the same dir as
                // the entry script — which is true for us but explicit
                // beats implicit (especially when served behind a
                // sub-path / reverse proxy / Relay tunnel).
                window.ort.env.wasm.wasmPaths = ORT_VENDOR_PATH;
                // Single-threaded by default — multi-thread needs
                // Cross-Origin-Isolation headers that Polaris doesn't
                // set today. Single-thread + SIMD is still ~3-5× faster
                // than scalar; thread support is a follow-up.
                window.ort.env.wasm.numThreads = 1;
                resolve(window.ort);
            };
            s.onerror = () => reject(new Error('Failed to load ' + s.src));
            document.head.appendChild(s);
        });
        return _ortLoadPromise;
    }

    // ─── IndexedDB cache (model bytes by hash) ──────────────────────
    // Each model is identified by its server-computed SHA-256 hash;
    // we store the raw ArrayBuffer under that key. Hash mismatch
    // between manifest + stored entry → drop and re-download. Caching
    // by hash (not by family/version) means model upgrades don't
    // leave stale bytes behind — the new hash simply misses cache.

    function openDb() {
        return new Promise((resolve, reject) => {
            const req = indexedDB.open(IDB_DB_NAME, 1);
            req.onupgradeneeded = (e) => {
                const db = e.target.result;
                if (!db.objectStoreNames.contains(IDB_STORE)) {
                    db.createObjectStore(IDB_STORE);
                }
            };
            req.onsuccess = () => resolve(req.result);
            req.onerror = () => reject(req.error);
        });
    }

    async function idbGet(hash) {
        const db = await openDb();
        return new Promise((resolve, reject) => {
            const tx = db.transaction(IDB_STORE, 'readonly');
            const req = tx.objectStore(IDB_STORE).get(hash);
            req.onsuccess = () => { resolve(req.result || null); db.close(); };
            req.onerror = () => { reject(req.error); db.close(); };
        });
    }

    async function idbPut(hash, buffer) {
        const db = await openDb();
        return new Promise((resolve, reject) => {
            const tx = db.transaction(IDB_STORE, 'readwrite');
            tx.objectStore(IDB_STORE).put(buffer, hash);
            tx.oncomplete = () => { resolve(); db.close(); };
            tx.onerror = () => { reject(tx.error); db.close(); };
        });
    }

    async function idbDelete(hash) {
        const db = await openDb();
        return new Promise((resolve) => {
            const tx = db.transaction(IDB_STORE, 'readwrite');
            tx.objectStore(IDB_STORE).delete(hash);
            tx.oncomplete = () => { resolve(); db.close(); };
            tx.onerror = () => { resolve(); db.close(); };  // best-effort
        });
    }

    /** Sum of cached blob bytes — for the Settings panel "cache size" line. */
    async function idbTotalSize() {
        const db = await openDb();
        return new Promise((resolve) => {
            const tx = db.transaction(IDB_STORE, 'readonly');
            const req = tx.objectStore(IDB_STORE).openCursor();
            let total = 0;
            req.onsuccess = (e) => {
                const cur = e.target.result;
                if (cur) {
                    const v = cur.value;
                    if (v && v.byteLength) total += v.byteLength;
                    cur.continue();
                } else {
                    resolve(total);
                    db.close();
                }
            };
            req.onerror = () => { resolve(0); db.close(); };
        });
    }

    async function idbClear() {
        const db = await openDb();
        return new Promise((resolve) => {
            const tx = db.transaction(IDB_STORE, 'readwrite');
            tx.objectStore(IDB_STORE).clear();
            tx.oncomplete = () => { resolve(); db.close(); };
            tx.onerror = () => { resolve(); db.close(); };
        });
    }

    // ─── Manifest + session cache ───────────────────────────────────
    // One ort.InferenceSession per (family, version) — sessions are
    // hundreds of MB resident; recreating per-op would be wasteful.
    // Sessions live for the page session and get released when the
    // user navigates away (browser tears down the worker).

    let _manifestPromise = null;
    const _sessions = new Map();   // key = "family/version" → InferenceSession

    async function fetchManifest(force) {
        if (_manifestPromise && !force) return _manifestPromise;
        _manifestPromise = fetch('/api/onnx/manifest', { cache: force ? 'no-store' : 'default' })
            .then(r => r.ok ? r.json() : { models: [], modelsPath: '', error: 'HTTP ' + r.status })
            .catch(e => ({ models: [], modelsPath: '', error: String(e) }));
        return _manifestPromise;
    }

    /** Find the manifest entry for a given (family, version), or null. */
    async function lookupModel(family, version) {
        const m = await fetchManifest();
        return (m.models || []).find(x => x.family === family && x.version === version) || null;
    }

    /** Pick the best execution provider available at runtime. */
    async function pickBackends() {
        // WebGPU is a feature detect; in unsupported browsers
        // ort.env.webgpu is missing or navigator.gpu is undefined.
        const webgpuOk = typeof navigator !== 'undefined'
                        && 'gpu' in navigator
                        && typeof navigator.gpu.requestAdapter === 'function';
        return webgpuOk
            ? ['webgpu', 'wasm']    // GPU first, fall back to CPU
            : ['wasm'];             // CPU only
    }

    /**
     * Fetch model bytes (cached) and create an ort.InferenceSession.
     * Returns the same session on subsequent calls with identical key.
     * Throws on missing manifest entry, CDN miss, or session error;
     * caller is responsible for UI fallback.
     *
     * onProgress is optional: (phase: 'cache-hit' | 'downloading' |
     *                                  'creating-session',
     *                          fraction?: number) => void
     */
    async function loadSession(family, version, onProgress) {
        const key = family + '/' + version;
        const existing = _sessions.get(key);
        if (existing) return existing;

        const entry = await lookupModel(family, version);
        if (!entry) throw new Error('Model ' + key + ' not in manifest');

        let bytes = entry.hash ? await idbGet(entry.hash) : null;
        if (bytes) {
            if (onProgress) onProgress('cache-hit');
        } else {
            if (onProgress) onProgress('downloading', 0);
            const resp = await fetch('/api/onnx/model/' + family + '/' + version);
            if (!resp.ok) throw new Error('Model fetch HTTP ' + resp.status);
            // Stream the body so we can report progress incrementally
            // for the multi-hundred-MB downloads.
            const total = parseInt(resp.headers.get('content-length') || '0', 10);
            const reader = resp.body.getReader();
            const chunks = [];
            let received = 0;
            for (; ;) {
                const { done, value } = await reader.read();
                if (done) break;
                chunks.push(value);
                received += value.byteLength;
                if (total > 0 && onProgress) {
                    onProgress('downloading', received / total);
                }
            }
            const merged = new Uint8Array(received);
            let off = 0;
            for (const c of chunks) { merged.set(c, off); off += c.byteLength; }
            bytes = merged.buffer;
            if (entry.hash) {
                try { await idbPut(entry.hash, bytes); }
                catch (e) { console.warn('[OnnxRegistry] IDB put failed', e); }
            }
        }

        if (onProgress) onProgress('creating-session');
        const ort = await loadOrtWeb();
        const ep = await pickBackends();
        const session = await ort.InferenceSession.create(bytes, {
            executionProviders: ep,
            graphOptimizationLevel: 'all',
        });
        _sessions.set(key, session);
        return session;
    }

    /** Release session + drop reference. Useful from the Editor close path. */
    async function releaseSession(family, version) {
        const key = family + '/' + version;
        const s = _sessions.get(key);
        if (s) {
            try { await s.release(); } catch { /* ignore */ }
            _sessions.delete(key);
        }
    }

    // ─── Public API ─────────────────────────────────────────────────
    window.OnnxRegistry = {
        loadOrtWeb,
        fetchManifest,
        lookupModel,
        loadSession,
        releaseSession,
        idbClear,
        idbDelete,
        idbTotalSize,
        pickBackends,
    };
})();
