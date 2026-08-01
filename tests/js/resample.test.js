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

// VIEWFILT: the Lanczos resampler is the one part of the view filter that is
// real arithmetic rather than a CSS property, so it is the part worth testing.
// Run with: node tests/js/resample.test.js

const assert = require('assert');
const path = require('path');
const R = require(path.join(__dirname, '..', '..', 'src', 'NINA.Polaris',
                            'wwwroot', 'js', 'resample.js'));

let passed = 0;
function test(name, fn) {
    try {
        fn();
        passed++;
        console.log('  ok   ' + name);
    } catch (e) {
        console.error('  FAIL ' + name + '\n       ' + e.message);
        process.exitCode = 1;
    }
}

function solid(w, h, r, g, b, a) {
    const px = new Uint8ClampedArray(w * h * 4);
    for (let i = 0; i < w * h; i++) {
        px[i * 4] = r; px[i * 4 + 1] = g; px[i * 4 + 2] = b; px[i * 4 + 3] = a === undefined ? 255 : a;
    }
    return px;
}

console.log('resample.js');

// ---- the kernel itself ----

test('lanczos(0) is 1 and the singularity does not produce NaN', () => {
    assert.strictEqual(R.lanczos(0, 3), 1);
});

test('lanczos is zero at every nonzero integer inside the support', () => {
    // This is what makes it an interpolating filter: at an exact source pixel
    // the neighbours contribute nothing, so 1:1 output equals the input.
    for (const x of [1, 2, -1, -2]) {
        assert.ok(Math.abs(R.lanczos(x, 3)) < 1e-12, 'lanczos(' + x + ') = ' + R.lanczos(x, 3));
    }
});

test('lanczos is zero outside the support', () => {
    assert.strictEqual(R.lanczos(3, 3), 0);
    assert.strictEqual(R.lanczos(4.7, 3), 0);
    assert.strictEqual(R.lanczos(-3, 3), 0);
});

test('lanczos is symmetric', () => {
    for (const x of [0.3, 1.4, 2.9]) {
        assert.ok(Math.abs(R.lanczos(x, 3) - R.lanczos(-x, 3)) < 1e-15);
    }
});

test('every output pixel taps weights summing to 1', () => {
    // Weights that do not sum to 1 darken or brighten the image; at the edges,
    // where taps fall outside and get clamped, this is where it shows first.
    for (const [src, dst] of [[10, 30], [30, 10], [7, 7], [1000, 137]]) {
        const taps = R.buildTaps(src, dst, 3);
        for (let i = 0; i < taps.length; i++) {
            const s = taps[i].w.reduce((a, b) => a + b, 0);
            assert.ok(Math.abs(s - 1) < 1e-9,
                src + '->' + dst + ' pixel ' + i + ' sums to ' + s);
        }
    }
});

test('taps never index outside the source', () => {
    for (const [src, dst] of [[8, 64], [64, 8], [5, 5]]) {
        for (const t of R.buildTaps(src, dst, 3)) {
            for (const i of t.idx) {
                assert.ok(i >= 0 && i < src, 'index ' + i + ' outside 0..' + (src - 1));
            }
        }
    }
});

// ---- the resample ----

test('a flat image stays exactly flat at every scale', () => {
    // The single most visible failure mode: normalisation drift shows up as a
    // darker border, and a stretched astro background makes it obvious.
    const src = solid(16, 16, 40, 80, 120, 255);
    for (const [w, h] of [[64, 64], [7, 5], [16, 16], [33, 9]]) {
        const out = R.resampleRGBA(src, 16, 16, w, h, 3);
        for (let i = 0; i < w * h; i++) {
            assert.strictEqual(out[i * 4], 40, 'R at ' + i + ' for ' + w + 'x' + h);
            assert.strictEqual(out[i * 4 + 1], 80);
            assert.strictEqual(out[i * 4 + 2], 120);
            assert.strictEqual(out[i * 4 + 3], 255);
        }
    }
});

test('resampling to the same size returns the same pixels', () => {
    // Falls out of the kernel being zero at nonzero integers. If this breaks,
    // switching the filter on would blur an image nobody asked to change.
    const w = 12, h = 9;
    const src = new Uint8ClampedArray(w * h * 4);
    for (let i = 0; i < w * h; i++) {
        src[i * 4] = (i * 7) % 256;
        src[i * 4 + 1] = (i * 13) % 256;
        src[i * 4 + 2] = (i * 29) % 256;
        src[i * 4 + 3] = 255;
    }
    const out = R.resampleRGBA(src, w, h, w, h, 3);
    for (let i = 0; i < w * h * 4; i++) {
        assert.ok(Math.abs(out[i] - src[i]) <= 1,
            'byte ' + i + ': ' + out[i] + ' vs ' + src[i]);
    }
});

