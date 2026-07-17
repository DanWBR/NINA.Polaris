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
# LLM provider abstraction — the seam between the open agent loop and a
# pluggable backend.
#
# This open module ships ONLY the interface + a deterministic MockProvider, so
# the whole agent loop and the browser-bridge round-trip run offline with no API
# keys. The concrete backend that talks to a hosted model (e.g. Azure OpenAI) —
# and its credentials — lives OUTSIDE this repo and is plugged in at runtime via
# CANOPUS_PROVIDER_FACTORY (see get_provider). A provider takes OpenAI-style
# messages + tool specs and returns assistant text OR a list of tool calls; the
# agent loop is provider-agnostic on top of this.

from __future__ import annotations

import importlib
import os
from dataclasses import dataclass, field


@dataclass
class ToolCall:
    id: str
    name: str
    arguments: dict


@dataclass
class ProviderResult:
    text: str | None = None
    tool_calls: list[ToolCall] = field(default_factory=list)
    # Token usage reported by the backend for THIS completion, for a deployment
    # that meters usage. {"prompt", "completion", "total"}; None when the backend
    # doesn't report it (e.g. the mock provider).
    usage: dict | None = None


class Provider:
    name = "base"

    async def complete(self, messages: list[dict], tools: list[dict]) -> ProviderResult:
        raise NotImplementedError


# --------------------------------------------------------------------------
# Mock provider — no network. Deterministic single tool round-trip so the
# end-to-end bridge (user -> plan/tool-call -> browser executes on Polaris ->
# result -> reply) can be exercised offline, and the open repo is fully runnable.
# --------------------------------------------------------------------------
class MockProvider(Provider):
    name = "mock"

    _KEYWORD_TOOL = [
        (("tonight", "target", "what to shoot", "suggest"), "get_tonights_best", {}),
        (("weather", "clouds", "forecast"), "get_weather", {}),
        (("status", "how is it going", "how's it going", "going"), "get_status", {}),
        (("focus",), "show_panel", {"tab": "focus"}),
        (("live", "stack"), "show_panel", {"tab": "live"}),
        (("plan",), "show_panel", {"tab": "plan"}),
        # A mutating example so the plan-approval path can be demoed. Eagle Neb.
        (("slew", "go to", "goto", "point at"), "slew_to", {"ra": 18.31, "dec": -13.79}),
    ]

    async def complete(self, messages: list[dict], tools: list[dict]) -> ProviderResult:
        last = messages[-1] if messages else {}
        # If the last thing was a tool result, wrap up with a summary and stop.
        if last.get("role") == "tool":
            content = last.get("content", "")
            preview = content if len(content) < 300 else content[:300] + "..."
            return ProviderResult(text=f"Done. Result: {preview}")

        text = ""
        for m in reversed(messages):
            if m.get("role") == "user":
                text = (m.get("content") or "").lower()
                break

        tool_names = {t["function"]["name"] for t in tools}
        for keywords, name, args in self._KEYWORD_TOOL:
            if name in tool_names and any(k in text for k in keywords):
                return ProviderResult(tool_calls=[ToolCall(id="mock-1", name=name, arguments=args)])

        return ProviderResult(text=(
            "I'm the Canopus Assistant (mock mode). Try: \"what's good tonight?\", "
            "\"how is it going?\", \"show me focus\", or \"slew to the Eagle Nebula\"."
        ))


def get_provider() -> Provider:
    """Return the LLM backend for the agent loop.

    The open Canopus ships the MockProvider plus a keyless LlamaServerProvider
    (providers_local) for the local SBC/device tiers, so this repo is runnable and
    testable with no API keys. A production CLOUD deployment plugs in a hosted-model
    backend by pointing CANOPUS_PROVIDER_FACTORY at a "module:callable" that
    returns a Provider — that concrete backend (e.g. an Azure OpenAI client) and
    its credentials live OUTSIDE this open repo. ASSISTANT_PROVIDER=mock forces
    the mock even when a local URL or factory is configured.

    Resolution order: mock override -> local llama-server (CANOPUS_LOCAL_LLM_URL)
    -> private factory (CANOPUS_PROVIDER_FACTORY) -> mock.
    """
    if os.environ.get("ASSISTANT_PROVIDER", "").lower() == "mock":
        return MockProvider()

    # The local (SBC / on-device) tier: a llama.cpp llama-server on loopback. No
    # keys, no network beyond localhost — so it ships open, in-repo.
    if os.environ.get("CANOPUS_LOCAL_LLM_URL", "").strip():
        try:
            from providers_local import get_local_provider
            return get_local_provider()
        except Exception:
            return MockProvider()

    spec = os.environ.get("CANOPUS_PROVIDER_FACTORY", "").strip()
    if spec:
        try:
            mod_name, _, fn_name = spec.partition(":")
            mod = importlib.import_module(mod_name)
            factory = getattr(mod, fn_name or "get_provider")
            return factory()
        except Exception:
            # A misconfigured or absent private backend must never crash the open
            # loop — fall back to the mock so the agent still answers.
            return MockProvider()
    return MockProvider()
