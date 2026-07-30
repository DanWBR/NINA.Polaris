# postMessage protocol: FOSS host (parent) ⇄ client (iframe)

The parent is the Polaris page (LAN origin, AGPL host). The iframe is the Assistant chat
UI served from `manifest.iframe.origin` (cloud). All messages are
`{ v: 1, type, ...payload }`. **Both sides validate `event.origin`**: the parent accepts
only `manifest.iframe.origin`; the iframe accepts only the Polaris page origin passed in
`host:init`.

The parent is the ONLY party that touches the local Polaris API. The iframe never calls
Polaris directly (cross-origin + mixed-content); it asks the parent to.

## parent → iframe

| type | payload | meaning |
|---|---|---|
| `host:init` | `{ parentOrigin, protocolVersion, polaris: { version, baseUrl }, locale, theme, ui: { font, zoom, padScale } }` | Sent after `assistant:ready`. Establishes the accepted parent origin + context. `ui` mirrors Polaris' Appearance settings (font key, page zoom 0.5 to 1.5, control-density percent) so the cross-origin chat can match the app. |
| `host:ui` | `{ ui: { font, zoom, padScale } }` | The user changed a Polaris Appearance setting; the iframe re-applies it live (no reload). |
| `host:tool-result` | `{ id, ok, result?, error? }` | Response to an `assistant:tool-call`. `id` echoes the request. |
| `host:status` | `{ snapshot }` | A compact `/ws/status` snapshot (see agent-protocol for shape). Sent when the iframe asked via `assistant:watch`. Throttled by the host (default ≥2 s). |
| `host:visibility` | `{ open }` | The chat panel was shown/hidden by the FAB. |
| `host:auth` | `{ hasPolarisSession }` | Whether the parent currently holds a valid Polaris session (so the iframe can warn if not). |

## iframe → parent

| type | payload | meaning |
|---|---|---|
| `assistant:ready` | `{}` | Iframe loaded; waiting for `host:init`. |
| `assistant:tool-call` | `{ id, method, path, query?, body? }` | Ask the parent to execute a Polaris API call. The parent checks the allowlist + denylist, calls it with the user's session, replies `host:tool-result`. |
| `assistant:ui` | `{ id, action, params }` | Ask the parent to perform a curated, non-destructive UI action so the assistant can SHOW the user what it is doing. The parent replies `host:tool-result`. Fixed vocabulary (below); no arbitrary DOM/JS. |
| `assistant:capture-view` | `{ id, maxDim?, quality? }` | Ask the parent to snapshot the image in whatever panel is CURRENTLY selected (LIVE/PREVIEW/FOCUS/VIDEO/AUTORUN, or the FILES viewer). Client-side canvas grab: no Polaris API call, so no allowlist. Replies `host:tool-result { id, ok, result: { dataUrl, tab, width, height } }`; `ok:false` with a message when the active panel shows no image. Feeds the assistant's vision model (tool `analyze_current_view`). |
| `assistant:watch` | `{ on: boolean }` | Start/stop forwarding `/ws/status` snapshots as `host:status`. |
| `assistant:subscribed` | `{ subscribed: boolean }` | Reports entitlement so the parent reveals/hides the FAB and dismisses onboarding. |
| `assistant:notify` | `{ level: "info"\|"warn"\|"error", text }` | Ask the parent to show a Polaris toast. |
| `assistant:open-external` | `{ url }` | Ask the parent to open a URL in a new tab (Checkout, privacy, manage). Parent validates it's https and one of the manifest URLs' origins. |
| `assistant:resize` | `{ height }` | Optional; the panel is fixed-size, so usually ignored. |
| `assistant:close` | `{}` | User closed the chat from inside the iframe. |

## Tool-call execution (parent)

On `assistant:tool-call`:
1. Reject if `path` is not `/api/...`, or matches the hardcoded denylist
   (`/api/auth/*`, `/api/system/factory-reset`, `/api/system/power/*`, `/api/tls/*`).
2. Reject if `{method, path}` is not in the manifest `allowlist` (path templates like
   `/api/plan/{id}/start` match `/api/plan/123/start`).
3. Execute via the existing authenticated Polaris fetch (same session/cookie the page uses).
4. Reply `host:tool-result { id, ok, result | error }`. Never expose the session token to
   the iframe; only the response body.

Approval (approve/review/reject) and the mutate warnings happen inside the iframe UI
BEFORE it emits a tool-call. The parent allowlist/denylist is defense-in-depth, not the
primary gate.

## UI actions (parent)

So the assistant can *show* the user what it is doing (jump to FOCUS while it
adjusts focus, open LIVE to reveal the stack), the parent exposes a small, fixed,
**non-destructive** UI vocabulary. It never runs arbitrary DOM/JS.

`assistant:ui { id, action, params }`:

| action | params | effect |
|---|---|---|
| `navigate` | `{ tab }` | Switch the app to a sidebar panel and run the same init the sidebar button does. |

`tab` (canonical): `home`, `equip`, `polar`, `sky`, `tonight`, `weather`, `focus`,
`guide`, `preview`, `sequence` (Autorun), `plan`, `live`, `video`, `seqadv`
(Advanced Sequencer), `files` (Studio/Editor), `settings`, `help`. Friendly
aliases accepted: `equipment`/`rigs`→`equip`, `autorun`→`sequence`,
`studio`/`editor`→`files`, `advanced`/`sequencer`→`seqadv`.

Reply is the shared `host:tool-result { id, ok, result?, error? }`. New safe verbs
(e.g. open a specific modal, highlight an element) can be added to this vocabulary
later; the host never accepts anything outside it.

## Handshake sequence

```
iframe loads → assistant:ready →
parent host:init →
(user subscribes in iframe) → assistant:subscribed{true} → parent shows FAB →
user chats → cloud proposes plan → user approves in iframe →
cloud → tool-call intent → iframe assistant:tool-call → parent executes → host:tool-result → iframe → cloud
iframe assistant:watch{on:true} → parent streams host:status snapshots
```
