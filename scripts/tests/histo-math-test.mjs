// Tests for the LIVE histogram's pure math, lifted straight out of app.js.
//
// app.js is one 48k-line Alpine component, not a module, so there is nothing to
// import. The functions are cut out by name and evaluated against a stub `this`
// — the same trick scripts/tests/install-linux-helpers-test.sh uses to test the
// installer's shell helpers without running the installer.
//
//   node scripts/tests/histo-math-test.mjs
//
// What is guarded here:
//   * the framing rule, which used to live in two places (a percentile band
//     chosen on the server and a zoom applied on the client) that drifted apart
//   * the resolution requirement inherited from LiveStackHistogramBandTests: a
//     stacked sky whose sigma is ~124 ADU must span several bins, not collapse
//     into one spike
//   * the handle round trip, which is now the identity because axis space and
//     stretch space are the same space

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const SRC = path.join(here, '..', '..', 'src', 'NINA.Polaris', 'wwwroot', 'js', 'app.js');
const src = fs.readFileSync(SRC, 'utf8');

let fails = 0;
const ok = (m) => console.log('  ok   ' + m);
const bad = (m) => { console.log('  FAIL ' + m); fails++; };
const near = (a, b, eps, m) =>
    Math.abs(a - b) <= eps ? ok(m) : bad(`${m}: ${a} vs ${b} (tol ${eps})`);

// ---- lift the functions ---------------------------------------------------
// Each is `        name(args) {` at 8 spaces, closing at `        },`.
function lift(name) {
    const start = src.indexOf(`\n        ${name}(`);
    if (start < 0) throw new Error(`not found in app.js: ${name}`);
    const end = src.indexOf('\n        },', start);
    if (end < 0) throw new Error(`unterminated: ${name}`);
    return src.slice(start + 1, end + '\n        },'.length).replace(/\r/g, '');
}

const NAMES = ['_histoBulkOf', '_histoFrame', '_histoUpdateEndpoints', '_histoDragMove',
               '_histoSample', '_stretchForFrame', '_computePerChannelStretch',
               '_autoStretchEndpoints', '_mtf'];
const app = eval('({' + NAMES.map(lift).join('\n') + '\n})');

// The drag throttles its redraw through a frame callback; outside a browser
// there is neither, and the redraw is not what is under test here.
globalThis.requestAnimationFrame = () => 0;
globalThis.cancelAnimationFrame = () => {};

// ---- a stack-shaped luminance histogram -----------------------------------
// 300k samples, sky at 1277 ADU with sigma 124, plus a sparse star tail out to
// 43738 — the same shape LiveStackHistogramBandTests used on real field data.
const NB = 2048;
function fieldBins() {
    const bins = new Float64Array(NB);
    const put = (adu, n) => {
        let i = Math.floor((adu / 65536) * NB);
        if (i < 0) i = 0; if (i >= NB) i = NB - 1;
        bins[i] += n;
    };
    for (let s = -400; s <= 400; s += 8) {
        const w = Math.exp(-(s * s) / (2 * 124 * 124));
        put(1277 + s, Math.round(300000 * w / 40));
    }
    for (let adu = 2000; adu < 43738; adu += 250) put(adu, 3);
    return bins;
}

console.log('== _histoBulkOf: the band the data occupies ==');
{
    const b = fieldBins();
    const [lo, hi] = app._histoBulkOf(b);
    const loAdu = lo * 65535, hiAdu = hi * 65535;
    (loAdu > 500 && loAdu < 1277) ? ok('low edge sits below the sky peak')
                                  : bad(`low edge ${loAdu.toFixed(0)} ADU`);
    (hiAdu > 1400 && hiAdu < 12000) ? ok('the sparse star tail does not set the high edge')
                                    : bad(`high edge ${hiAdu.toFixed(0)} ADU`);
    const empty = app._histoBulkOf(new Float64Array(NB));
    empty[0] === null ? ok('an empty histogram reports no band') : bad('empty band');
}

