// Report `this.foo(...)` calls in wwwroot/js/app.js where `foo` is never
// defined anywhere in the file.
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

if (missing.length === 0) {
    console.log(`OK: every this.x() call in ${FILE} has a matching definition.`);
    process.exit(0);
}

console.error(`${missing.length} call(s) with no definition in ${FILE}:\n`);
for (const m of missing) {
    console.error(`  ${FILE}:${m.line}  this.${m.name}()`);
    console.error(`    ${m.text}`);
}
process.exit(1);
