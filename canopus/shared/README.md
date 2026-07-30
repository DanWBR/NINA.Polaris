# Canopus Assistant: Shared Contracts

These are the interface contracts that both sides build against:

- **FOSS host** (`nina-polaris`, AGPL), a generic, off-by-default "assistant module
  host": a badge, an intro/subscribe modal, a floating button, an iframe host, and a
  postMessage bridge. It has **no product/commercial logic**; everything specific comes
  from the cloud **manifest**.
- **Client** (`canopus/client`, AGPL): the chat UI served from the cloud and
  embedded as a cross-origin iframe.
- **Agent core** (`canopus/server`, AGPL): the provider-agnostic agent loop, the LLM
  provider interface + mock, the knowledge base, and the status monitor.
- **Hosted backend** (private): the concrete Azure OpenAI provider (endpoint + key), the
  FastAPI app, magic-link identity, Stripe billing + entitlements, and the Azure infra.
  This is the only closed part; it plugs into the open agent core via
  `CANOPUS_PROVIDER_FACTORY`.

Because the Polaris SBC may have no internet, the **client browser is the bridge**: it
talks to the cloud over the internet and executes the agent's tool calls against the
local Polaris API over the LAN. The cloud never touches the telescope.

## Files

| File | Contract | Consumed by |
|---|---|---|
| `manifest.schema.json` + `manifest.example.json` | What the FOSS host fetches to render badge/modal/iframe + the tool allowlist | FOSS host ← cloud |
| `postmessage-protocol.md` | parent (FOSS host) ⇄ iframe (client) messages | FOSS host + client |
| `agent-protocol.md` | iframe (client) ⇄ cloud WS agent channel + account/billing REST | client + cloud |
| `tools/tool-schema.md` | Tool definition + Polaris executor-mapping format | cloud (LLM tools) + FOSS host (allowlist/executor) |
| `tools/catalog.json` | Starter tool catalog (planning + status + a few mutating) | cloud + FOSS host |

## The one-artifact trick

A tool entry in `catalog.json` is **both** the LLM function-calling definition
(`name`/`description`/`parameters`) **and** the browser executor recipe (`polaris`:
method/path/query/body mapping). The cloud loads it as tools; the FOSS host loads the
`polaris` blocks as its call allowlist + executor.

## Security invariants (FOSS host enforces)

1. The host only calls Polaris endpoints present in the manifest-supplied allowlist.
2. Regardless of allowlist, a hardcoded **denylist** is always blocked:
   `/api/auth/*`, `/api/system/factory-reset`, `/api/system/power/*`, `/api/tls/*`,
   and any non-`/api/` path.
3. postMessage is origin-checked both ways against `manifest.iframe.origin`.
4. The feature is off unless the user opted in (subscribed) and enabled it.