console.log('== _histoFrame: one framing rule ==');
{
    const base = {
        HISTO_BINS: NB,
        histoZoom: false,
        histo: { bins: fieldBins(), color: false, binsR: null },
        _histoBulkOf: app._histoBulkOf,
    };
    const off = app._histoFrame.call(base);
    (off[0] === 0 && off[1] === 1) ? ok('zoom off is the full scale')
                                   : bad(`zoom off gave ${off}`);

    base.histoZoom = true;
    const on = app._histoFrame.call(base);
    (on[0] >= 0 && on[1] <= 1 && on[1] > on[0]) ? ok('zoom on stays inside the scale')
                                                : bad(`zoom on gave ${on}`);
    (on[1] - on[0] < 0.5) ? ok('zoom on actually narrows the window')
                          : bad(`window is ${(on[1] - on[0]).toFixed(3)} of full scale`);

    // THE resolution requirement. The window is what the canvas shows, and the
    // canvas is ~256 px wide, so one sigma of sky has to cover several pixels
    // or the stack draws as a hairline — the defect the server-side band was
    // introduced to fix, restated where the framing now lives.
    const sigmaFrac = 124 / 65535;
    const perPixel = (on[1] - on[0]) / 256;
    (sigmaFrac / perPixel >= 4)
        ? ok(`one sigma of sky spans ${(sigmaFrac / perPixel).toFixed(1)} pixels`)
        : bad(`sky sigma covers only ${(sigmaFrac / perPixel).toFixed(2)} pixels`);

    // And the bins themselves must resolve it, which is why there are 2048.
    const sigmaBins = sigmaFrac * NB;
    (sigmaBins >= 3) ? ok(`one sigma of sky spans ${sigmaBins.toFixed(1)} bins`)
                     : bad(`sky sigma covers only ${sigmaBins.toFixed(2)} bins`);

    // A degenerate frame must not collapse to a zero-width window.
    const flat = { ...base, histo: { bins: (() => {
        const b = new Float64Array(NB); b[900] = 1e6; return b;
    })(), color: false, binsR: null } };
    const w = app._histoFrame.call(flat);
    (w[1] - w[0] >= 8 / NB) ? ok('a single-spike frame still gets a usable window')
                            : bad(`degenerate window ${w}`);
}

console.log('== handles: axis space IS stretch space ==');
{
    const st = {
        HISTO_BINS: NB, histoZoom: true,
        stretchAuto: false, stretchBlack: 0.10, stretchWhite: 0.80, stretchMid: 0.25,
        histo: { bins: fieldBins(), color: false, binsR: null,
                 _autoBlack: 0, _autoWhite: 1, _autoMid: 0.25,
                 dispLo: 0, dispHi: 1 },
        _histoDrag: null,
        _histoBulkOf: app._histoBulkOf,
        _histoFrame: app._histoFrame,
        _histoUpdateEndpoints: app._histoUpdateEndpoints,
        _histoDragMove: app._histoDragMove,
    };
    st._histoUpdateEndpoints();
    near(st.histo.blackFrac, 0.10, 1e-9, 'black handle sits on stretchBlack');
    near(st.histo.whiteFrac, 0.80, 1e-9, 'white handle sits on stretchWhite');
    near(st.histo.midFrac, 0.10 + 0.25 * 0.70, 1e-9, 'mid handle sits between them');

    // Drag the black handle to a known pixel and read the value back.
    st._histoDrag = { which: 'black', rect: { left: 0, width: 200 }, lo: 0, hi: 1 };
    st._histoDragMove({ clientX: 60 });
    near(st.stretchBlack, 0.30, 1e-9, 'dragging to 30% of the axis sets black to 0.30');
    near(st.histo.blackFrac, 0.30, 1e-9, 'and the handle follows, with no conversion');

    // Inside a zoomed window the mapping is the window, not the full scale.
    st._histoDrag = { which: 'white', rect: { left: 0, width: 200 }, lo: 0.20, hi: 0.40 };
    st._histoDragMove({ clientX: 100 });
    near(st.stretchWhite, 0.30, 1e-9, 'a zoomed drag maps through the window');

    // Neighbours bound each other, nothing else does.
    st._histoDrag = { which: 'black', rect: { left: 0, width: 200 }, lo: 0, hi: 1 };
    st._histoDragMove({ clientX: 190 });
    (st.stretchBlack <= st.stretchWhite) ? ok('black cannot cross white')
                                         : bad(`black ${st.stretchBlack} > white ${st.stretchWhite}`);
}

