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
# LlamaServerProvider — the OPEN, keyless, on-device/on-SBC LLM backend.
#
# It drives a local llama.cpp `llama-server` (OpenAI-compatible /v1 surface)
# running Qwen3-4B Q4_0. This is the provider behind the "On this server (SBC)"
# and (later) "On this device" Canopus backends: no API key, no network beyond
# loopback, no subscription. It is a drop-in Provider, so the agent loop
# (agent.py) is unchanged.
#
# The prompt/parse strategy MIRRORS the canopus-eval harness that measured this
# model (canopus-eval/canopus_eval/backends.py): ask llama-server for native
# OpenAI tool_calls (works when llama-server runs with --jinja), and fall back to
# parsing Qwen3's `<tool_call>{json}</tool_call>` content when the runtime hands
# the call back as plain text. Thinking is disabled (enable_thinking=false): on
# the eval it only added latency and no safety, and the target is a phone/SBC.

from __future__ import annotations

import json
import os
import re

import httpx

from providers import Provider, ProviderResult, ToolCall


# --------------------------------------------------------------------------
# Tool-call parsing — vendored from the canopus-eval harness (the JSON +
# <tool_call> path, which MOBILE.md confirms is all Qwen3 needs). Kept local so
# the open server has no dependency on the eval package.
# --------------------------------------------------------------------------
_FENCE = re.compile(r"```(?:json)?\s*(\{.*?\})\s*```", re.S)
_TAGGED = re.compile(r"<tool_call>\s*(\{.*?\})\s*</tool_call>", re.S)
# Qwen3 reasons inside <think>; that block is full of the tool's name and
# hypothetical JSON, so it must be stripped before parsing or the model's musings
# read as its decision. An unterminated block (generation cut off) drops to EOF.
_THINK = re.compile(r"<think>.*?</think>", re.S)
_THINK_OPEN = re.compile(r"<think>.*$", re.S)


def _strip_thinking(text: str) -> str:
    return _THINK_OPEN.sub("", _THINK.sub("", text)).strip()


def _load(s: str) -> dict | None:
    try:
        return json.loads(s)
    except json.JSONDecodeError:
        # Trailing prose after the object is common; retry on the balanced prefix.
        depth = 0
        for i, ch in enumerate(s):
            if ch == "{":
                depth += 1
            elif ch == "}":
                depth -= 1
                if depth == 0:
                    try:
                        return json.loads(s[: i + 1])
                    except json.JSONDecodeError:
                        return None
        return None


def _normalize(obj) -> tuple[str, dict] | None:
    if not isinstance(obj, dict):
        return None
    # {"name", "arguments"} and the OpenAI {"function": {...}} wrapper.
    fn = obj.get("function") if isinstance(obj.get("function"), dict) else obj
    name = fn.get("name")
    if not isinstance(name, str):
        return None
    args = fn.get("arguments", fn.get("parameters", {}))
    if isinstance(args, str):
        args = _load(args) or {}
    return (name, args if isinstance(args, dict) else {})


def parse_tool_call_text(text: str) -> tuple[str, dict] | None:
    """Extract a (name, arguments) tool call from Qwen3's free-text output."""
    text = _strip_thinking(text)
    for pat in (_TAGGED, _FENCE):
        m = pat.search(text)
        if m:
            obj = _load(m.group(1))
            if obj:
                call = _normalize(obj)
                if call:
                    return call
    # Bare JSON object somewhere in the reply.
    start = text.find("{")
    while start != -1:
        obj = _load(text[start:])
        if obj:
            call = _normalize(obj)
            if call:
                return call
        start = text.find("{", start + 1)
    return None


class LlamaServerProvider(Provider):
    """Talk to a local llama.cpp `llama-server` over its OpenAI-compatible API."""

    name = "llama-server"

    def __init__(self, base_url: str, model: str = "local", timeout: float = 180.0,
                 max_tokens: int = 1024) -> None:
        # base_url is the llama-server root, e.g. http://127.0.0.1:8791
        self.base_url = base_url.rstrip("/")
        self.model = model
        self.timeout = timeout
        self.max_tokens = max_tokens

    async def complete(self, messages: list[dict], tools: list[dict]) -> ProviderResult:
        body = {
            "model": self.model,
            "messages": messages,
            "tools": tools,
            "tool_choice": "auto",
            "temperature": 0,
            "stream": False,
            "max_tokens": self.max_tokens,
            # Qwen3: turn thinking off via the chat template (llama-server --jinja).
            # Harmless if the template ignores it; the parser strips <think> anyway.
            "chat_template_kwargs": {"enable_thinking": False},
        }
        async with httpx.AsyncClient(timeout=self.timeout) as client:
            try:
                r = await client.post(f"{self.base_url}/v1/chat/completions", json=body)
            except httpx.RequestError as e:
                # Connection refused / server disconnected / read timeout: the local
                # model isn't answering (still loading a big prompt, or restarted).
                # Surface something actionable instead of a bare/empty error.
                raise RuntimeError(
                    "the local model isn't responding right now "
                    f"({type(e).__name__}). It may still be loading or was "
                    "restarted — wait a moment, or Stop then Start the local "
                    "backend in Settings."
                ) from e
            if r.status_code >= 400:
                # Surface llama-server's actual reason (e.g. "request (N tokens)
                # exceeds the available context size") instead of a bare status code.
                detail = ""
                try:
                    j = r.json()
                    err = j.get("error")
                    detail = (err.get("message") if isinstance(err, dict) else err) or j.get("message") or ""
                except Exception:
                    detail = (r.text or "")[:300]
                raise RuntimeError(f"local model error (HTTP {r.status_code}): {detail}".strip())
            data = r.json()

        usage = None
        u = data.get("usage") or {}
        if u:
            usage = {"prompt": u.get("prompt_tokens"),
                     "completion": u.get("completion_tokens"),
                     "total": u.get("total_tokens")}

        choice = (data.get("choices") or [{}])[0]
        msg = choice.get("message") or {}

        # 1) Native OpenAI tool_calls (llama-server --jinja parsed them for us).
        native = msg.get("tool_calls") or []
        if native:
            calls = []
            for i, tc in enumerate(native):
                fn = tc.get("function") or {}
                name = fn.get("name")
                if not name:
                    continue
                raw_args = fn.get("arguments")
                if isinstance(raw_args, str):
                    args = _load(raw_args) or {}
                elif isinstance(raw_args, dict):
                    args = raw_args
                else:
                    args = {}
                calls.append(ToolCall(id=tc.get("id") or f"local-{i+1}", name=name, arguments=args))
            if calls:
                return ProviderResult(text=msg.get("content"), tool_calls=calls, usage=usage)

        # 2) Fallback: the call is embedded in the assistant text (Qwen3
        #    <tool_call>{...}</tool_call>).
        content = msg.get("content") or ""
        parsed = parse_tool_call_text(content)
        if parsed:
            name, args = parsed
            return ProviderResult(tool_calls=[ToolCall(id="local-1", name=name, arguments=args)], usage=usage)

        # 3) Plain assistant reply.
        return ProviderResult(text=_strip_thinking(content), usage=usage)


def get_local_provider() -> LlamaServerProvider:
    """Factory used by providers.get_provider() (and usable via
    CANOPUS_PROVIDER_FACTORY=providers_local:get_local_provider)."""
    url = os.environ.get("CANOPUS_LOCAL_LLM_URL", "http://127.0.0.1:8791").strip()
    model = os.environ.get("CANOPUS_LOCAL_LLM_MODEL", "local").strip() or "local"
    return LlamaServerProvider(url, model=model)