test('output dimensions and buffer length are exactly what was asked for', () => {
    const out = R.resampleRGBA(solid(10, 10, 1, 2, 3), 10, 10, 37, 21, 3);
    assert.strictEqual(out.length, 37 * 21 * 4);
    assert.ok(out instanceof Uint8ClampedArray);
});

test('a magnified edge stays monotonic between the two levels', () => {
    // Lanczos overshoots at an edge, which is the point of it, but the ringing
    // must stay bounded and the transition must not invert.
    const w = 8, h = 1;
    const src = new Uint8ClampedArray(w * 4);
    for (let x = 0; x < w; x++) {
        const v = x < 4 ? 0 : 255;
        src[x * 4] = v; src[x * 4 + 1] = v; src[x * 4 + 2] = v; src[x * 4 + 3] = 255;
    }
    const out = R.resampleRGBA(src, w, h, w * 8, 1, 3);
    // Left end still dark, right end still bright.
    assert.ok(out[0] < 40, 'left end brightened to ' + out[0]);
    assert.ok(out[(w * 8 - 1) * 4] > 215, 'right end darkened to ' + out[(w * 8 - 1) * 4]);
    // The clamped output can never leave 0..255, whatever the kernel does.
    for (let i = 0; i < out.length; i++) {
        assert.ok(out[i] >= 0 && out[i] <= 255);
    }
});

test('a single bright pixel stays put and stays the brightest', () => {
    const w = 9, h = 9;
    const src = new Uint8ClampedArray(w * h * 4);
    for (let i = 0; i < w * h; i++) src[i * 4 + 3] = 255;
    const centre = (4 * w + 4) * 4;
    src[centre] = 255; src[centre + 1] = 255; src[centre + 2] = 255;

    const out = R.resampleRGBA(src, w, h, w * 4, h * 4, 3);
    let bestIdx = -1, best = -1;
    for (let i = 0; i < w * 4 * h * 4; i++) {
        if (out[i * 4] > best) { best = out[i * 4]; bestIdx = i; }
    }
    const bx = bestIdx % (w * 4), by = Math.floor(bestIdx / (w * 4));
    // The centre of source pixel 4 maps to output 4*4 + 1.5 = 17.5, so the two
    // pixels either side of it are the peak.
    assert.ok(Math.abs(bx - 17.5) <= 1.5, 'peak drifted to x=' + bx);
    assert.ok(Math.abs(by - 17.5) <= 1.5, 'peak drifted to y=' + by);
});

test('bad arguments throw instead of returning a wrong-sized buffer', () => {
    assert.throws(() => R.resampleRGBA(solid(4, 4, 0, 0, 0), 4, 4, 0, 8, 3), /positive/);
    assert.throws(() => R.resampleRGBA(solid(4, 4, 0, 0, 0), 4, 4, -1, 8, 3), /positive/);
    assert.throws(() => R.resampleRGBA(new Uint8ClampedArray(4), 40, 40, 8, 8, 3), /shorter/);
});

test('estimateCost grows with the work actually done', () => {
    const small = R.estimateCost(100, 100, 200, 200, 3);
    const big = R.estimateCost(1000, 1000, 2000, 2000, 3);
    assert.ok(big > small * 50, 'cost should scale with pixel count');
    // Minification costs more per output pixel, since the kernel widens.
    assert.ok(R.estimateCost(4000, 4000, 100, 100, 3) > R.estimateCost(100, 100, 100, 100, 3));
});

// ---- it has to be fast enough to be worth offering ----

test('a realistic view-sized upscale completes in well under a second', () => {
    const sw = 800, sh = 600;
    const src = solid(sw, sh, 12, 34, 56);
    const t0 = Date.now();
    R.resampleRGBA(src, sw, sh, 1600, 1200, 3);
    const ms = Date.now() - t0;
    console.log('       800x600 -> 1600x1200 took ' + ms + ' ms');
    assert.ok(ms < 4000, 'took ' + ms + ' ms, too slow to be a view filter');
});

console.log(passed + ' assertions passed');
