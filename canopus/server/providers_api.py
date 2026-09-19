# N.I.N.A. Polaris — Canopus Assistant
# Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
#
# This program is free software: you can redistribute it and/or modify it
# under the terms of the GNU Affero General Public License as published by
# the Free Software Foundation, either version 3 of the License, or (at your
# option) any later version.
#
# This program is distributed in the hope that it will be useful, but WITHOUT
# ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or
# FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License
# for more details. You should have received a copy of the license along with
# this program. If not, see <https://www.gnu.org/licenses/>.
#
# Cloud providers driven by the user's OWN API key: Anthropic (Messages API),
# OpenAI (Chat Completions) and any OpenAI-compatible server (OpenRouter, Groq,
# a remote Ollama...). This is the "Cloud API with your key" Canopus backend: the
# Polaris host keeps the key and runs this agent; the browser never sees it.
#
# The agent loop speaks OpenAI chat format (messages with tool_calls / tool
# results, vision as image_url data URLs). OpenAIChatProvider passes that
# through; AnthropicProvider translates it to the Messages API and back.
#
# Configuration comes from the environment the host sets on this process:
#   CANOPUS_API_PROVIDER   anthropic | openai | compatible
#   CANOPUS_API_KEY        the key
#   CANOPUS_API_MODEL      model id
#   CANOPUS_API_BASE_URL   optional; required for "compatible". For openai and
#                          compatible it is the /v1 root ({base}/chat/completions);
#                          for anthropic the host root ({base}/v1/messages).
#   CANOPUS_API_MAX_TOKENS optional output cap (default 8192)

from __future__ import annotations

import asyncio
import json
import os

import httpx

from providers import Provider, ProviderResult, ToolCall
from providers_local import _load, _strip_thinking, parse_tool_call_text

ANTHROPIC_DEFAULT_URL = "https://api.anthropic.com"
OPENAI_DEFAULT_URL = "https://api.openai.com/v1"
ANTHROPIC_VERSION = "2023-06-01"
DEFAULT_MAX_TOKENS = 8192
SETTINGS_HINT = "Check it in Settings, Assistant."


# --------------------------------------------------------------------------
# Errors: every failure becomes a RuntimeError with text the chat can show.
# --------------------------------------------------------------------------
def _error_detail(r: httpx.Response) -> str:
    try:
        j = r.json()
    except Exception:
        return (r.text or "")[:300]
    err = j.get("error") if isinstance(j, dict) else None
    if isinstance(err, dict):
        return str(err.get("message") or err.get("type") or "")[:300]
    if isinstance(err, str):
        return err[:300]
    return str(j.get("message") or "")[:300] if isinstance(j, dict) else ""


def _raise_http(label: str, model: str, r: httpx.Response) -> None:
    detail = _error_detail(r)
    code = r.status_code
    low = detail.lower()
    if code in (401, 403):
        raise RuntimeError(f"{label} rejected the API key. {SETTINGS_HINT}")
    if code == 404 or "model_not_found" in low or "not_found_error" in low or "does not exist" in low:
        raise RuntimeError(f"{label} doesn't know the model '{model}'. Pick another in Settings, Assistant.")
    if code in (429, 529):
        raise RuntimeError(f"{label} is rate-limiting or out of credit (HTTP {code}): {detail}".rstrip(": "))
    if code == 400 and "image" in low:
        raise RuntimeError(f"{label} error: {detail}. Pick a vision-capable model to analyse frames.")
    raise RuntimeError(f"{label} error (HTTP {code}): {detail}".rstrip(": "))


def _raise_network(label: str, url: str, e: Exception) -> None:
    host = httpx.URL(url).host if url else label
    raise RuntimeError(
        f"Couldn't reach {host} ({type(e).__name__}). Check the host's internet connection."
    ) from e


def _retry_after(r: httpx.Response) -> float:
    try:
        return min(10.0, max(0.0, float(r.headers.get("retry-after", "2"))))
    except ValueError:
        return 2.0


async def _post_json(label: str, model: str, url: str, headers: dict, body: dict, timeout: float) -> dict:
    """POST with one retry on 429/529 (honouring Retry-After, capped at 10 s)."""
    async with httpx.AsyncClient(timeout=timeout) as client:
        for attempt in range(2):
            try:
                r = await client.post(url, json=body, headers=headers)
            except httpx.RequestError as e:
                _raise_network(label, url, e)
            if r.status_code in (429, 529) and attempt == 0:
                await asyncio.sleep(_retry_after(r))
                continue
            if r.status_code >= 400:
                _raise_http(label, model, r)
            return r.json()
    raise RuntimeError(f"{label} error: no response")   # unreachable


