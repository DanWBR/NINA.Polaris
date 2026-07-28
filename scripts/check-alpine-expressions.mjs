// Compile every Alpine expression in wwwroot/index.html the way Alpine does
// (new AsyncFunction) and report the ones with a syntax error.
//
//   node scripts/check-alpine-expressions.mjs
//
// Alpine reports these at RUNTIME, as one "Uncaught SyntaxError: Invalid or
// unexpected token" from deep inside alpine.min.js with no hint of which
// attribute is at fault -- a stray apostrophe in an x-text shipped this way.
// Two known false-positive sources are handled: HTML entities (Alpine sees the
// decoded text) and statement-bodied attributes (@handlers, x-effect, x-init).
// Matches inside HTML comments are still reported; check the line before
// believing one.

import { readFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const raw = readFileSync(resolve(ROOT, 'src/NINA.Polaris/wwwroot/index.html'), 'utf8');
// Blank out HTML comments (keeping newlines so reported line numbers still
// match the file): this file documents Alpine attributes inside comments, and
// the browser never compiles those.
const html = raw.replace(/<!--[\s\S]*?-->/g, (c) => c.replace(/[^\n]/g, ' '));

const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
const decode = (s) => s.replace(/&amp;/g, '&').replace(/&lt;/g, '<')
                       .replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&#39;/g, "'");
const re = /\s(x-(?:show|text|model[^=]*|if|for|effect|init|html|bind:[^=\s]+)|[:@][a-zA-Z0-9_.:-]+)="([^"]*)"/g;
let m, bad = 0, total = 0;
while ((m = re.exec(html))) {
  const [, attr, raw] = m;
  const expr = decode(raw);
  if (!expr.trim()) continue;
  total++;
  const statementish = attr.startsWith('@') || attr === 'x-effect' || attr === 'x-init';
  const src = attr === 'x-for' ? (expr.split(/\s+in\s+/).pop() || expr) : expr;
  try {
    new AsyncFunction('$data', statementish ? src : `return (${src})`);
  } catch (e) {
    bad++;
    const line = html.slice(0, m.index).split('\n').length;
    console.log(`index.html:${line}  ${attr}  -> ${e.message}`);
    console.log(`   ${expr.replace(/\s+/g, ' ').slice(0, 130)}`);
  }
}
console.log(`\n${total} expressions checked, ${bad} with a syntax error`);
