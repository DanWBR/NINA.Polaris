# Canopus Assistant: agent core (open)

The open, AGPLv3 heart of the assistant: the provider-agnostic **agent loop**,
the **LLM provider interface** with a keyless mock, the **knowledge base** (RAG),
and the **status monitor**. It is fully runnable and testable here with no API
keys and no network.

| Module | Role |
|---|---|
| `agent.py` | The agent loop. Consumes the shared agent protocol, emits `tool-call` / `ui` **intents** over the WebSocket, and gates mutating tools behind an approvable `plan`. Provider-agnostic. |
| `providers.py` | `Provider` interface + `ProviderResult`/`ToolCall` + a deterministic `MockProvider`. `get_provider()` returns the mock unless a private backend is plugged in via `CANOPUS_PROVIDER_FACTORY`. |
| `knowledge.py` + `knowledge/**` | The curated astrophotography knowledge base and a lightweight retriever. `KnowledgeBase().search(q)` returns the top matching sections. |
| `monitor.py` | Turns a live rig-status snapshot into plain-language `Alert`s. |
| `test_*.py` | Unit tests for the above; they run against the mock. |

## Run / test locally (mock, no keys)

```bash
cd server
python -m pip install -r requirements.txt
python -m pytest -q            # test_agent / test_knowledge / test_monitor
```

`get_provider()` returns the `MockProvider`, so the agent loop, tool round-trip,
and knowledge retrieval all work offline.

## What is NOT in this open tree

The hosted subscription service that wraps this core lives in a separate private
repo and is not required here:

- the concrete Azure OpenAI provider (endpoint + API key), plugged in at runtime
  via `CANOPUS_PROVIDER_FACTORY="module:factory"`;
- the FastAPI app, magic-link identity, Stripe billing + entitlements, account
  storage, usage metering, and the Azure infrastructure.

## Cloud API mode (the user's own key)

`providers_api.py` adds `AnthropicProvider` (Messages API) and `OpenAIChatProvider`
(Chat Completions, also any OpenAI-compatible server). The Polaris host stores the
key in `{DataDir}/canopus/api-config.json` and launches this server with:

| Variable | Meaning |
|---|---|
| `CANOPUS_API_PROVIDER` | `anthropic`, `openai` or `compatible` |
| `CANOPUS_API_KEY` | the key |
| `CANOPUS_API_MODEL` | model id |
| `CANOPUS_API_BASE_URL` | optional; required for `compatible`. The /v1 root for openai and compatible, the host root for anthropic |
| `CANOPUS_API_MAX_TOKENS` | optional output cap, default 8192 |

No `CANOPUS_LOCAL_TIER`, so the full catalog, the full system prompt and the
vision tools apply. The startup warm-up is skipped (it would cost tokens). The
manifest keeps `tier: "local"` and names the model in `product.name`.
`get_provider()` picks this mode before the local llama-server one.

Anthropic replays the raw content of every tool-use reply (thinking blocks
included) when the agent sends the history back; the agent keeps provider tool-call
ids for that reason (`AgentSession._call_id`).