def _usage_openai(data: dict) -> dict | None:
    u = data.get("usage") or {}
    if not u:
        return None
    return {"prompt": u.get("prompt_tokens"), "completion": u.get("completion_tokens"),
            "total": u.get("total_tokens")}


# --------------------------------------------------------------------------
# OpenAI and OpenAI-compatible
# --------------------------------------------------------------------------
class OpenAIChatProvider(Provider):
    """Chat Completions with a Bearer key. flavor="openai" sends
    max_completion_tokens and no temperature (the gpt-5 / o-series reject the
    old fields); flavor="compatible" sends max_tokens, which is what llama.cpp,
    Ollama, LM Studio, OpenRouter and Groq understand."""

    def __init__(self, base_url: str, api_key: str, model: str, flavor: str = "openai",
                 timeout: float = 120.0, max_tokens: int = DEFAULT_MAX_TOKENS):
        self.base_url = base_url.rstrip("/")
        self.api_key = api_key
        self.model = model
        self.flavor = flavor
        self.timeout = timeout
        self.max_tokens = max_tokens
        self.name = "openai" if flavor == "openai" else "compatible"
        self.label = "OpenAI" if flavor == "openai" else "The model server"

    def _body(self, messages: list[dict], tools: list[dict]) -> dict:
        body = {"model": self.model, "messages": messages, "stream": False}
        if tools:
            body["tools"] = tools
            body["tool_choice"] = "auto"
        if self.flavor == "openai":
            body["max_completion_tokens"] = self.max_tokens
        else:
            body["max_tokens"] = self.max_tokens
        return body

    async def complete(self, messages: list[dict], tools: list[dict]) -> ProviderResult:
        headers = {"Authorization": f"Bearer {self.api_key}", "Content-Type": "application/json"}
        data = await _post_json(self.label, self.model, f"{self.base_url}/chat/completions",
                                headers, self._body(messages, tools), self.timeout)
        usage = _usage_openai(data)
        choice = (data.get("choices") or [{}])[0]
        msg = choice.get("message") or {}

        native = msg.get("tool_calls") or []
        calls = []
        for i, tc in enumerate(native):
            fn = tc.get("function") or {}
            name = fn.get("name")
            if not name:
                continue
            raw = fn.get("arguments")
            args = (_load(raw) or {}) if isinstance(raw, str) else (raw if isinstance(raw, dict) else {})
            calls.append(ToolCall(id=tc.get("id") or f"call-{i + 1}", name=name, arguments=args))
        if calls:
            return ProviderResult(text=msg.get("content") or None, tool_calls=calls, usage=usage)

        content = msg.get("content") or ""
        if self.flavor == "compatible":
            # Ollama-class servers may hand the call back as <tool_call> text.
            parsed = parse_tool_call_text(content)
            if parsed:
                name, args = parsed
                return ProviderResult(tool_calls=[ToolCall(id="call-1", name=name, arguments=args)], usage=usage)
            content = _strip_thinking(content)
        return ProviderResult(text=content, usage=usage)


# --------------------------------------------------------------------------
# Anthropic Messages API: translation of the agent's OpenAI-format history
# --------------------------------------------------------------------------
def to_anthropic_tools(openai_tools: list[dict]) -> list[dict]:
    out = []
    for t in openai_tools or []:
        fn = t.get("function") if isinstance(t.get("function"), dict) else t
        name = fn.get("name")
        if not name:
            continue
        params = fn.get("parameters")
        if not isinstance(params, dict) or "type" not in params:
            params = {"type": "object", "properties": {}}
        out.append({"name": name, "description": fn.get("description") or "", "input_schema": params})
    return out


def data_url_to_image_block(url: str) -> dict | None:
    """data:image/jpeg;base64,... -> Anthropic base64 image block; None otherwise."""
    if not isinstance(url, str) or not url.startswith("data:"):
        return None
    head, sep, data = url[5:].partition(",")
    if not sep:
        return None
    parts = head.split(";")
    media = parts[0] or "image/jpeg"
    if "base64" not in parts[1:] or not media.startswith("image/"):
        return None
    return {"type": "image", "source": {"type": "base64", "media_type": media, "data": data}}


