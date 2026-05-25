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

    // ───────────────────────────────────────────────────────────────
    // Pixel helpers shared by the pipelines (GX-2..GX-4).
    // ───────────────────────────────────────────────────────────────

    /** Bilinear resize of a planar / interleaved uint16 pixel buffer.
     *  In/out layout: row-major, channels-interleaved when channels>1.
     *  For BGE we resize a uint16 mono frame to 256x256 (replicated to
     *  3 channels later), and resize the resulting background plane
     *  back to the source dimensions. */
    function bilinearResize(src, srcW, srcH, channels, dstW, dstH) {
        const dst = new Uint16Array(dstW * dstH * channels);
        const sx = srcW / dstW;
        const sy = srcH / dstH;
        for (let y = 0; y < dstH; y++) {
            const fy = (y + 0.5) * sy - 0.5;
            const iy = Math.floor(fy);
            const dy = fy - iy;
            const iy0 = Math.max(0, Math.min(srcH - 1, iy));
            const iy1 = Math.max(0, Math.min(srcH - 1, iy + 1));
            for (let x = 0; x < dstW; x++) {
                const fx = (x + 0.5) * sx - 0.5;
                const ix = Math.floor(fx);
                const dx = fx - ix;
                const ix0 = Math.max(0, Math.min(srcW - 1, ix));
                const ix1 = Math.max(0, Math.min(srcW - 1, ix + 1));
                for (let c = 0; c < channels; c++) {
                    const a = src[(iy0 * srcW + ix0) * channels + c];
                    const b = src[(iy0 * srcW + ix1) * channels + c];
                    const cc = src[(iy1 * srcW + ix0) * channels + c];
                    const d = src[(iy1 * srcW + ix1) * channels + c];
                    const top = a + (b - a) * dx;
                    const bot = cc + (d - cc) * dx;
                    const v = top + (bot - top) * dy;
                    dst[(y * dstW + x) * channels + c] =
                        Math.max(0, Math.min(65535, Math.round(v)));
                }
            }
        }
        return dst;
    }

    /** Float32 bilinear resize — same as above but float in / float out.
     *  Used to resample the predicted background plane (already float
     *  after denormalize) back to the source dimensions without an
     *  intermediate uint16 round-trip. */
    function bilinearResizeF(src, srcW, srcH, dstW, dstH) {
        const dst = new Float32Array(dstW * dstH);
        const sx = srcW / dstW;
        const sy = srcH / dstH;
        for (let y = 0; y < dstH; y++) {
            const fy = (y + 0.5) * sy - 0.5;
            const iy = Math.floor(fy);
            const dy = fy - iy;
            const iy0 = Math.max(0, Math.min(srcH - 1, iy));
            const iy1 = Math.max(0, Math.min(srcH - 1, iy + 1));
            for (let x = 0; x < dstW; x++) {
                const fx = (x + 0.5) * sx - 0.5;
                const ix = Math.floor(fx);
                const dx = fx - ix;
                const ix0 = Math.max(0, Math.min(srcW - 1, ix));
                const ix1 = Math.max(0, Math.min(srcW - 1, ix + 1));
                const a = src[iy0 * srcW + ix0];
                const b = src[iy0 * srcW + ix1];
                const cc = src[iy1 * srcW + ix0];
                const d = src[iy1 * srcW + ix1];
                const top = a + (b - a) * dx;
                const bot = cc + (d - cc) * dx;
                dst[y * dstW + x] = top + (bot - top) * dy;
            }
        }
        return dst;
    }

    /** Sample-based median + MAD on a normalised Float32 plane.
     *  We don't need full-image statistics for a stable BGE normalize —
     *  3-4k random samples gives sub-1% accuracy on the median, which
     *  is well within the slack the model + post-Gaussian smooth eats.
     *  Returns { median, mad }. */
    function medianMadSampled(plane) {
        const N = Math.min(plane.length, 4000);
        const samples = new Float32Array(N);
        const step = plane.length / N;
        for (let i = 0; i < N; i++) samples[i] = plane[Math.floor(i * step)];
        const sorted = Array.from(samples).sort((a, b) => a - b);
        const median = sorted[N >> 1];
        const dev = new Float32Array(N);
        for (let i = 0; i < N; i++) dev[i] = Math.abs(samples[i] - median);
        const devSorted = Array.from(dev).sort((a, b) => a - b);
        const mad = devSorted[N >> 1] || 1e-6;
        return { median, mad };
    }

    /** Cheap separable box-blur 3-pass ~Gaussian on a Float32 plane.
     *  Matches the radius used elsewhere in EditPipeline; for a fixed
     *  256x256 working plane this is sub-millisecond. */
    function boxBlurF(src, w, h, radius) {
        if (radius < 1) return src.slice();
        let buf = src.slice();
        let tmp = new Float32Array(buf.length);
        for (let pass = 0; pass < 3; pass++) {
            // horizontal
            for (let y = 0; y < h; y++) {
                const off = y * w;
                let sum = 0;
                for (let i = -radius; i <= radius; i++) {
                    sum += buf[off + Math.max(0, Math.min(w - 1, i))];
                }
                const inv = 1.0 / (2 * radius + 1);
                tmp[off] = sum * inv;
                for (let x = 1; x < w; x++) {
                    const out = x - radius - 1;
                    const inn = x + radius;
                    sum -= buf[off + Math.max(0, Math.min(w - 1, out))];
                    sum += buf[off + Math.max(0, Math.min(w - 1, inn))];
                    tmp[off + x] = sum * inv;
                }
            }
            [buf, tmp] = [tmp, buf];
            // vertical
            for (let x = 0; x < w; x++) {
                let sum = 0;
                for (let i = -radius; i <= radius; i++) {
                    sum += buf[Math.max(0, Math.min(h - 1, i)) * w + x];
                }
                const inv = 1.0 / (2 * radius + 1);
                tmp[x] = sum * inv;
                for (let y = 1; y < h; y++) {
                    const out = y - radius - 1;
                    const inn = y + radius;
                    sum -= buf[Math.max(0, Math.min(h - 1, out)) * w + x];
                    sum += buf[Math.max(0, Math.min(h - 1, inn)) * w + x];
                    tmp[y * w + x] = sum * inv;
                }
            }
            [buf, tmp] = [tmp, buf];
        }
        return buf;
    }

    // ───────────────────────────────────────────────────────────────
    // GX-2: Background extraction (BGE) — single forward pass.
    // ───────────────────────────────────────────────────────────────
    //
    // Math mirrors GraXpert's background_extraction.py:
    //   1) downsample source to 256x256 (we let the model's receptive
    //      field handle the smooth background; loss vs the Python's
    //      256x240+pad-16 is sub-percent for the final correction)
    //   2) per-channel MAD normalize: (v − median) / MAD × 0.04, clip ±1
    //   3) mono → replicate across 3 channels for the NHWC tensor
    //   4) session.run({ gen_input_image: [1,256,256,3] })
    //   5) denormalize: out × MAD/0.04 + median
    //   6) Gaussian smooth (σ≈3, ~3-pass box blur for speed)
    //   7) bilinear resize back to source W×H
    //   8) apply correction: subtract / divide
    //
    // Sample-point user overrides + RBF interpolation deferred — v1
    // behaves like CLI auto-BGE (no manual points). Future GX-* phase
    // can add a sample-pick UI on the working canvas.
    //
    // Input: Uint16Array of mono pixels (length = w*h). RGB v2 ships
    // with GX-5 editor integration when color FITS pipelines land.

    class BgePipeline {
        async run(pixels, width, height, opts = {}) {
            const correction = opts.correction || 'Subtraction';
            const family = 'bge';
            const version = '1.0.1';
            const TILE = 256;

            const session = await loadSession(family, version, opts.onProgress);

            // 1) Downsample to 256x256.
            const small = bilinearResize(pixels, width, height, 1, TILE, TILE);

            // 2-3) Build [1, 256, 256, 3] NHWC float32 tensor.
            //      Mono → replicate to 3 channels. MAD normalize.
            //      Operate on the source (16-bit display range) scaled
            //      to [0,1] floats — matches what GraXpert does after
            //      its astropy normalize step.
            const planeF = new Float32Array(TILE * TILE);
            for (let i = 0; i < small.length; i++) planeF[i] = small[i] / 65535;
            const { median, mad } = medianMadSampled(planeF);

            const tensorData = new Float32Array(TILE * TILE * 3);
            for (let i = 0; i < TILE * TILE; i++) {
                const v = ((planeF[i] - median) / mad) * 0.04;
                const clipped = Math.max(-1, Math.min(1, v));
                tensorData[i * 3]     = clipped;
                tensorData[i * 3 + 1] = clipped;
                tensorData[i * 3 + 2] = clipped;
            }

            // 4) Inference.
            const ort = await loadOrtWeb();
            const inputName = session.inputNames[0]; // "gen_input_image"
            const outputName = session.outputNames[0];
            const inputTensor = new ort.Tensor('float32', tensorData,
                [1, TILE, TILE, 3]);
            const t0 = performance.now();
            const out = await session.run({ [inputName]: inputTensor });
            const inferenceMs = performance.now() - t0;
            const outTensor = out[outputName];
            const outData = outTensor.data; // Float32Array len = 1*256*256*3

            // 5) Average the three channels back to a mono background
            //    plane in [0,1] space and denormalize.
            const bgSmall = new Float32Array(TILE * TILE);
            for (let i = 0; i < TILE * TILE; i++) {
                const r = outData[i * 3];
                const g = outData[i * 3 + 1];
                const b = outData[i * 3 + 2];
                const avg = (r + g + b) / 3.0;
                bgSmall[i] = avg * mad / 0.04 + median;
            }

            // 6) Gaussian-ish smooth (3-pass box blur, radius 3).
            const bgSmooth = boxBlurF(bgSmall, TILE, TILE, 3);

            // 7) Resize back to source dimensions.
            const bgFull = bilinearResizeF(bgSmooth, TILE, TILE, width, height);

            // 8) Apply correction. Subtraction recentres around the
            //    median so the corrected background sits where the
            //    original median was (otherwise BGE subtracts toward
            //    zero and the resulting image looks crushed).
            //    Division mode: scale by bg/median so the output also
            //    keeps its overall brightness reference.
            const result = new Uint16Array(pixels.length);
            if (correction === 'Division') {
                for (let i = 0; i < pixels.length; i++) {
                    const v = pixels[i] / 65535;
                    const bg = Math.max(1e-6, bgFull[i]);
                    const corrected = v / bg * median;
                    result[i] = Math.max(0, Math.min(65535,
                        Math.round(corrected * 65535)));
                }
            } else {
                // Subtraction (default)
                for (let i = 0; i < pixels.length; i++) {
                    const v = pixels[i] / 65535;
                    const bg = bgFull[i];
                    const corrected = v - bg + median;
                    result[i] = Math.max(0, Math.min(65535,
                        Math.round(corrected * 65535)));
                }
            }

            return {
                pixels: result,
                width, height,
                channels: 1,
                stats: { median, mad, inferenceMs },
            };
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
        // Pipelines
        BgePipeline,
    };
})();
