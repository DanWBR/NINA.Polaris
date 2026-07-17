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
# Tests for the local (llama-server) provider: the Qwen3 <tool_call> parser and
# the two response shapes complete() must handle — native OpenAI tool_calls and
# a tool call embedded in assistant text. No network: httpx is stubbed.
#
#   python -m pytest server/test_providers_local.py

import asyncio

import providers_local
from providers_local import LlamaServerProvider, parse_tool_call_text


# ---- parser -------------------------------------------------------------
def test_parse_tagged_toolcall_after_thinking():
    text = ('<think>The user asks about progress, I should call get_status.</think>\n'
            '<tool_call>\n{"name": "get_status", "arguments": {}}\n</tool_call>')
    assert parse_tool_call_text(text) == ("get_status", {})


def test_parse_toolcall_with_args():
    text = '<tool_call>{"name": "search_catalog", "arguments": {"query": "M42"}}</tool_call>'
    assert parse_tool_call_text(text) == ("search_catalog", {"query": "M42"})


def test_parse_bare_json_object():
    assert parse_tool_call_text('sure: {"name": "get_weather", "arguments": {}} done') \
        == ("get_weather", {})


def test_parse_plain_prose_is_none():
    assert parse_tool_call_text("Guiding looks fine right now.") is None


def test_unterminated_think_is_dropped():
    # A generation cut off mid-thought has no decision after it.
    assert parse_tool_call_text("<think>I am considering get_status") is None


# ---- complete() with stubbed httpx -------------------------------------
class _FakeResp:
    def __init__(self, data, status_code=200):
        self._data = data
        self.status_code = status_code
        self.text = ""

    def raise_for_status(self):
        pass

    def json(self):
        return self._data


class _FakeClient:
    def __init__(self, data):
        self._data = data

    async def __aenter__(self):
        return self

    async def __aexit__(self, *a):
        return False

    async def post(self, url, json=None):
        assert url.endswith("/v1/chat/completions")
        # tools must be forwarded to the model
        assert json and json.get("tools")
        return _FakeResp(self._data)


def _run_complete(monkeypatch, response_data):
    monkeypatch.setattr(providers_local.httpx, "AsyncClient",
                        lambda **kw: _FakeClient(response_data))
    p = LlamaServerProvider("http://127.0.0.1:8791")
    tools = [{"type": "function", "function": {"name": "get_status", "parameters": {}}}]
    return asyncio.run(p.complete([{"role": "user", "content": "how is it going?"}], tools))


def test_native_tool_calls(monkeypatch):
    data = {"choices": [{"message": {"content": None, "tool_calls": [
        {"id": "call_7", "type": "function",
         "function": {"name": "get_status", "arguments": "{}"}}]}}],
        "usage": {"prompt_tokens": 100, "completion_tokens": 5, "total_tokens": 105}}
    res = _run_complete(monkeypatch, data)
    assert len(res.tool_calls) == 1
    assert res.tool_calls[0].name == "get_status" and res.tool_calls[0].id == "call_7"
    assert res.tool_calls[0].arguments == {}
    assert res.usage["total"] == 105


def test_content_toolcall_fallback(monkeypatch):
    data = {"choices": [{"message": {
        "content": '<tool_call>{"name": "search_catalog", "arguments": {"query": "NGC 7000"}}</tool_call>'}}]}
    res = _run_complete(monkeypatch, data)
    assert len(res.tool_calls) == 1
    assert res.tool_calls[0].name == "search_catalog"
    assert res.tool_calls[0].arguments == {"query": "NGC 7000"}


def test_plain_text_reply(monkeypatch):
    data = {"choices": [{"message": {"content": "<think>hmm</think>Guiding is running well."}}]}
    res = _run_complete(monkeypatch, data)
    assert not res.tool_calls
    assert res.text == "Guiding is running well."


if __name__ == "__main__":
    import types
    mp = types.SimpleNamespace(setattr=lambda o, n, v: setattr(o, n, v))
    for name, fn in sorted(globals().items()):
        if name.startswith("test_") and callable(fn):
            fn(mp) if fn.__code__.co_argcount else fn()
            print("PASS", name)
    print("ALL PASS")
