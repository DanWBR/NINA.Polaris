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
# Tests for providers_api: the OpenAI / OpenAI-compatible / Anthropic backends
# behind "Cloud API with your key". No network: httpx.AsyncClient is faked.

import asyncio
import json
import os

import httpx
import pytest

import providers
import providers_api
from providers import ToolCall
from providers_api import (AnthropicProvider, OpenAIChatProvider, data_url_to_image_block,
                           get_api_provider, parse_anthropic_response, to_anthropic_messages,
                           to_anthropic_tools)

TOOLS = [{"type": "function", "function": {
    "name": "get_status", "description": "Read the rig state", "parameters": {}}}]


class _FakeResp:
    def __init__(self, data, status_code=200, headers=None):
        self._data = data
        self.status_code = status_code
        self.headers = headers or {}
        self.text = json.dumps(data) if not isinstance(data, str) else data

    def json(self):
        if isinstance(self._data, str):
            raise ValueError("not json")
        return self._data


class _FakeClient:
    """Records every POST; answers from a queue of responses (or raises)."""
    calls: list = []
    queue: list = []

    def __init__(self, **kw):
        pass

    async def __aenter__(self):
        return self

    async def __aexit__(self, *a):
        return False

    async def post(self, url, json=None, headers=None):
        _FakeClient.calls.append({"url": url, "json": json, "headers": headers})
        item = _FakeClient.queue.pop(0)
        if isinstance(item, Exception):
            raise item
        return item


def _install(monkeypatch, *responses):
    _FakeClient.calls = []
    _FakeClient.queue = list(responses)
    monkeypatch.setattr(providers_api.httpx, "AsyncClient", _FakeClient)
    monkeypatch.setattr(providers_api.asyncio, "sleep", _no_sleep)


async def _no_sleep(_s):
    return None


def _run(p, messages=None):
    return asyncio.run(p.complete(messages or [{"role": "user", "content": "how is it going?"}], TOOLS))


# ---- OpenAI ---------------------------------------------------------------
def test_openai_request_shape(monkeypatch):
    _install(monkeypatch, _FakeResp({"choices": [{"message": {"content": "Fine."}}],
                                     "usage": {"prompt_tokens": 10, "completion_tokens": 2, "total_tokens": 12}}))
    p = OpenAIChatProvider("https://api.openai.com/v1", "sk-test", "gpt-5")
    res = _run(p)
    call = _FakeClient.calls[0]
    assert call["url"] == "https://api.openai.com/v1/chat/completions"
    assert call["headers"]["Authorization"] == "Bearer sk-test"
    body = call["json"]
    assert body["model"] == "gpt-5" and body["tools"] == TOOLS and body["tool_choice"] == "auto"
    assert "max_completion_tokens" in body and "max_tokens" not in body
    assert "temperature" not in body and "chat_template_kwargs" not in body
    assert res.text == "Fine." and res.usage == {"prompt": 10, "completion": 2, "total": 12}


def test_compatible_uses_max_tokens_and_text_tool_call_fallback(monkeypatch):
    _install(monkeypatch, _FakeResp({"choices": [{"message": {
        "content": '<tool_call>{"name": "get_status", "arguments": {}}</tool_call>'}}]}))
    p = OpenAIChatProvider("http://ollama:11434/v1/", "k", "qwen3", flavor="compatible")
    res = _run(p)
    body = _FakeClient.calls[0]["json"]
    assert "max_tokens" in body and "max_completion_tokens" not in body
    assert _FakeClient.calls[0]["url"] == "http://ollama:11434/v1/chat/completions"
    assert [c.name for c in res.tool_calls] == ["get_status"]


def test_openai_native_tool_calls_keep_ids(monkeypatch):
    _install(monkeypatch, _FakeResp({"choices": [{"message": {"content": None, "tool_calls": [
        {"id": "call_abc", "type": "function", "function": {"name": "get_status", "arguments": '{"a": 1}'}}]}}]}))
    res = _run(OpenAIChatProvider("https://api.openai.com/v1", "k", "gpt-5"))
    assert res.tool_calls[0].id == "call_abc" and res.tool_calls[0].arguments == {"a": 1}


def test_openai_401_is_a_key_error(monkeypatch):
    _install(monkeypatch, _FakeResp({"error": {"message": "Incorrect API key"}}, 401))
    with pytest.raises(RuntimeError, match="rejected the API key"):
        _run(OpenAIChatProvider("https://api.openai.com/v1", "bad", "gpt-5"))