def _content_blocks(content) -> list[dict]:
    """OpenAI message content (string or parts list) -> Anthropic blocks."""
    if isinstance(content, str):
        return [{"type": "text", "text": content}] if content.strip() else []
    blocks: list[dict] = []
    for part in content or []:
        if not isinstance(part, dict):
            continue
        kind = part.get("type")
        if kind == "text":
            text = part.get("text") or ""
            if text.strip():
                blocks.append({"type": "text", "text": text})
        elif kind == "image_url":
            url = (part.get("image_url") or {}).get("url") if isinstance(part.get("image_url"), dict) else part.get("image_url")
            img = data_url_to_image_block(url)
            blocks.append(img if img else {"type": "text", "text": "(image unavailable)"})
    return blocks


def _tool_result_block(m: dict) -> dict:
    content = m.get("content")
    if not isinstance(content, str):
        content = json.dumps(content) if content is not None else ""
    block = {"type": "tool_result", "tool_use_id": m.get("tool_call_id") or "", "content": content}
    try:
        parsed = json.loads(content) if content else None
    except json.JSONDecodeError:
        parsed = None
    if isinstance(parsed, dict) and parsed.get("ok") is False:
        block["is_error"] = True
    return block


def to_anthropic_messages(messages: list[dict], raw_by_first_tool_id: dict | None = None) -> tuple[str, list[dict]]:
    """Translate the agent's OpenAI-format history.

    - system messages become the top-level `system` string;
    - assistant tool_calls become tool_use blocks (or the stashed raw response
      content when we have it, so thinking blocks are replayed verbatim);
    - every run of tool results becomes ONE user message, and a user message
      that follows tool results (the vision follow-up) merges into it, results
      first; the remaining same-role neighbours are merged too.
    """
    raw_by_first_tool_id = raw_by_first_tool_id or {}
    system_parts: list[str] = []
    out: list[dict] = []

    def push(role: str, blocks: list[dict]) -> None:
        if not blocks:
            return
        if out and out[-1]["role"] == role:
            out[-1]["content"].extend(blocks)
        else:
            out.append({"role": role, "content": list(blocks)})

    for m in messages:
        role = m.get("role")
        if role == "system":
            c = m.get("content")
            if isinstance(c, str) and c.strip():
                system_parts.append(c)
            continue
        if role == "user":
            push("user", _content_blocks(m.get("content")))
        elif role == "assistant":
            calls = m.get("tool_calls") or []
            if calls:
                first_id = (calls[0].get("id") or "") if isinstance(calls[0], dict) else ""
                raw = raw_by_first_tool_id.get(first_id)
                if raw:
                    push("assistant", [dict(b) for b in raw])
                    continue
                blocks = _content_blocks(m.get("content")) if isinstance(m.get("content"), str) else []
                for c in calls:
                    fn = c.get("function") or {}
                    args = fn.get("arguments")
                    if isinstance(args, str):
                        args = _load(args) or {}
                    blocks.append({"type": "tool_use", "id": c.get("id") or "", "name": fn.get("name") or "",
                                   "input": args if isinstance(args, dict) else {}})
                push("assistant", blocks)
            else:
                push("assistant", _content_blocks(m.get("content")))
        elif role == "tool":
            push("user", [_tool_result_block(m)])

    # tool_result blocks must lead their user message (Anthropic requires it).
    for msg in out:
        if msg["role"] == "user":
            results = [b for b in msg["content"] if b.get("type") == "tool_result"]
            if results and results != msg["content"][: len(results)]:
                msg["content"] = results + [b for b in msg["content"] if b.get("type") != "tool_result"]

    # Orphan tool_results (no matching tool_use just before) would be rejected.
    for i, msg in enumerate(out):
        if msg["role"] != "user":
            continue
        prev_ids = {b.get("id") for b in out[i - 1]["content"] if b.get("type") == "tool_use"} if i > 0 else set()
        msg["content"] = [b for b in msg["content"] if b.get("type") != "tool_result" or b.get("tool_use_id") in prev_ids]
    out = [m for m in out if m["content"]]
    while out and out[0]["role"] != "user":
        out.pop(0)
    return "\n\n".join(system_parts), out


