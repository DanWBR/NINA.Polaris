// N.I.N.A. Polaris — Canopus Assistant
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option) any
// later version. Distributed WITHOUT ANY WARRANTY; see the GNU AGPL for details.
// <https://www.gnu.org/licenses/>.
//
// LocalOpenAIProvider — the "On this device" LLM backend. It runs IN THE BROWSER
// and talks to an OpenAI-compatible server the user runs on their own machine
// (Ollama / LM Studio / llama.cpp), using the machine's native GPU. This is the
// JS twin of the server-side providers_local.py LlamaServerProvider: it posts
// OpenAI-style messages + tool specs and returns assistant text OR tool calls,
// parsing either native `tool_calls` or Qwen's `<tool_call>{json}</tool_call>`.
//
// Exposes window.LocalOpenAIProvider (the client has no module bundler).

(function () {
  'use strict';

  // ---- tool-call parsing (JS port of providers_local.py) ----
  const FENCE = /```(?:json)?\s*(\{[\s\S]*?\})\s*```/;
  const TAGGED = /<tool_call>\s*(\{[\s\S]*?\})\s*<\/tool_call>/;
  const THINK = /<think>[\s\S]*?<\/think>/g;
  const THINK_OPEN = /<think>[\s\S]*$/;

  function stripThinking(text) {
    return (text || '').replace(THINK, '').replace(THINK_OPEN, '').trim();
  }

  // Parse a possibly-trailing-prose JSON object, balancing braces.
  function loadJson(s) {
    try { return JSON.parse(s); } catch (_) { /* trailing prose */ }
    let depth = 0;
    for (let i = 0; i < s.length; i++) {
      if (s[i] === '{') depth++;
      else if (s[i] === '}') {
        depth--;
        if (depth === 0) {
          try { return JSON.parse(s.slice(0, i + 1)); } catch (_) { return null; }
        }
      }
    }
    return null;
  }

  // { name, arguments } and the OpenAI { function: {...} } wrapper.
  function normalize(obj) {
    if (!obj || typeof obj !== 'object') return null;
    const fn = (obj.function && typeof obj.function === 'object') ? obj.function : obj;
    const name = fn.name;
    if (typeof name !== 'string') return null;
    let args = fn.arguments !== undefined ? fn.arguments : (fn.parameters || {});
    if (typeof args === 'string') args = loadJson(args) || {};
    return { name, arguments: (args && typeof args === 'object') ? args : {} };
  }

  // Extract a (name, arguments) tool call from a model's free-text output.
  function parseToolCallText(text) {
    text = stripThinking(text);
    for (const pat of [TAGGED, FENCE]) {
      const m = pat.exec(text);
      if (m) {
        const call = normalize(loadJson(m[1]));
        if (call) return call;
      }
    }
    // Bare JSON object somewhere in the reply.
    let start = text.indexOf('{');
    while (start !== -1) {
      const call = normalize(loadJson(text.slice(start)));
      if (call) return call;
      start = text.indexOf('{', start + 1);
    }
    return null;
  }

  class LocalOpenAIProvider {
    // url: OpenAI-compatible base, e.g. http://localhost:11434/v1  (Ollama)
    constructor(url, model) {
      this.base = (url || 'http://localhost:11434/v1').replace(/\/+$/, '');
      this.model = model || 'qwen2.5:7b';
      this.maxTokens = 1024;
    }

    // messages: OpenAI messages; tools: OpenAI function specs.
    // returns { text, toolCalls: [{id,name,arguments}], usage }
    async complete(messages, tools) {
      const body = {
        model: this.model,
        messages,
        tools,
        tool_choice: 'auto',
        temperature: 0,
        stream: false,
        max_tokens: this.maxTokens,
        // Qwen: keep thinking off via the chat template; harmless if ignored.
        chat_template_kwargs: { enable_thinking: false },
      };
      let r;
      try {
        r = await fetch(this.base + '/chat/completions', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(body),
        });
      } catch (e) {
        // Connection refused / CORS / DNS: the local server isn't reachable.
        throw new Error(
          "can't reach the local model at " + this.base + '. Is your LLM server '
          + '(Ollama / LM Studio / llama.cpp) running, and is CORS allowed for this '
          + 'page? (' + (e && e.message ? e.message : e) + ')');
      }
      if (!r.ok) {
        let detail = '';
        try { const j = await r.json(); detail = (j.error && (j.error.message || j.error)) || j.message || ''; }
        catch (_) { try { detail = (await r.text()).slice(0, 300); } catch (__) {} }
        throw new Error('local model error (HTTP ' + r.status + '): ' + detail);
      }
      const data = await r.json();
      const choice = (data.choices && data.choices[0]) || {};
      const msg = choice.message || {};

      // 1) Native OpenAI tool_calls.
      const native = msg.tool_calls || [];
      if (native.length) {
        const calls = [];
        native.forEach((tc, i) => {
          const fn = tc.function || {};
          if (!fn.name) return;
          let args = fn.arguments;
          if (typeof args === 'string') args = loadJson(args) || {};
          else if (!args || typeof args !== 'object') args = {};
          calls.push({ id: tc.id || ('local-' + (i + 1)), name: fn.name, arguments: args });
        });
        if (calls.length) return { text: msg.content || null, toolCalls: calls, usage: data.usage };
      }

      // 2) Tool call embedded in the assistant text (Qwen <tool_call>).
      const content = msg.content || '';
      const parsed = parseToolCallText(content);
      if (parsed) return { text: null, toolCalls: [{ id: 'local-1', name: parsed.name, arguments: parsed.arguments }], usage: data.usage };

      // 3) Plain reply.
      return { text: stripThinking(content), toolCalls: [], usage: data.usage };
    }
  }

  // Ping for the Settings "Test connection" button: GET {base}/models.
  async function testLocalProvider(url) {
    const base = (url || 'http://localhost:11434/v1').replace(/\/+$/, '');
    const r = await fetch(base + '/models');
    if (!r.ok) throw new Error('HTTP ' + r.status);
    const j = await r.json().catch(() => ({}));
    const models = (j.data || j.models || []).map(m => m.id || m.name).filter(Boolean);
    return { ok: true, models };
  }

  window.LocalOpenAIProvider = LocalOpenAIProvider;
  window.canopusParseToolCallText = parseToolCallText; // exposed for tests
  window.testLocalProvider = testLocalProvider;
})();