def test_openai_unknown_model(monkeypatch):
    _install(monkeypatch, _FakeResp({"error": {"message": "The model `gpt-9` does not exist", "code": "model_not_found"}}, 404))
    with pytest.raises(RuntimeError, match="doesn't know the model 'gpt-9'"):
        _run(OpenAIChatProvider("https://api.openai.com/v1", "k", "gpt-9"))


def test_429_retries_once_then_reports(monkeypatch):
    _install(monkeypatch,
             _FakeResp({"error": {"message": "slow down"}}, 429, {"retry-after": "1"}),
             _FakeResp({"error": {"message": "slow down"}}, 429))
    with pytest.raises(RuntimeError, match="rate-limiting or out of credit"):
        _run(OpenAIChatProvider("https://api.openai.com/v1", "k", "gpt-5"))
    assert len(_FakeClient.calls) == 2


def test_429_then_success(monkeypatch):
    _install(monkeypatch,
             _FakeResp({"error": {"message": "slow down"}}, 429),
             _FakeResp({"choices": [{"message": {"content": "ok now"}}]}))
    assert _run(OpenAIChatProvider("https://api.openai.com/v1", "k", "gpt-5")).text == "ok now"


def test_network_error_names_the_host(monkeypatch):
    _install(monkeypatch, httpx.ConnectError("boom"))
    with pytest.raises(RuntimeError, match="Couldn't reach api.openai.com"):
        _run(OpenAIChatProvider("https://api.openai.com/v1", "k", "gpt-5"))


# ---- Anthropic: translation ------------------------------------------------
def test_tools_become_input_schema():
    at = to_anthropic_tools(TOOLS + [{"type": "function", "function": {
        "name": "slew_to", "parameters": {"type": "object", "properties": {"ra": {"type": "number"}}}}}])
    assert at[0] == {"name": "get_status", "description": "Read the rig state",
                     "input_schema": {"type": "object", "properties": {}}}
    assert at[1]["input_schema"]["properties"]["ra"] == {"type": "number"}


def test_data_url_to_image_block():
    b = data_url_to_image_block("data:image/jpeg;base64,/9j/AAA=")
    assert b == {"type": "image", "source": {"type": "base64", "media_type": "image/jpeg", "data": "/9j/AAA="}}
    assert data_url_to_image_block("https://x/y.jpg") is None
    assert data_url_to_image_block("data:text/plain;base64,aGk=") is None


def test_translation_tool_round_trip_and_vision_merge():
    history = [
        {"role": "system", "content": "You are Canopus."},
        {"role": "user", "content": "look at the frame"},
        {"role": "assistant", "content": None, "tool_calls": [
            {"id": "toolu_1", "type": "function", "function": {"name": "analyze_frame", "arguments": "{}"}}]},
        {"role": "tool", "tool_call_id": "toolu_1", "content": json.dumps({"ok": True, "note": "Image attached below."})},
        {"role": "user", "content": [{"type": "text", "text": "Here is the frame."},
                                     {"type": "image_url", "image_url": {"url": "data:image/jpeg;base64,QUJD"}}]},
    ]
    system, msgs = to_anthropic_messages(history)
    assert system == "You are Canopus."
    assert [m["role"] for m in msgs] == ["user", "assistant", "user"]
    assert msgs[1]["content"] == [{"type": "tool_use", "id": "toolu_1", "name": "analyze_frame", "input": {}}]
    merged = msgs[2]["content"]
    assert merged[0]["type"] == "tool_result" and merged[0]["tool_use_id"] == "toolu_1"
    assert "is_error" not in merged[0]
    assert merged[1] == {"type": "text", "text": "Here is the frame."}
    assert merged[2]["type"] == "image" and merged[2]["source"]["data"] == "QUJD"


