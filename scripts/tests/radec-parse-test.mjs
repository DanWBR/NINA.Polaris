// Tests for the typed-coordinates parser behind "GoTo RA/Dec" (issue #23),
// lifted straight out of app.js the way histo-math-test.mjs does it.
//
//   node scripts/tests/radec-parse-test.mjs
//
// What is guarded here: every format the issue asked for, the hours-versus-
// degrees rule for a bare decimal RA, the sky-search line splitter telling a
// coordinate pair from an object name, and the round trip through the
// formatter.

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
    (a != null && Math.abs(a - b) <= eps) ? ok(m) : bad(`${m}: ${a} vs ${b} (tol ${eps})`);

function lift(name) {
    const start = src.indexOf(`\n        ${name}(`);
    if (start < 0) throw new Error(`not found in app.js: ${name}`);
    const end = src.indexOf('\n        },', start);
    if (end < 0) throw new Error(`unterminated: ${name}`);
    return src.slice(start + 1, end + '\n        },'.length).replace(/\r/g, '');
}

const NAMES = ['_parseSexagesimal', 'parseRaDec', 'parseRaDecLine', 'formatRaDec'];
const app = new Function('return {\n' + NAMES.map(lift).join('\n') + '\n};')();

console.log('single coordinates');
near(app._parseSexagesimal('05:35:17.3'), 5.588139, 1e-5, 'HH:MM:SS.s');
near(app._parseSexagesimal('5h35m17s'), 5.588056, 1e-5, 'HhMMmSSs');
near(app._parseSexagesimal('05 35 17'), 5.588056, 1e-5, 'space separated');
near(app._parseSexagesimal('-05:23:28'), -5.391111, 1e-5, 'negative Dec');
near(app._parseSexagesimal("-05°23'28\""), -5.391111, 1e-5, 'degree/arcmin/arcsec marks');
near(app._parseSexagesimal('-5d23m28s'), -5.391111, 1e-5, 'd m s letters');
near(app._parseSexagesimal('+41 16'), 41.266667, 1e-5, 'two parts, explicit plus');
near(app._parseSexagesimal('83.82'), 83.82, 1e-9, 'decimal');
near(app._parseSexagesimal('-5.39'), -5.39, 1e-9, 'negative decimal');
app._parseSexagesimal('05:60:00') === null ? ok('minutes >= 60 rejected') : bad('05:60:00 accepted');
app._parseSexagesimal('abc') === null ? ok('letters rejected') : bad('abc accepted');
app._parseSexagesimal('') === null ? ok('empty rejected') : bad('empty accepted');

console.log('pairs: the formats the issue asked for');
let r = app.parseRaDec('05:35:17.3', '-05:23:28', false);
near(r.raHours, 5.588139, 1e-5, 'M42 sexagesimal RA hours');
near(r.decDeg, -5.391111, 1e-5, 'M42 sexagesimal Dec');
r = app.parseRaDec('83.82', '-5.39', true);
near(r.raHours, 83.82 / 15, 1e-9, 'decimal degrees RA, told it is degrees');
r = app.parseRaDec('5.59', '-5.39', false);
near(r.raHours, 5.59, 1e-9, 'decimal RA is hours by default, like every RA field here');
r = app.parseRaDec('83.82d', '-5.39', false);
near(r.raHours, 83.82 / 15, 1e-9, 'a trailing d overrides the default to degrees');
r = app.parseRaDec('5.59h', '-5.39', true);
near(r.raHours, 5.59, 1e-9, 'a trailing h overrides the default to hours');
r = app.parseRaDec('5h35m17s', '-5d23m28s', true);
near(r.raHours, 5.588056, 1e-5, 'sexagesimal RA is hours regardless of the decimal setting');
r = app.parseRaDec('25:00:00', '0', false);
r.error ? ok('RA past 24h refused: ' + r.error) : bad('RA 25h accepted');
r = app.parseRaDec('12', '95', false);
r.error ? ok('Dec past 90 refused') : bad('Dec 95 accepted');
r = app.parseRaDec('nope', '1', false);
r.error ? ok('garbage refused with a message') : bad('garbage accepted');

console.log('the search line: coordinates or a name?');
const line = (q) => app.parseRaDecLine(q);
r = line('05:35:17.3 -05:23:28'); r ? near(r.decDeg, -5.391111, 1e-5, 'colon pair') : bad('colon pair not recognised');
r = line('5h35m17s -5d23m28s'); r ? near(r.raHours, 5.588056, 1e-5, 'letter pair') : bad('letter pair not recognised');
r = line('05 35 17 -05 23 28'); r ? near(r.raHours, 5.588056, 1e-5, 'space pair with sign') : bad('space pair not recognised');
r = line('05 35 17 05 23 28'); r ? near(r.decDeg, 5.391111, 1e-5, 'space pair without sign splits in the middle') : bad('unsigned space pair not recognised');
r = line('83.82, -5.39'); r ? near(r.raHours, 83.82 / 15, 1e-9, 'comma pair, a decimal RA past 24 can only be degrees') : bad('comma pair not recognised');
r = line('5.59 -5.39'); r ? near(r.raHours, 5.59, 1e-9, 'a decimal RA under 24 stays hours') : bad('small decimal pair not recognised');
r = line('05:35:17, -05:23:28'); r ? ok('comma with sexagesimal') : bad('comma sexagesimal not recognised');
for (const name of ['M31', 'NGC 7000', 'Orion', 'Vega', 'Sirius', 'Mars', 'Sh2-155', 'Abell 39', '']) {
    line(name) === null ? ok(`"${name}" is a name, not coordinates`) : bad(`"${name}" taken as coordinates`);
}

console.log('formatting');
const f = app.formatRaDec(5.588139, -5.391111);
f === '05h35m17.3s -05°23′28″' ? ok('M42 formats as ' + f) : bad('format: ' + f);
const f2 = app.formatRaDec(0.712, 41.27);
f2.startsWith('00h42m43') && f2.includes('+41°16′12″') ? ok('M31 formats as ' + f2) : bad('format: ' + f2);

console.log(fails === 0 ? '\nall passed' : `\n${fails} FAILED`);
process.exit(fails === 0 ? 0 : 1);
