// Guard against reading a body field off a fetch Response.
//
//   node scripts/check-response-misuse.mjs
//
// app.js has two families of API helpers: apiGet / apiPostJson / apiPutJson
// return the PARSED body, while apiFetch / apiPost / apiPut return the raw
// Response. Mixing them up is silent, which is what makes it worth a checker:
//
//   const rig = await this.apiPost('/api/equipment/rigs', { name });
//   this.rigs.push(rig);            // a Response goes into the list
//   toast(`Created rig: ${rig.name}`);   // "Created rig: undefined"
//
// Nothing throws. The value is undefined and the UI carries on, so the bug
// surfaces later and somewhere else: in the field report that prompted this,
// as a DELETE to /api/equipment/rigs/undefined answered 400. Twenty-three call
// sites had it.
//
// The rule: if a variable assigned from apiPost/apiPut/apiFetch is then read
// for anything that is not a Response member, it wanted the body. Exits 1 with
// the offending lines.
import { readFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const FILE = resolve(ROOT, 'src/NINA.Polaris/wwwroot/js/app.js');

// Everything a real Response legitimately offers.
const RESPONSE_MEMBERS = new Set([
    'json', 'text', 'blob', 'arrayBuffer', 'formData', 'clone',
    'ok', 'status', 'statusText', 'headers', 'url', 'redirected',
    'type', 'body', 'bodyUsed',
]);

const LOOKAHEAD = 8;   // lines of scope to inspect after the assignment

const src = readFileSync(FILE, 'utf8').split(/\r?\n/);
const assign = /(?:const|let|var)\s+(\w+)\s*=\s*await\s+this\.(apiFetch|apiPost|apiPut)\(/;

const findings = [];
for (let i = 0; i < src.length; i++) {
    const m = assign.exec(src[i]);
    if (!m) continue;
    const [, name, helper] = m;
    const window = src.slice(i, i + LOOKAHEAD).join('\n');
    const used = [...window.matchAll(new RegExp(`\\b${name}\\s*\\??\\.\\s*(\\w+)`, 'g'))]
        .map((x) => x[1]);
    const bodyish = [...new Set(used)].filter((u) => !RESPONSE_MEMBERS.has(u));
    if (bodyish.length) {
        findings.push({ line: i + 1, name, helper, fields: bodyish, code: src[i].trim() });
    }
}

if (!findings.length) {
    console.log('check-response-misuse: clean');
    process.exit(0);
}

console.error(`check-response-misuse: ${findings.length} site(s) read a body field off a Response.`);
console.error('Use apiGet / apiPostJson / apiPutJson when you want the parsed body.\n');
for (const f of findings) {
    console.error(`  app.js:${f.line}  ${f.name} = await this.${f.helper}(...)  ->  reads .${f.fields.join(', .')}`);
    console.error(`    ${f.code}`);
}
process.exit(1);
