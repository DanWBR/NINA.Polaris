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
               '_histoSample', '_channelGains', '_autoStretchEndpoints',
               '_screenTransfer', '_mtf', '_histoBuild', '_histoLuts',
               '_histoStretchSig', '_displayMap', '_computePerChannelStretch'];
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

console.log('== stage 1 balances the channels by GAIN ==');
{
    const MAXV = 65535;
    const ep = (median, mad) => {
        const shadow = Math.max(0, median - 3 * mad);
        const scale = MAXV > shadow ? 1 / (MAXV - shadow) : 1;
        return { shadow, scale, xMed: (median - shadow) * scale, median, mad };
    };
    const ctx = { _mtf: app._mtf, _channelGains: app._channelGains };
    const shown = (p, v) => Math.max(0, Math.min(1, (v - p.shadow) * p.scale));

    // The operator's SV605CC flat, measured in ASIFitsView: the channels are
    // nowhere near each other, and blue is at the saturation wall. Shifting
    // each channel's black point cannot bring those together — it put blue at
    // 0.900 of the output while red sat at 0.020, which is the solid blue flat
    // that was reported. A gain does.
    const flat = ctx._channelGains(ep(10204, 381), ep(28228, 439), ep(65534, 3), MAXV);
    const fr = shown(flat.r, 10204), fg = shown(flat.g, 28228), fb = shown(flat.b, 65534);
    near(fr, fg, 0.01, 'flat: red lands where green does');
    near(fb, fg, 0.01, 'flat: blue lands where green does');
    (Math.max(fr, fg, fb) < 0.5)
        ? ok(`flat renders as a mid grey (${fg.toFixed(3)}), not a blown channel`)
        : bad(`flat still blows out at ${Math.max(fr, fg, fb).toFixed(3)}`);

    // A sky background: the case the old offset model did handle. It must keep
    // working, or every light frame regresses to fix a flat.
    const sky = ctx._channelGains(ep(4000, 120), ep(5000, 130), ep(12000, 150), MAXV);
    const sr = shown(sky.r, 4000), sg = shown(sky.g, 5000), sb = shown(sky.b, 12000);
    near(sr, sg, 0.01, 'sky: red neutral against green');
    near(sb, sg, 0.01, 'sky: blue neutral against green');

    // A bias: a few ADU of offset between channels, tiny MAD. This is the
    // "bias is all pink" case, and it must come out neutral too.
    const bias = ctx._channelGains(ep(500, 4), ep(505, 4), ep(495, 4), MAXV);
    const br = shown(bias.r, 500), bg = shown(bias.g, 505), bb = shown(bias.b, 495);
    near(br, bg, 0.005, 'bias: red neutral');
    near(bb, bg, 0.005, 'bias: blue neutral');

    // A dead plane must not drag the others through the floor.
    const dead = ctx._channelGains(ep(0, 0), ep(5000, 130), ep(5100, 130), MAXV);
    (Number.isFinite(dead.r.shadow) && Number.isFinite(dead.r.scale) && dead.r.scale > 0)
        ? ok('a dead channel yields finite endpoints') : bad('dead channel produced garbage');

    (flat.midtone > 0 && flat.midtone < 1) ? ok('the auto midtone stays in range')
                                           : bad(`midtone ${flat.midtone}`);
}

console.log('== stage 2 is the handles, and it starts as the identity ==');
{
    const st = { _mtf: app._mtf, _screenTransfer: app._screenTransfer,
                 stretchBlack: 0, stretchWhite: 1, stretchMid: 0.5 };
    for (const v of [0, 0.15, 0.5, 0.83, 1]) {
        near(st._screenTransfer(v), v, 1e-9, `identity at ${v}`);
    }
    // Raising the black point clips the bottom and rescales what is left DOWN.
    // It is the contrast control, not the brightness one.
    st.stretchBlack = 0.1;
    (st._screenTransfer(0.3) < 0.3) ? ok('raising black darkens what remains')
                                    : bad('raising black should not brighten');
    (st._screenTransfer(0.05) === 0) ? ok('and clips what falls below it')
                                     : bad('below-black did not clip');

    // The two that brighten a faint frame, which is what the handles are for:
    // pull the white point in, or bend the midtone down.
    st.stretchBlack = 0; st.stretchWhite = 0.5;
    (st._screenTransfer(0.3) > 0.3) ? ok('pulling white in brightens')
                                    : bad('white point did not brighten');
    st.stretchWhite = 1; st.stretchMid = 0.25;
    (st._screenTransfer(0.25) > 0.25) ? ok('a lower midtone lifts the shadows')
                                      : bad('midtone did nothing');
}

