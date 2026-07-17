# Canopus Assistant

The open source of **Canopus Assistant** — the optional AI co-pilot for
[N.I.N.A. Polaris](https://github.com/DanWBR/NINA.Polaris). It plans the night,
drives the rig **with your approval**, inspects your frames, and answers
astrophotography questions, all inside the Polaris web UI.

Almost all of Canopus lives here under the AGPLv3, the same licence as Polaris
itself. The one part that stays private is the **hosted backend that talks to a
commercial LLM** (Azure OpenAI) and runs the paid subscription — that is where
the API keys, subscriber accounts, and billing live, and it is what the €/month
funds. Everything you need to understand, run, and extend the assistant offline
is in this folder.

## What's here (open, AGPLv3)

| Path | What it is |
|---|---|
| `server/agent.py` | The provider-agnostic **agent loop** — turns a request into a plan of tool calls, gates mutating tools behind an approval. Never touches the telescope. |
| `server/providers.py` | The **LLM provider interface** + a deterministic `MockProvider`. Ships mock-only so the whole loop runs with no keys. |
| `server/knowledge.py` + `server/knowledge/**` | The **astrophotography knowledge base** (RAG) — the curated docs the assistant grounds its answers in, and the retriever over them. |
| `server/monitor.py` | Turns a live rig-status snapshot into plain-language alerts. |
| `shared/tools/` | The **tool catalog** (`catalog.json`) + schema — one entry is *both* the LLM function definition and the browser executor recipe. |
| `shared/*.md`, `shared/*.json` | The wire contracts: the manifest schema, the parent↔iframe postMessage protocol, and the client↔cloud agent protocol. |
| `client/` | The **chat panel** web app served as an embedded iframe. |

## What's NOT here (private, commercial)

The hosted service that this open code plugs into — kept in a separate private
repo:

- The concrete **Azure OpenAI provider** (the thing that holds the endpoint +
  API key and calls the model).
- The **subscription backend**: the FastAPI app, magic-link identity, Stripe
  billing and entitlements, account storage, usage metering, and the Azure
  infrastructure.

None of that is required to run or develop against the open code.

## Run the open agent offline (mock, no keys)

```bash
cd server
python -m pip install -r requirements.txt   # or into a venv
python -c "import providers, agent, knowledge, monitor; print('ok')"
```

`providers.get_provider()` returns the `MockProvider` by default, so the agent
loop, the tool round-trip, and the knowledge retrieval all work with no network
and no API keys — enough to develop and test the whole thing.

## Plugging in a real model (production)

The open `get_provider()` looks up an optional factory from the environment:

```bash
export CANOPUS_PROVIDER_FACTORY="my_azure_provider:get_provider"
```

`my_azure_provider` is a module — living OUTSIDE this open repo — that returns a
`Provider` subclass wrapping your hosted model. When the variable is unset (or the
import fails), the loop falls back to the mock. That is the entire seam between
the open assistant and the private, key-holding backend.

## How it stays safe (design)

The cloud never contacts your telescope. When the agent decides to act, it hands
that **intent** to your browser, which executes it against your **local** Polaris
API over your LAN — so the Polaris SBC needs no inbound internet, and mutating
actions (slew, autofocus, start/stop a sequence, meridian flip) only run after
you approve the proposed plan. The FOSS Polaris app is a neutral, off-by-default
host that renders whatever the cloud manifest describes; it carries no
assistant-specific product logic.

See `shared/README.md` for the wire contracts and the security invariants the
host enforces.