console.log('== a drag freezes the framing ==');
{
    const st = {
        HISTO_BINS: NB, histoZoom: true,
        stretchAuto: false, stretchBlack: 0, stretchWhite: 1, stretchMid: 0.25,
        histo: { bins: fieldBins(), color: false, binsR: null, dispLo: 0.11, dispHi: 0.22 },
        _histoDrag: { which: 'black', rect: { left: 0, width: 100 }, lo: 0.11, hi: 0.22 },
        _histoBulkOf: app._histoBulkOf,
        _histoFrame: app._histoFrame,
        _histoUpdateEndpoints: app._histoUpdateEndpoints,
    };
    st._histoUpdateEndpoints();
    (st.histo.dispLo === 0.11 && st.histo.dispHi === 0.22)
        ? ok('the window does not move under the cursor')
        : bad(`window moved to ${st.histo.dispLo}..${st.histo.dispHi}`);
}

console.log('== _histoSample: no staircase when the window covers few bins ==');
{
    const b = fieldBins();
    const W = 800;

    // The case from the field screenshot: a 595 ADU window over 2048 bins is
    // about 19 bins spread across the canvas. Repeating each bin's value across
    // its column drew 19 plateaus with a cliff between them.
    const lo = 1000 / 65535, hi = 1595 / 65535;
    const binsInWindow = (hi - lo) * NB;
    (binsInWindow < 25) ? ok(`the window really is narrow (${binsInWindow.toFixed(1)} bins)`)
                        : bad(`window covers ${binsInWindow.toFixed(1)} bins, not the case under test`);

    const v = app._histoSample.call({}, b, lo, hi, W);
    const distinct = new Set(Array.from(v, (x) => x.toFixed(6))).size;
    (distinct > W * 0.5)
        ? ok(`${distinct} distinct heights across ${W + 1} columns`)
        : bad(`only ${distinct} distinct heights: the curve is still a staircase`);

    // The longest run of identical samples is the plateau length.
    let run = 1, worst = 1;
    for (let i = 1; i <= W; i++) {
        run = (v[i] === v[i - 1]) ? run + 1 : 1;
        if (run > worst) worst = run;
    }
    (worst <= 4) ? ok(`longest flat run is ${worst} px`)
                 : bad(`a ${worst} px plateau survived`);

    // Interpolating must not invent signal outside the data's range.
    let mx = 0;
    for (let i = 0; i < b.length; i++) if (b[i] > mx) mx = b[i];
    const smax = Math.max(...v);
    (smax <= mx + 1e-9) ? ok('sampling never exceeds the tallest bin')
                        : bad(`sample ${smax} above the tallest bin ${mx}`);
    (Math.min(...v) >= 0) ? ok('and never goes negative') : bad('negative sample');
}

console.log('== _histoSample: a narrow spike survives the zoomed-OUT view ==');
{
    // The other direction. Full scale over 800 columns is ~2.5 bins per column,
    // so a one-bin spike has to be picked up by the column that contains it —
    // averaging there would flatten the sky peak of a stacked frame.
    const b = new Float64Array(NB);
    b[1000] = 5000;
    const v = app._histoSample.call({}, b, 0, 1, 800);
    (Math.max(...v) > 5000 * 0.4)
        ? ok('a single-bin spike still reaches the curve')
        : bad(`spike flattened to ${Math.max(...v).toFixed(0)} of 5000`);
}