def test_translation_marks_failed_tool_results_and_merges_neighbours():
    history = [
        {"role": "user", "content": "a"},
        {"role": "user", "content": "b"},
        {"role": "assistant", "content": "Sure", "tool_calls": [
            {"id": "t1", "type": "function", "function": {"name": "get_status", "arguments": "{}"}},
            {"id": "t2", "type": "function", "function": {"name": "get_weather", "arguments": "{}"}}]},
        {"role": "tool", "tool_call_id": "t1", "content": json.dumps({"ok": False, "error": "offline"})},
        {"role": "tool", "tool_call_id": "t2", "content": "clear"},
        {"role": "assistant", "content": "All good."},
        {"role": "user", "content": ""},
        {"role": "user", "content": "thanks"},
    ]
    _, msgs = to_anthropic_messages(history)
    assert [m["role"] for m in msgs] == ["user", "assistant", "user", "assistant", "user"]
    assert msgs[0]["content"] == [{"type": "text", "text": "a"}, {"type": "text", "text": "b"}]
    assert msgs[1]["content"][0] == {"type": "text", "text": "Sure"}
    assert [b["type"] for b in msgs[1]["content"]] == ["text", "tool_use", "tool_use"]
    assert len(msgs[2]["content"]) == 2 and msgs[2]["content"][0].get("is_error") is True
    assert "is_error" not in msgs[2]["content"][1]
    assert msgs[4]["content"] == [{"type": "text", "text": "thanks"}]


def test_translation_replays_stashed_raw_blocks():
    raw = [{"type": "thinking", "thinking": "hmm", "signature": "sig"},
           {"type": "tool_use", "id": "toolu_9", "name": "get_status", "input": {}}]
    history = [
        {"role": "user", "content": "how is it going?"},
        {"role": "assistant", "content": None, "tool_calls": [
            {"id": "toolu_9", "type": "function", "function": {"name": "get_status", "arguments": "{}"}}]},
        {"role": "tool", "tool_call_id": "toolu_9", "content": "{}"},
    ]
    _, msgs = to_anthropic_messages(history, {"toolu_9": raw})
    assert msgs[1]["content"] == raw


def test_translation_drops_leading_non_user_and_orphan_results():
    history = [
        {"role": "assistant", "content": "stray"},
        {"role": "user", "content": "hi"},
        {"role": "tool", "tool_call_id": "nope", "content": "{}"},
    ]
    _, msgs = to_anthropic_messages(history)
    assert msgs == [{"role": "user", "content": [{"type": "text", "text": "hi"}]}]


def test_parse_anthropic_response():
    text, calls, stop, usage = parse_anthropic_response({
        "content": [{"type": "text", "text": "Checking. "},
                    {"type": "tool_use", "id": "toolu_2", "name": "get_status", "input": {"x": 1}}],
        "stop_reason": "tool_use", "usage": {"input_tokens": 50, "output_tokens": 7}})
    assert text == "Checking." and stop == "tool_use"
    assert calls == [ToolCall(id="toolu_2", name="get_status", arguments={"x": 1})]
    assert usage == {"prompt": 50, "completion": 7, "total": 57}


def test_anthropic_request_and_stash(monkeypatch):
    first = {"content": [{"type": "thinking", "thinking": "t", "signature": "s"},
                         {"type": "tool_use", "id": "toolu_5", "name": "get_status", "input": {}}],
             "stop_reason": "tool_use", "usage": {"input_tokens": 1, "output_tokens": 1}}
    second = {"content": [{"type": "text", "text": "Guiding at 0.6 arcsec."}], "stop_reason": "end_turn",
              "usage": {"input_tokens": 3, "output_tokens": 4}}
    _install(monkeypatch, _FakeResp(first), _FakeResp(second))
    p = AnthropicProvider("sk-ant", "claude-sonnet-5")
    history = [{"role": "system", "content": "sys"}, {"role": "user", "content": "how is it going?"}]
    res = _run(p, history)
    call = _FakeClient.calls[0]
    assert call["url"] == "https://api.anthropic.com/v1/messages"
    assert call["headers"]["x-api-key"] == "sk-ant" and call["headers"]["anthropic-version"] == "2023-06-01"
    body = call["json"]
    assert body["system"] == "sys" and body["model"] == "claude-sonnet-5" and "thinking" not in body
    assert body["tools"][0]["input_schema"] == {"type": "object", "properties": {}}
    assert body["messages"] == [{"role": "user", "content": [{"type": "text", "text": "how is it going?"}]}]
    assert res.tool_calls[0].id == "toolu_5" and res.text is None

    # The agent replays the assistant tool call + result: the raw blocks
    # (thinking included) must go back verbatim.
    history += [
        {"role": "assistant", "content": None, "tool_calls": [
            {"id": "toolu_5", "type": "function", "function": {"name": "get_status", "arguments": "{}"}}]},
        {"role": "tool", "tool_call_id": "toolu_5", "content": json.dumps({"ok": True})},
    ]
    res2 = _run(p, history)
    body2 = _FakeClient.calls[1]["json"]
    assert body2["messages"][1] == {"role": "assistant", "content": first["content"]}
    assert body2["messages"][2]["content"][0]["type"] == "tool_result"
    assert res2.text == "Guiding at 0.6 arcsec." and res2.usage["total"] == 7


