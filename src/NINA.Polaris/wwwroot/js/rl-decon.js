// rl-decon.js — browser-side Richardson-Lucy deconvolution (White 1994 damped).
//
// The server measures the PSF, fits the noise model, and detects stars
// (POST /api/decon/rl-prepare). The heavy part — the RL iteration loop —
// runs here in the browser via a pure-JS FFT so the SBC server CPU stays
// free. Pixels arrive as Uint16Array (via /api/onnx/source-pixels) and
// results are saved via /api/onnx/save, reusing the existing ONNX pipeline
// plumbing.
//
// Exposes: window.RlBrowserDecon.deconvolve(pixels, w, h, channels, prep, iters, onProgress)

(function () {
    'use strict';

    const TILE = 512;   // must be power of 2; 512×512 tiles keep per-tile FFT cheap

    // ── Radix-2 Cooley-Tukey in-place FFT ─────────────────────────────
    // Operates on Float32Array re/im with stride/offset so the same
    // function handles both row-pass and column-pass of a 2-D transform.
    // Twiddle factors are computed on the fly (no precomputation table
    // needed for 512-point transforms; the cost is dominated by the RL
    // multiplications, not the sin/cos evaluations).

    function fft1d(re, im, offset, stride, n, inverse) {
        // Bit-reverse permutation
        let j = 0;
        for (let i = 1; i < n; i++) {
            let bit = n >> 1;
            while (j & bit) { j ^= bit; bit >>= 1; }
            j ^= bit;
            if (i < j) {
                let t = re[offset + i * stride]; re[offset + i * stride] = re[offset + j * stride]; re[offset + j * stride] = t;
                    t = im[offset + i * stride]; im[offset + i * stride] = im[offset + j * stride]; im[offset + j * stride] = t;
            }
        }
        // Butterfly stages
        const sign = inverse ? 1 : -1;
        for (let len = 2; len <= n; len <<= 1) {
            const halfLen = len >> 1;
            const ang = sign * Math.PI / halfLen;
            const baseWRe = Math.cos(ang), baseWIm = Math.sin(ang);
            for (let i = 0; i < n; i += len) {
                let wRe = 1.0, wIm = 0.0;
                for (let k = 0; k < halfLen; k++) {
                    const u = offset + (i + k) * stride;
                    const v = offset + (i + k + halfLen) * stride;
                    const vRe = re[v] * wRe - im[v] * wIm;
                    const vIm = re[v] * wIm + im[v] * wRe;
                    re[v] = re[u] - vRe;  im[v] = im[u] - vIm;
                    re[u] = re[u] + vRe;  im[u] = im[u] + vIm;
                    const nextWRe = wRe * baseWRe - wIm * baseWIm;
                    wIm = wRe * baseWIm + wIm * baseWRe;
                    wRe = nextWRe;
                }
            }
        }
        if (inverse) {
            const inv = 1.0 / n;
            for (let i = 0; i < n; i++) {
                re[offset + i * stride] *= inv;
                im[offset + i * stride] *= inv;
            }
        }
    }

    function fft2d(re, im, w, h, inverse) {
        for (let y = 0; y < h; y++) fft1d(re, im, y * w, 1, w, inverse);
        for (let x = 0; x < w; x++) fft1d(re, im, x, w, h, inverse);
    }

    // ── PSF spectrum precomputation ────────────────────────────────────
    // kernelData: Float32Array[kw*kh], center at (kh>>1, kw>>1).
    // Returns { psfRe, psfIm } — the TILE×TILE complex spectrum, reused
    // across all iterations and all tiles of the same frame.
    function preparePsf(kernelData, kw, kh) {
        const N = TILE * TILE;
        const psfRe = new Float32Array(N);
        const psfIm = new Float32Array(N);
        const hr = kh >> 1, wr = kw >> 1;
        for (let ky = 0; ky < kh; ky++) {
            for (let kx = 0; kx < kw; kx++) {
                // Wrap so the kernel center lands at (0,0) → circular convolution.
                const ty = ((ky - hr) % TILE + TILE) % TILE;
                const tx = ((kx - wr) % TILE + TILE) % TILE;
                psfRe[ty * TILE + tx] += kernelData[ky * kw + kx];
            }
        }
        fft2d(psfRe, psfIm, TILE, TILE, false);
        return { psfRe, psfIm };
    }

    // ── Spectral operations ────────────────────────────────────────────

    // Convolve src (Float32Array, TILE*TILE) with pre-computed PSF spectrum.
    // workRe/workIm are TILE*TILE scratch buffers (no alloc per call).
    function fftConvolve(src, psfRe, psfIm, workRe, workIm) {
        const n = TILE * TILE;
        workRe.set(src); workIm.fill(0);
        fft2d(workRe, workIm, TILE, TILE, false);
        for (let i = 0; i < n; i++) {
            const r = workRe[i] * psfRe[i] - workIm[i] * psfIm[i];
            workIm[i] = workRe[i] * psfIm[i] + workIm[i] * psfRe[i];
            workRe[i] = r;
        }
        fft2d(workRe, workIm, TILE, TILE, true);
        const out = new Float32Array(n);
        for (let i = 0; i < n; i++) out[i] = workRe[i];
        return out;
    }

    // Correlate = convolve with conjugate (time-reversed) PSF.
    function fftCorrelate(src, psfRe, psfIm, workRe, workIm) {
        const n = TILE * TILE;
        workRe.set(src); workIm.fill(0);
        fft2d(workRe, workIm, TILE, TILE, false);
        for (let i = 0; i < n; i++) {
            // multiply by conj(PSF): (a+jb)(c-jd) = ac+bd + j(bc-ad)
            const r = workRe[i] * psfRe[i] + workIm[i] * psfIm[i];
            workIm[i] = workIm[i] * psfRe[i] - workRe[i] * psfIm[i];
            workRe[i] = r;
        }
        fft2d(workRe, workIm, TILE, TILE, true);
        const out = new Float32Array(n);
        for (let i = 0; i < n; i++) out[i] = workRe[i];
        return out;
    }

    // ── Star protect mask ──────────────────────────────────────────────
    // Returns Float32Array(w*h): 1 in protected zones (stars), 0 elsewhere.
    // featherR: box-blur radius for the ramp (eliminates hard edges).
    function buildStarMask(stars, w, h, featherR) {
        const mask = new Float32Array(w * h);
        for (const s of stars) {
            const r2 = s.r * s.r;
            const x0 = Math.max(0, Math.floor(s.x - s.r));
            const x1 = Math.min(w - 1, Math.ceil(s.x + s.r));
            const y0 = Math.max(0, Math.floor(s.y - s.r));
            const y1 = Math.min(h - 1, Math.ceil(s.y + s.r));
            for (let py = y0; py <= y1; py++) {
                const dy = py - s.y;
                for (let px = x0; px <= x1; px++) {
                    if ((px - s.x) * (px - s.x) + dy * dy <= r2)
                        mask[py * w + px] = 1;
                }
            }
        }
        if (featherR <= 0) return mask;
        const tmp = new Float32Array(w * h);
        // Horizontal pass
        for (let py = 0; py < h; py++) {
            for (let px = 0; px < w; px++) {
                let s = 0;
                for (let dx = -featherR; dx <= featherR; dx++)
                    s += mask[py * w + Math.min(w - 1, Math.max(0, px + dx))];
                tmp[py * w + px] = s / (2 * featherR + 1);
            }
        }
        // Vertical pass
        for (let py = 0; py < h; py++) {
            for (let px = 0; px < w; px++) {
                let s = 0;
                for (let dy = -featherR; dy <= featherR; dy++)
                    s += tmp[Math.min(h - 1, Math.max(0, py + dy)) * w + px];
                mask[py * w + px] = s / (2 * featherR + 1);
            }
        }
        return mask;
    }

    // ── Main deconvolution entry point ─────────────────────────────────
    // pixels:    Uint16Array, plane-sequential (ch0 then ch1 then ch2)
    // w, h:      frame dimensions
    // channels:  1 or 3
    // prep:      object from /api/decon/rl-prepare (decoded kernel etc.)
    // iters:     RL iteration count
    // onProgress(fraction 0..1): called after each tile; yields to the
    //            event loop every 4 tiles so the tab doesn't freeze.
    // Returns:   Uint16Array (same layout as pixels)
    async function deconvolve(pixels, w, h, channels, prep, iters, onProgress) {
        // Decode kernel (base64 little-endian float32)
        const kBytes = Uint8Array.from(atob(prep.kernelBase64), c => c.charCodeAt(0));
        const kernelData = new Float32Array(kBytes.buffer);
        const kSize = prep.kernelSize;  // odd, square
        const { psfRe, psfIm } = preparePsf(kernelData, kSize, kSize);

        const kernelR = kSize >> 1;
        const overlap = Math.max(kernelR, 8);   // margin to hide wrap-around contamination
        const inner = TILE - 2 * overlap;
        if (inner <= 0) throw new Error('Tile too small for this PSF radius (' + kernelR + ')');

        const tilesX = Math.max(1, Math.ceil(w / inner));
        const tilesY = Math.max(1, Math.ceil(h / inner));
        const totalTiles = tilesX * tilesY * channels;
        let doneTiles = 0;

        // Shared scratch buffers (avoid allocating per tile)
        const workRe = new Float32Array(TILE * TILE);
        const workIm = new Float32Array(TILE * TILE);

        // Noise model from photon-transfer fit (σ² = noiseA·S + noiseB)
        const { noiseA, noiseB, background: bg, dampT = 2.5 } = prep;

        // Star protect mask (frame-global)
        let starMask = null;
        if (prep.protectStars && prep.stars && prep.stars.length > 0) {
            const meanR = prep.stars.reduce((s, st) => s + st.r, 0) / prep.stars.length;
            const feather = Math.min(24, Math.max(4, Math.round(meanR * 0.8)));
            starMask = buildStarMask(prep.stars, w, h, feather);
        }

        const plane = w * h;
        const outPixels = new Uint16Array(pixels.length);

        for (let ch = 0; ch < channels; ch++) {
            const base = ch * plane;

            // Observed channel as float (input to RL, held constant)
            const obs = new Float32Array(plane);
            for (let i = 0; i < plane; i++) obs[i] = pixels[base + i];

            // Accumulate weighted output + weights (feathered blend)
            const result = new Float32Array(plane);
            const weight = new Float32Array(plane);

            for (let ty = 0; ty < tilesY; ty++) {
                for (let tx = 0; tx < tilesX; tx++) {
                    // Inner region of this tile in frame coordinates
                    const ix0 = tx * inner;
                    const iy0 = ty * inner;
                    const ix1 = Math.min(ix0 + inner, w);
                    const iy1 = Math.min(iy0 + inner, h);

                    // Extract TILE×TILE patch (with edge replication for border tiles)
                    const tile = new Float32Array(TILE * TILE);
                    for (let py = 0; py < TILE; py++) {
                        const fy = Math.min(h - 1, Math.max(0, iy0 - overlap + py));
                        for (let px = 0; px < TILE; px++) {
                            const fx = Math.min(w - 1, Math.max(0, ix0 - overlap + px));
                            tile[py * TILE + px] = obs[fy * w + fx];
                        }
                    }

                    // Per-pixel noise σ from photon-transfer model
                    const sigma = new Float32Array(TILE * TILE);
                    for (let i = 0; i < TILE * TILE; i++) {
                        const s = Math.max(0, tile[i] - bg);
                        sigma[i] = Math.sqrt(Math.max(1, noiseA * s + noiseB));
                    }

                    // Richardson-Lucy iterations (White 1994 damped)
                    const estimate = tile.slice();
                    for (let iter = 0; iter < iters; iter++) {
                        const blurred = fftConvolve(estimate, psfRe, psfIm, workRe, workIm);
                        const ratio = new Float32Array(TILE * TILE);
                        for (let i = 0; i < TILE * TILE; i++) {
                            const b = Math.max(blurred[i], 1e-6);
                            let r = tile[i] / b;
                            // Damping: suppress the correction where the residual
                            // is within dampT standard deviations of the noise
                            // (White 1994): prevents amplifying noise into dark rings.
                            const diff = Math.abs(tile[i] - blurred[i]);
                            const z = Math.min(1, diff / (dampT * sigma[i]));
                            const u = z * z * (3 - 2 * z);   // smoothstep
                            ratio[i] = 1 + u * (r - 1);
                        }
                        const correction = fftCorrelate(ratio, psfRe, psfIm, workRe, workIm);
                        for (let i = 0; i < TILE * TILE; i++)
                            estimate[i] = Math.max(0, estimate[i] * Math.max(0, correction[i]));
                    }

                    // Blend inner portion back with cosine feathering
                    for (let py = overlap; py < TILE - overlap; py++) {
                        const fy = iy0 + py - overlap;
                        if (fy >= iy1) break;
                        for (let px = overlap; px < TILE - overlap; px++) {
                            const fx = ix0 + px - overlap;
                            if (fx >= ix1) break;
                            // Distance from inner edge (0 at edge, grows inward)
                            const edge = Math.min(py - overlap, TILE - overlap - 1 - py,
                                                  px - overlap, TILE - overlap - 1 - px);
                            const wf = edge >= overlap
                                ? 1.0
                                : 0.5 - 0.5 * Math.cos(Math.PI * (edge + 0.5) / overlap);
                            result[fy * w + fx] += estimate[py * TILE + px] * wf;
                            weight[fy * w + fx] += wf;
                        }
                    }

                    doneTiles++;
                    if (onProgress) onProgress(doneTiles / totalTiles);
                    // Yield every 4 tiles so the UI can update
                    if (doneTiles % 4 === 0) await new Promise(r => setTimeout(r, 0));
                }
            }

            // Normalise
            for (let i = 0; i < plane; i++)
                result[i] = weight[i] > 0 ? result[i] / weight[i] : obs[i];

            // Composite stars from original (protect mask 0=decon, 1=keep)
            if (starMask) {
                for (let i = 0; i < plane; i++) {
                    const keep = Math.min(1, starMask[i]);
                    result[i] = keep * obs[i] + (1 - keep) * result[i];
                }
            }

            for (let i = 0; i < plane; i++)
                outPixels[base + i] = Math.min(65535, Math.max(0, Math.round(result[i])));
        }

        return outPixels;
    }

    window.RlBrowserDecon = { deconvolve };
})();