def parse_anthropic_response(data: dict) -> tuple[str, list[ToolCall], str | None, dict | None]:
    text_parts: list[str] = []
    calls: list[ToolCall] = []
    for block in data.get("content") or []:
        kind = block.get("type")
        if kind == "text":
            text_parts.append(block.get("text") or "")
        elif kind == "tool_use":
            inp = block.get("input")
            calls.append(ToolCall(id=block.get("id") or f"toolu-{len(calls) + 1}", name=block.get("name") or "",
                                  arguments=inp if isinstance(inp, dict) else {}))
    usage = None
    u = data.get("usage") or {}
    if u:
        p, c = u.get("input_tokens") or 0, u.get("output_tokens") or 0
        usage = {"prompt": p, "completion": c, "total": p + c}
    return "".join(text_parts).strip(), calls, data.get("stop_reason"), usage


class AnthropicProvider(Provider):
    name = "anthropic"
    label = "Anthropic"

    def __init__(self, api_key: str, model: str, base_url: str = ANTHROPIC_DEFAULT_URL,
                 timeout: float = 180.0, max_tokens: int = DEFAULT_MAX_TOKENS):
        self.api_key = api_key
        self.model = model
        self.base_url = (base_url or ANTHROPIC_DEFAULT_URL).rstrip("/")
        self.timeout = timeout
        self.max_tokens = max_tokens
        # Raw response content of every tool-use reply, keyed by its first
        # tool_use id: the model's thinking blocks must be replayed verbatim
        # with the tool_use they belong to, or the API rejects the next turn.
        self._raw_by_first_tool_id: dict[str, list[dict]] = {}

    def _body(self, messages: list[dict], tools: list[dict]) -> dict:
        system, msgs = to_anthropic_messages(messages, self._raw_by_first_tool_id)
        body = {"model": self.model, "max_tokens": self.max_tokens, "messages": msgs}
        if system:
            body["system"] = system
        atools = to_anthropic_tools(tools)
        if atools:
            body["tools"] = atools
        return body

    async def complete(self, messages: list[dict], tools: list[dict]) -> ProviderResult:
        headers = {"x-api-key": self.api_key, "anthropic-version": ANTHROPIC_VERSION,
                   "content-type": "application/json"}
        data = await _post_json(self.label, self.model, f"{self.base_url}/v1/messages",
                                headers, self._body(messages, tools), self.timeout)
        text, calls, stop, usage = parse_anthropic_response(data)
        if calls:
            self._raw_by_first_tool_id[calls[0].id] = list(data.get("content") or [])
            return ProviderResult(text=text or None, tool_calls=calls, usage=usage)
        if stop == "refusal":
            text = text or "The model declined this request."
        elif stop == "max_tokens":
            text = (text + " (reply cut short by the output limit)").strip()
        return ProviderResult(text=text, usage=usage)


# --------------------------------------------------------------------------
# Factory
# --------------------------------------------------------------------------
class _ConfigErrorProvider(Provider):
    """Stands in when the API backend is misconfigured: the chat shows the
    reason instead of mock replies."""
    name = "config-error"

    def __init__(self, message: str):
        self.message = message

    async def complete(self, messages: list[dict], tools: list[dict]) -> ProviderResult:
        raise RuntimeError(self.message)


def get_api_provider() -> Provider:
    provider = os.environ.get("CANOPUS_API_PROVIDER", "").strip().lower()
    key = os.environ.get("CANOPUS_API_KEY", "").strip()
    model = os.environ.get("CANOPUS_API_MODEL", "").strip()
    base = os.environ.get("CANOPUS_API_BASE_URL", "").strip()
    try:
        max_tokens = int(os.environ.get("CANOPUS_API_MAX_TOKENS", "") or DEFAULT_MAX_TOKENS)
    except ValueError:
        max_tokens = DEFAULT_MAX_TOKENS
    not_configured = "The cloud API backend isn't configured. "
    if provider not in ("anthropic", "openai", "compatible"):
        return _ConfigErrorProvider(not_configured + "Pick a provider in Settings, Assistant.")
    if not key:
        return _ConfigErrorProvider(not_configured + "Add an API key in Settings, Assistant.")
    if not model:
        return _ConfigErrorProvider(not_configured + "Choose a model in Settings, Assistant.")
    if provider == "anthropic":
        return AnthropicProvider(key, model, base or ANTHROPIC_DEFAULT_URL, max_tokens=max_tokens)
    if provider == "openai":
        return OpenAIChatProvider(base or OPENAI_DEFAULT_URL, key, model, flavor="openai", max_tokens=max_tokens)
    if not base:
        return _ConfigErrorProvider(not_configured + "Enter the base URL of the OpenAI-compatible server in Settings, Assistant.")
    return OpenAIChatProvider(base, key, model, flavor="compatible", max_tokens=max_tokens)
