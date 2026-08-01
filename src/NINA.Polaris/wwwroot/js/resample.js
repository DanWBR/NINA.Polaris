// N.I.N.A. Polaris
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT
// ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or
// FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License
// for more details. You should have received a copy of the license along with
// this program. If not, see <https://www.gnu.org/licenses/>.

// VIEWFILT: a real Lanczos resampler, for VIEWING only.
//
// The browser gives exactly two resampling behaviours through CSS:
// `image-rendering: pixelated` (nearest neighbour) and everything else
// (a bilinear-ish filter whose details are the engine's business). There is no
// CSS or canvas knob for "bicubic" or "Lanczos", so a menu offering them on top
// of `image-rendering` would be a menu of labels, not of filters. This file is
// what makes the Lanczos entry mean something.
//
// Nothing here touches saved pixels. It resamples a preview for display and the
// result never reaches a file.
(function (global) {
    'use strict';

    // sinc(x) = sin(pi x) / (pi x), with the removable singularity at 0 filled in.
    function sinc(x) {
        if (x === 0) return 1;
        const p = Math.PI * x;
        return Math.sin(p) / p;
    }

    // Lanczos kernel of order a: sinc(x) windowed by sinc(x/a), zero outside
    // |x| >= a. a = 3 is the usual choice; a = 2 rings less and blurs slightly
    // more.
    function lanczos(x, a) {
        if (x < 0) x = -x;
        if (x >= a) return 0;
        return sinc(x) * sinc(x / a);
    }

    // Per-output-pixel filter taps for one axis. Computed once and reused for
    // every row (or column), which is the whole point of doing the resample
    // separably: a 3-lobe 2D kernel is 36 taps per pixel, two 1D passes are 12.
    //
    // scale < 1 (minification) widens the kernel in SOURCE space, otherwise the
    // filter would alias by sampling between the pixels it is meant to average.
    function buildTaps(srcLen, dstLen, a) {
        const scale = dstLen / srcLen;
        const support = scale < 1 ? a / scale : a;
        const taps = new Array(dstLen);
        for (let i = 0; i < dstLen; i++) {
            // Centre of output pixel i, expressed in source coordinates.
            const centre = (i + 0.5) / scale - 0.5;
            let lo = Math.ceil(centre - support);
            let hi = Math.floor(centre + support);
            const idx = [];
            const w = [];
            let sum = 0;
            for (let j = lo; j <= hi; j++) {
                // Clamp at the edges rather than wrapping or zeroing: a black
                // fringe around a stretched astro frame reads as data.
                const s = j < 0 ? 0 : (j >= srcLen ? srcLen - 1 : j);
                const t = scale < 1 ? (j - centre) * scale : (j - centre);
                const weight = lanczos(t, a);
                if (weight === 0) continue;
                idx.push(s);
                w.push(weight);
                sum += weight;
            }
            // Normalise so flat input stays flat. Without this, edge pixels
            // (where taps fall outside and get clamped) come out darker.
            if (sum !== 0) for (let k = 0; k < w.length; k++) w[k] /= sum;
            taps[i] = { idx: idx, w: w };
        }
        return taps;
    }

    /**
     * Resample RGBA pixels with a Lanczos kernel.
     *
     * @param {Uint8ClampedArray} src  RGBA, srcW * srcH * 4
     * @param {number} srcW
     * @param {number} srcH
     * @param {number} dstW
     * @param {number} dstH
     * @param {number} [a=3]           kernel order (lobes)
     * @returns {Uint8ClampedArray}    RGBA, dstW * dstH * 4
     */
    function resampleRGBA(src, srcW, srcH, dstW, dstH, a) {
        a = a || 3;
        if (!(srcW > 0 && srcH > 0 && dstW > 0 && dstH > 0)) {
            throw new Error('resample: every dimension must be positive');
        }
        if (src.length < srcW * srcH * 4) {
            throw new Error('resample: source buffer is shorter than srcW * srcH * 4');
        }

        // Horizontal pass into a float buffer at dstW x srcH. Staying in floats
        // between the passes matters: rounding to 8 bits twice visibly bands a
        // stretched background.
        const xTaps = buildTaps(srcW, dstW, a);
        const mid = new Float32Array(dstW * srcH * 4);
        for (let y = 0; y < srcH; y++) {
            const srcRow = y * srcW * 4;
            const midRow = y * dstW * 4;
            for (let x = 0; x < dstW; x++) {
                const t = xTaps[x];
                let r = 0, g = 0, b = 0, al = 0;
                for (let k = 0; k < t.idx.length; k++) {
                    const p = srcRow + t.idx[k] * 4;
                    const w = t.w[k];
                    r += src[p] * w;
                    g += src[p + 1] * w;
                    b += src[p + 2] * w;
                    al += src[p + 3] * w;
                }
                const o = midRow + x * 4;
                mid[o] = r; mid[o + 1] = g; mid[o + 2] = b; mid[o + 3] = al;
            }
        }

        // Vertical pass into the final 8-bit buffer.
        const yTaps = buildTaps(srcH, dstH, a);
        const out = new Uint8ClampedArray(dstW * dstH * 4);
        for (let y = 0; y < dstH; y++) {
            const t = yTaps[y];
            const outRow = y * dstW * 4;
            for (let x = 0; x < dstW; x++) {
                let r = 0, g = 0, b = 0, al = 0;
                for (let k = 0; k < t.idx.length; k++) {
                    const p = (t.idx[k] * dstW + x) * 4;
                    const w = t.w[k];
                    r += mid[p] * w;
                    g += mid[p + 1] * w;
                    b += mid[p + 2] * w;
                    al += mid[p + 3] * w;
                }
                const o = outRow + x * 4;
                // Uint8ClampedArray rounds and clamps on assignment, which is
                // what we want: Lanczos overshoots past 0 and 255 around edges.
                out[o] = r; out[o + 1] = g; out[o + 2] = b; out[o + 3] = al;
            }
        }
        return out;
    }

    /**
     * Cost of a resample, in tap-multiplies. The caller uses this to decide
     * whether to attempt one at all: on a phone, a big upscale is seconds, and
     * a viewer that locks up is worse than a slightly softer image.
     */
    function estimateCost(srcW, srcH, dstW, dstH, a) {
        a = a || 3;
        const xSupport = dstW < srcW ? (2 * a * srcW / dstW) : 2 * a;
        const ySupport = dstH < srcH ? (2 * a * srcH / dstH) : 2 * a;
        return dstW * srcH * xSupport + dstW * dstH * ySupport;
    }

    const api = { resampleRGBA: resampleRGBA, estimateCost: estimateCost,
                  lanczos: lanczos, buildTaps: buildTaps };

    if (typeof module !== 'undefined' && module.exports) module.exports = api;
    global.PolarisResample = api;
})(typeof self !== 'undefined' ? self : this);
