// N.I.N.A. Polaris — Canopus Assistant
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License, version 3 or later.
// Distributed WITHOUT ANY WARRANTY. <https://www.gnu.org/licenses/>.
//
// Node test for the device-tier pieces: the provider-local tool-call parser and
// the in-browser agent loop (plan-approval + tool bridge), with no browser and no
// network. Mirrors server/test_agent.py + test_providers_local.py.
//
//   node canopus/client/test-agent-device.js

global.window = {};
require('./provider-local.js');
require('./agent.js');
const CanopusAgent = window.CanopusAgent;
const parse = window.canopusParseToolCallText;

let failures = 0;
const eq = (a, b) => JSON.stringify(a) === JSON.stringify(b);
function assert(cond, msg) { if (!cond) { console.log('FAIL:', msg); failures++; } else { console.log('ok:', msg); } }

// ---- parser ----
assert(eq(parse('<think>hmm</think><tool_call>{"name":"get_status","arguments":{}}</tool_call>'), { name: 'get_status', arguments: {} }), 'parse tagged after <think>');
assert(eq(parse('sure {"name":"search_catalog","arguments":{"query":"M42"}} ok'), { name: 'search_catalog', arguments: { query: 'M42' } }), 'parse bare json');
assert(parse('Guiding looks fine.') === null, 'parse plain prose -> null');

// ---- agent loop ----
const CATALOG = { tools: [
  { name: 'get_status', description: 'Get the current rig status.', parameters: { type: 'object', properties: {} }, requiresApproval: false, polaris: { method: 'GET', path: '/api/system/status' } },
  { name: 'slew_to', description: 'Slew to a named target.', parameters: { type: 'object', properties: { target: { type: 'string' } } }, requiresApproval: true, polaris: { method: 'POST', path: '/api/sky/slew-and-center' } },
]};

function makeHarness(script) {
  const out = [];
  let agent;
  const send = (m) => {
    out.push(m);
    if (m.type === 'tool-call' || m.type === 'ui') {
      // Stand in for the browser bridge → Polaris host → result.
      agent.onMessage({ type: 'tool-result', id: m.id, ok: true, result: { stub: true } });
    }
  };
  const provider = { i: 0, async complete() { return script[this.i++] || { text: 'done', toolCalls: [] }; } };
  agent = new CanopusAgent(provider, CATALOG, { send });
  return { agent, out };
}

(async () => {
  // Read tool: user -> tool-call(get_status) -> assistant -> done.
  const h1 = makeHarness([{ text: null, toolCalls: [{ id: '1', name: 'get_status', arguments: {} }] }, { text: 'All good — guiding is running.', toolCalls: [] }]);
  await h1.agent.onMessage({ type: 'user', text: 'how is it going?' });
  const t1 = h1.out.map(m => m.type);
  assert(t1.includes('tool-call'), 'read: emits a tool-call');
  const tc1 = h1.out.find(m => m.type === 'tool-call');
  assert(tc1 && tc1.path === '/api/system/status', 'read: correct tool path');
  assert(h1.out.some(m => m.type === 'assistant'), 'read: assistant reply');
  assert(t1[t1.length - 1] === 'done', 'read: ends with done');

  // Mutating tool: must PLAN first, not act.
  const h2 = makeHarness([{ text: null, toolCalls: [{ id: '1', name: 'slew_to', arguments: { target: 'M16' } }] }, { text: 'Slewing.', toolCalls: [] }]);
  await h2.agent.onMessage({ type: 'user', text: 'slew to M16' });
  assert(h2.out.some(m => m.type === 'plan'), 'mutate: emits a plan');
  assert(!h2.out.some(m => m.type === 'tool-call'), 'mutate: no tool-call before approval');
  const plan = h2.out.find(m => m.type === 'plan');
  assert(plan.steps[0].mutates === true, 'mutate: plan step flagged mutates');
  await h2.agent.onMessage({ type: 'approve', planId: plan.planId });
  assert(h2.out.some(m => m.type === 'tool-call' && m.path === '/api/sky/slew-and-center'), 'mutate: executes on approve');

  // Reject: never calls the tool.
  const h3 = makeHarness([{ text: null, toolCalls: [{ id: '1', name: 'slew_to', arguments: { target: 'M16' } }] }, { text: 'Okay, standing by.', toolCalls: [] }]);
  await h3.agent.onMessage({ type: 'user', text: 'point somewhere' });
  const plan3 = h3.out.find(m => m.type === 'plan');
  await h3.agent.onMessage({ type: 'reject', planId: plan3.planId });
  assert(!h3.out.some(m => m.path === '/api/sky/slew-and-center'), 'reject: never slews');

  console.log(failures ? ('\n' + failures + ' FAILURE(S)') : '\nALL PASS');
  process.exit(failures ? 1 : 0);
})();
