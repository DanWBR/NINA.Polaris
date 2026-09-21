// Two checks over wwwroot/js/app.js:
//
//   1. `this.foo(...)` calls where `foo` is never defined anywhere.
//   2. two members of the same name in the same object literal, where the
//      later one silently wins.
//
//   node scripts/check-missing-methods.mjs
//
// app.js is one big object literal, so a renamed helper leaves the old call
// site compiling fine and failing only when a user presses the button. That
// shipped once: the Studio solve card called `this._pollSlewCenter()` while
// the method had always been `pollSlewCenter()`, so SLEW and SLEW & CENTER
// died with "this._pollSlewCenter is not a function" (field report, RPi 5,
// 2026-09-05).
//
// Deliberately lenient: any `name() {`, `async name() {`, `name: function`,
// `name: (a) =>`, `name: async` or `this.name = ...` ANYWHERE in the file counts
// as a definition, including inside nested object literals, and a call the
// author guarded with `if (this.name)` / `this.name &&` / `this.name?.()` is
// treated as an optional hook. It cannot prove a call resolves, only that the
// name exists somewhere, which is enough to catch a typo or a half-finished
// rename without drowning the output in false positives.
//
// The duplicate check is the other half of the same problem. A JS object
// literal accepts the same key twice without a murmur and keeps the last one,
// so in a 48k-line literal a second definition is invisible. Three shipped
// that way: `openSettingsCard` (the survivor scrolled to a settings card
// without expanding it, so links landed on a shut card), `cameraIso` (a dead
// `800` under the object the DSLR ISO list actually uses) and `weather` (the
// forecast state shadowed the weather-station state, and the status handler's
// wholesale `this.weather = {...}` then wiped the forecast on every tick).

import { readFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const FILE = 'src/NINA.Polaris/wwwroot/js/app.js';
const src = readFileSync(resolve(ROOT, FILE), 'utf8');

const defined = new Set();
// name() {  /  async name() {  /  *name() {
for (const m of src.matchAll(/(?:^|\s)(?:async\s+|\*\s*)?([A-Za-z_$][\w$]*)\s*\([^()]*\)\s*\{/g)) {
    defined.add(m[1]);
}
// name: function  /  name: async function  /  name: (a, b) =>  /  name: async (a) =>
for (const m of src.matchAll(/([A-Za-z_$][\w$]*)\s*:\s*(?:async\s+)?(?:function\b|\([^()]*\)\s*=>|[A-Za-z_$][\w$]*\s*=>)/g)) {
    defined.add(m[1]);
}
// this.name = anything: callback slots filled at runtime are legitimate call
// targets too (this._confirmResolver = resolve, picker.onChange = ...)
for (const m of src.matchAll(/\bthis\.([A-Za-z_$][\w$]*)\s*=[^=]/g)) {
    defined.add(m[1]);
}

// Blank out comments and string bodies (keeping newlines so line numbers still
// match) before looking for calls: a comment that NAMES the bug, or a URL with
// "//" in it, must not be read as code.
function stripCommentsAndStrings(text) {
    const out = [];
    let i = 0, quote = null;
    while (i < text.length) {
        const c = text[i], next = text[i + 1];
        if (quote) {
            if (c === '\\') { out.push(' ', ' '); i += 2; continue; }
            if (c === quote) { quote = null; out.push(c); i++; continue; }
            out.push(c === '\n' ? '\n' : ' '); i++; continue;
        }
        if (c === '/' && next === '/') {
            while (i < text.length && text[i] !== '\n') { out.push(' '); i++; }
            continue;
        }
        if (c === '/' && next === '*') {
            const end = text.indexOf('*/', i + 2);
            const stop = end === -1 ? text.length : end + 2;
            for (; i < stop; i++) out.push(text[i] === '\n' ? '\n' : ' ');
            continue;
        }
        if (c === '"' || c === "'" || c === '`') { quote = c; out.push(c); i++; continue; }
        out.push(c); i++;
    }
    return out.join('');
}

const srcLines = src.split('\n');
const lines = stripCommentsAndStrings(src).split('\n');
// ---- check 2: two members of the same name in the same object literal ----
//
// Siblings are found by indentation rather than by counting braces: app.js is
// uniformly indented one member per line, while a brace count is thrown off by
// a regex literal such as /^\{/ and would then report nonsense. Two members
// are siblings when they sit at the same indent under the same parent line
// (the nearest line above them that is indented less).
//
// Only definitions count. A method needs a `{` after its parameter list, so
// a call like `settle();` on its own line is not read as one, whether the body
// runs on to the next line or closes on this one. A property has to be
// `name:`. Language keywords are skipped, so `if (x) {`, `} else if (x) {` and
// `default:` do not qualify, and `function f() {` never matches because the
// name does not sit at the start of the line. Accessors are skipped as well: a
// `get x()` beside a `set x()` is legitimate, and the price is not catching
// the same accessor written twice. Members written several to a line are not
// seen, which is the same kind of leniency as the check above.
const METHOD_DEF = /^(?:async\s+|\*\s*)?([A-Za-z_$][\w$]*)\s*\([^()]*\)\s*\{/;
const PROP_DEF = /^([A-Za-z_$][\w$]*)\s*:/;
const NOT_A_MEMBER = new Set(['if', 'for', 'while', 'switch', 'catch', 'function',
    'return', 'else', 'do', 'try', 'finally', 'case', 'default', 'new', 'typeof',
    'await', 'class', 'const', 'let', 'var', 'get', 'set', 'static', 'delete',
    'void', 'in', 'of', 'yield', 'throw']);

const indents = [];
const members = [];
for (const [i, line] of lines.entries()) {
    if (!line.trim()) { indents.push(null); continue; }
    indents.push(line.length - line.trimStart().length);
    const body = line.trim();
    const m = body.match(METHOD_DEF) ?? body.match(PROP_DEF);
    if (!m || NOT_A_MEMBER.has(m[1])) continue;
    members.push({ line: i, name: m[1], indent: indents[i] });
}

/** The nearest line above `at` that is indented less: the line that opened
 *  whatever object or block this member sits in. */
function parentLine(at, indent) {
    for (let j = at - 1; j >= 0; j--) {
        if (indents[j] !== null && indents[j] < indent) return j;
    }
    return -1;
}

const firstSeen = new Map();
const duplicates = [];
for (const mem of members) {
    const key = `${parentLine(mem.line, mem.indent)}|${mem.indent}|${mem.name}`;
    if (firstSeen.has(key)) duplicates.push({ ...mem, first: firstSeen.get(key) });
    else firstSeen.set(key, mem.line);
}

// ---- check 1: calls with no definition ----
const missing = [];
for (const [i, line] of lines.entries()) {
    for (const m of line.matchAll(/\bthis\.([A-Za-z_][\w$]*)\s*\(/g)) {
        const name = m[1];
        if (defined.has(name)) continue;
        // A call the author already guarded (if (this.x), this.x && ..., this.x?.())
        // is an optional hook, not a missing method.
        const guard = new RegExp(`(if\\s*\\(\\s*!?this\\.${name}\\b|this\\.${name}\\s*&&|this\\.${name}\\?\\.)`);
        if (guard.test(line)) continue;
        missing.push({ line: i + 1, name, text: srcLines[i].trim().slice(0, 120) });
    }
}

if (duplicates.length > 0) {
    console.error(`${duplicates.length} member(s) defined twice in the same object `
        + `literal in ${FILE} (the later one wins, the earlier one is dead):`);
    console.error('');
    for (const d of duplicates) {
        console.error(`  ${d.name}`);
        console.error(`    ${FILE}:${d.first + 1}  ${srcLines[d.first].trim().slice(0, 100)}`);
        console.error(`    ${FILE}:${d.line + 1}  ${srcLines[d.line].trim().slice(0, 100)}`);
    }
    if (missing.length > 0) console.error('');
}

if (missing.length > 0) {
    console.error(`${missing.length} call(s) with no definition in ${FILE}:`);
    console.error('');
    for (const m of missing) {
        console.error(`  ${FILE}:${m.line}  this.${m.name}()`);
        console.error(`    ${m.text}`);
    }
}

if (missing.length > 0 || duplicates.length > 0) process.exit(1);

console.log(`OK: every this.x() call in ${FILE} has a matching definition, and no `
    + `object literal defines the same member twice (${members.length} members checked).`);
process.exit(0);