def test_anthropic_refusal_and_cutoff(monkeypatch):
    _install(monkeypatch, _FakeResp({"content": [], "stop_reason": "refusal"}),
             _FakeResp({"content": [{"type": "text", "text": "partial"}], "stop_reason": "max_tokens"}))
    p = AnthropicProvider("k", "claude-sonnet-5")
    assert _run(p).text == "The model declined this request."
    assert _run(p).text == "partial (reply cut short by the output limit)"


def test_anthropic_529_message(monkeypatch):
    _install(monkeypatch, _FakeResp({"error": {"type": "overloaded_error", "message": "Overloaded"}}, 529),
             _FakeResp({"error": {"type": "overloaded_error", "message": "Overloaded"}}, 529))
    with pytest.raises(RuntimeError, match="Anthropic is rate-limiting or out of credit"):
        _run(AnthropicProvider("k", "claude-sonnet-5"))


# ---- factory / selection ---------------------------------------------------
def _env(monkeypatch, **kv):
    for k in ("ASSISTANT_PROVIDER", "CANOPUS_API_PROVIDER", "CANOPUS_API_KEY", "CANOPUS_API_MODEL",
              "CANOPUS_API_BASE_URL", "CANOPUS_LOCAL_LLM_URL", "CANOPUS_PROVIDER_FACTORY"):
        monkeypatch.delenv(k, raising=False)
    for k, v in kv.items():
        monkeypatch.setenv(k, v)


def test_get_provider_prefers_api_over_local(monkeypatch):
    _env(monkeypatch, CANOPUS_API_PROVIDER="anthropic", CANOPUS_API_KEY="k", CANOPUS_API_MODEL="claude-sonnet-5",
         CANOPUS_LOCAL_LLM_URL="http://127.0.0.1:8791")
    p = providers.get_provider()
    assert isinstance(p, AnthropicProvider) and p.base_url == "https://api.anthropic.com"


def test_get_provider_openai_and_compatible(monkeypatch):
    _env(monkeypatch, CANOPUS_API_PROVIDER="openai", CANOPUS_API_KEY="k", CANOPUS_API_MODEL="gpt-5")
    p = providers.get_provider()
    assert isinstance(p, OpenAIChatProvider) and p.flavor == "openai" and p.base_url == "https://api.openai.com/v1"
    _env(monkeypatch, CANOPUS_API_PROVIDER="compatible", CANOPUS_API_KEY="k", CANOPUS_API_MODEL="m",
         CANOPUS_API_BASE_URL="https://openrouter.ai/api/v1/")
    p = providers.get_provider()
    assert isinstance(p, OpenAIChatProvider) and p.flavor == "compatible" and p.base_url == "https://openrouter.ai/api/v1"


def test_get_provider_misconfigured_raises_in_chat(monkeypatch):
    _env(monkeypatch, CANOPUS_API_PROVIDER="openai", CANOPUS_API_MODEL="gpt-5")   # no key
    p = providers.get_provider()
    with pytest.raises(RuntimeError, match="Add an API key"):
        _run(p)
    _env(monkeypatch, CANOPUS_API_PROVIDER="compatible", CANOPUS_API_KEY="k", CANOPUS_API_MODEL="m")   # no URL
    with pytest.raises(RuntimeError, match="base URL"):
        _run(get_api_provider())


def test_mock_override_still_wins(monkeypatch):
    _env(monkeypatch, ASSISTANT_PROVIDER="mock", CANOPUS_API_PROVIDER="openai", CANOPUS_API_KEY="k",
         CANOPUS_API_MODEL="gpt-5")
    assert isinstance(providers.get_provider(), providers.MockProvider)


if __name__ == "__main__":
    import sys
    sys.exit(pytest.main([__file__, "-q"]))