console.log('== manual mode keeps the per-channel stretch ==');
{
    // A colour sub with the channels at clearly different sky levels — the
    // shape of a real OSC frame, and the reason the per-channel path exists.
    const W = 64, H = 64, N = W * H, MAXV = 65535;
    const px = new Uint16Array(N * 3);
    let seed = 7;
    const noise = () => { seed = (seed * 1103515245 + 12345) & 0x7fffffff; return (seed % 400) - 200; };
    for (let i = 0; i < N; i++) {
        px[i] = 4000 + noise();          // R
        px[N + i] = 5000 + noise();      // G
        px[2 * N + i] = 12000 + noise(); // B, a long way right of the others
    }

    const mk = (over) => Object.assign({
        stretchAuto: true, stretchBlack: 0, stretchWhite: 1, stretchMid: 0.25,
        histo: { _autoBlack: 0.05, _autoWhite: 1, _autoMid: null },
        _mtf: app._mtf,
        _autoStretchEndpoints: app._autoStretchEndpoints,
        _computePerChannelStretch: app._computePerChannelStretch,
        _stretchForFrame: app._stretchForFrame,
    }, over);

    const auto = mk({})._stretchForFrame(px, W, H, 0, MAXV, 3, 0, 0.25);
    if (!auto.perChan) { bad('auto mode produced no per-channel stretch'); }
    else {
        ok('auto mode is per-channel');
        (auto.perChan.b.shadow > auto.perChan.g.shadow
         && auto.perChan.g.shadow > auto.perChan.r.shadow)
            ? ok('each channel gets its own black point')
            : bad(`shadows R=${auto.perChan.r.shadow.toFixed(0)} `
                + `G=${auto.perChan.g.shadow.toFixed(0)} B=${auto.perChan.b.shadow.toFixed(0)}`);
    }

    // THE REGRESSION. Manual mode used to return null here, so the first touch
    // of any handle replaced the per-channel endpoints with one global black
    // point. On this frame that drives blue far past the other two and the
    // picture goes solid blue — reported from the field after nudging the
    // midtones handle, which had nothing to do with it.
    const manual = mk({ stretchAuto: false, stretchBlack: 0.05, stretchWhite: 1,
                        stretchMid: 0.4 })._stretchForFrame(px, W, H, 0, MAXV, 3, 0, 0.25);
    manual.perChan ? ok('manual mode is STILL per-channel')
                   : bad('manual mode dropped the per-channel stretch');

    if (manual.perChan && auto.perChan) {
        // Seeded at the auto endpoints, the delta is zero: no jump on the first
        // pixel of a drag.
        for (const c of ['r', 'g', 'b']) {
            near(manual.perChan[c].shadow, auto.perChan[c].shadow, 1e-6,
                `${c}: black unchanged when the handle sits where Auto left it`);
        }
        near(manual.midtone, 0.4, 1e-9, 'the midtone handle is what sets the midtone');

        // Move the black handle: all three shift together, gaps preserved, so
        // the colour balance does not move.
        const moved = mk({ stretchAuto: false, stretchBlack: 0.08, stretchWhite: 1,
                           stretchMid: 0.25 })._stretchForFrame(px, W, H, 0, MAXV, 3, 0, 0.25);
        const d = 0.03 * MAXV;
        for (const c of ['r', 'g', 'b']) {
            near(moved.perChan[c].shadow, auto.perChan[c].shadow + d, 1e-6,
                `${c}: black moves by exactly the handle delta`);
        }
        const gapAuto = auto.perChan.b.shadow - auto.perChan.r.shadow;
        const gapMoved = moved.perChan.b.shadow - moved.perChan.r.shadow;
        near(gapMoved, gapAuto, 1e-6, 'the gap between channels survives the drag');
    }

    const mono = mk({})._stretchForFrame(new Uint16Array(N), W, H, 0, MAXV, 1, 0, 0.25);
    (mono.perChan === null && mono.midtone === 0.25)
        ? ok('a mono frame keeps the single global stretch')
        : bad('mono frame took the colour path');

    const calib = mk({})._stretchForFrame(px, W, H, 0, MAXV, 3, 1, 0.25);
    (calib.perChan === null)
        ? ok('a calibration frame is rendered neutral, not sky-neutralised')
        : bad('calibration frame took the per-channel path');
}

console.log();
console.log(fails === 0 ? 'all checks passed' : `${fails} check(s) failed`);
process.exit(fails === 0 ? 0 : 1);