console.log('== the handles ARE the axis ==');
{
    const st = {
        HISTO_BINS: NB, histoZoom: false,
        stretchBlack: 0.2, stretchWhite: 0.9, stretchMid: 0.5,
        histo: { bins: fieldBins(), color: false, binsR: null, dispLo: 0, dispHi: 1 },
        _histoDrag: null,
        _histoBulkOf: app._histoBulkOf,
        _histoFrame: app._histoFrame,
        _histoUpdateEndpoints: app._histoUpdateEndpoints,
    };
    st._histoUpdateEndpoints();
    near(st.histo.blackFrac, 0.2, 1e-9, 'black handle is stretchBlack, unconverted');
    near(st.histo.whiteFrac, 0.9, 1e-9, 'white handle is stretchWhite, unconverted');
    near(st.histo.midFrac, 0.2 + 0.5 * 0.7, 1e-9, 'mid sits between the two');
}

console.log('== the built histogram has no comb of zeros ==');
{
    // A real frame is 16-bit INTEGERS, and the stretch pulls a sky occupying a
    // couple of hundred distinct levels across the whole axis. Counting each
    // sample as a point put most of them in the same handful of bins and left
    // the ones between empty, so the curve drew as a picket fence of spikes
    // separated by zeros -- which reads as missing data and is nothing of the
    // sort. Every sample is spread over the width its ADU step covers instead.
    const W = 220, H = 220, N = W * H;
    const px = new Uint16Array(N * 3);
    let seed = 12345;
    const rnd = () => { seed = (seed * 1103515245 + 12345) & 0x7fffffff; return seed / 0x7fffffff; };
    const gauss = (mu, sd) => {
        const u = Math.max(1e-9, rnd()), v = rnd();
        return Math.round(mu + sd * Math.sqrt(-2 * Math.log(u)) * Math.cos(2 * Math.PI * v));
    };
    // The shape of the frame that produced the comb: a background at ~180 ADU
    // with a spread of a couple of dozen, so the whole sky is a HUNDRED-odd
    // distinct integer values. The stretch pulls those across four hundred
    // bins, and point counting leaves five of every six empty. A frame with a
    // wide background does not show the defect at all, which is why this
    // fixture is narrow on purpose.
    for (let i = 0; i < N; i++) {
        px[i] = Math.max(0, gauss(150, 22));
        px[N + i] = Math.max(0, gauss(180, 24));
        px[2 * N + i] = Math.max(0, gauss(310, 30));
        // A few stars, so the frame is not pure background.
        if ((i % 4093) === 0) {
            px[i] += 20000; px[N + i] += 22000; px[2 * N + i] += 18000;
        }
    }

    const app2 = {
        HISTO_BINS: 512, histoZoom: true,
        stretchBlack: 0, stretchWhite: 1, stretchMid: 0.5,
        _histoToken: 1, _histoDrag: null, _histoMap: null, _histoLut: null,
        histo: { bins: null, _token: -1, _sig: null, dispLo: 0, dispHi: 1 },
        _lastRawFrame: { pixels: px, width: W, height: H, bitDepth: 16,
                         bayerPattern: 0, channels: 3, maxVal: 65535 },
        _mtf: app._mtf,
        _screenTransfer: app._screenTransfer,
        _autoStretchEndpoints: app._autoStretchEndpoints,
        _channelGains: app._channelGains,
        _computePerChannelStretch: app._computePerChannelStretch,
        _displayMap: app._displayMap,
        _histoLuts: app._histoLuts,
        _histoStretchSig: app._histoStretchSig,
        _histoBulkOf: app._histoBulkOf,
        _histoFrame: app._histoFrame,
        _histoUpdateEndpoints: app._histoUpdateEndpoints,
        _histoBuild: app._histoBuild,
    };

    app2._histoBuild() ? ok('the histogram builds') : bad('build returned false');
    app2.histo.color ? ok('a 3-plane frame is colour') : bad('colour not detected');

    // Bin 0 is the clip pile: everything below the black point renders as
    // black, so it is a real spike with a real gap after it. The comb this
    // guards against is inside the DISTRIBUTION, so measure from bin 1 and
    // ignore the sparse tails, where isolated samples are honest.
    const holes = (bins, label) => {
        let total = 0;
        for (let i = 1; i < bins.length; i++) total += bins[i];
        if (total <= 0) { bad(`${label}: no bins at all`); return; }
        let acc = 0, first = -1, last = -1;
        for (let i = 1; i < bins.length; i++) {
            acc += bins[i];
            if (first < 0 && acc >= total * 0.005) first = i;
            if (acc <= total * 0.995) last = i;
        }
        let run = 0, worst = 0;
        for (let i = first; i <= last; i++) {
            run = bins[i] > 0 ? 0 : run + 1;
            if (run > worst) worst = run;
        }
        (worst === 0)
            ? ok(`${label}: ${last - first + 1} bins across the bulk, none empty`)
            : bad(`${label}: a run of ${worst} empty bins inside the distribution`);
    };
    holes(app2.histo.binsR, 'R');
    holes(app2.histo.binsG, 'G');
    holes(app2.histo.binsB, 'B');

    // And the whole point of the gain balance: the three curves land together.
    // The MEDIAN bin, not the mode. The gain balance equalises the channels'
    // medians by construction; their widths still differ (a gain scales the
    // spread along with the level) and the MTF is steep near the black point,
    // so the mode of the binned density is not the statistic being controlled.
    // Bin 0 is excluded: it is the clip pile, not part of the distribution.
    const medianBinOf = (bins) => {
        let total = 0;
        for (let i = 1; i < bins.length; i++) total += bins[i];
        let acc = 0;
        for (let i = 1; i < bins.length; i++) {
            acc += bins[i];
            if (acc >= total / 2) return i;
        }
        return bins.length - 1;
    };
    const pr = medianBinOf(app2.histo.binsR), pg = medianBinOf(app2.histo.binsG),
          pb = medianBinOf(app2.histo.binsB);
    (Math.abs(pr - pg) <= 8 && Math.abs(pb - pg) <= 8)
        ? ok(`the three channels sit on top of each other (${pr}, ${pg}, ${pb})`)
        : bad(`channels apart: R ${pr}, G ${pg}, B ${pb}`);

    // The stats are the exposure's, in ADU, not the screen's.
    (app2.histo.binsG[0] > 0) ? ok('what falls below the black point piles at zero')
                              : bad('nothing clipped, so the black point is outside the data');

    // In ADU, and therefore near the fixture's background, NOT near the screen
    // level the same pixels are drawn at.
    (app2.histo.avg > 120 && app2.histo.avg < 400)
        ? ok(`AVG is in ADU (${app2.histo.avg})`)
        : bad(`AVG looks wrong: ${app2.histo.avg}`);

    // Moving a handle must rebuild, and only then.
    const sig = app2.histo._sig;
    app2._histoBuild();
    (app2.histo._sig === sig) ? ok('a rebuild with nothing changed is skipped')
                              : bad('rebuilt for no reason');
    app2.stretchMid = 0.3;
    app2._histoBuild();
    (app2.histo._sig !== sig) ? ok('moving a handle rebuilds the bins')
                              : bad('handle move did not rebuild');
}

console.log();
console.log(fails === 0 ? 'all checks passed' : `${fails} check(s) failed`);
process.exit(fails === 0 ? 0 : 1);
