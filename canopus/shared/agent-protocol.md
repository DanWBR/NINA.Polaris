# Agent protocol: client (iframe) ⇄ cloud

This is between the closed client and the closed cloud (the FOSS host is not involved).
Two surfaces: a small REST API for accounts/billing, and a WebSocket for the agent
conversation. Auth is a session token (bearer) obtained after email verification;
entitlement (active subscription) is checked on the WS connect and per mutating turn.

## REST (base = `manifest.api.base`)

| method + path | body | returns |
|---|---|---|
| `POST /account/start` | `{ email }` | `{ pending: true }`. Sends a magic link. |
| `POST /account/verify` | `{ token }` | `{ session, email }`. Exchanges the magic-link token for a session. |
| `GET /account/session` | none (bearer) | `{ email, subscribed, plan, currentPeriodEnd }` |
| `POST /billing/checkout` | none (bearer) | `{ url }`. Stripe Checkout session URL. |
| `POST /billing/portal` | none (bearer) | `{ url }`. Stripe customer portal. |
| `POST /stripe/webhook` | Stripe event | 200. Internal; updates entitlement. |

Single flat plan (US$4.99/mo). No tiers. `subscribed=false` ⇒ the WS refuses agent turns
(read-only greeting only) and the FOSS FAB stays hidden.

## WebSocket `WS {api.base}/agent` (bearer session)

Messages are `{ v: 1, type, ...payload }`.

### client → cloud

| type | payload | meaning |
|---|---|---|
| `user` | `{ text, attachments? }` | A user turn. |
| `approve` | `{ planId }` | Approve a proposed plan; the cloud begins executing its steps. |
| `reject` | `{ planId, reason? }` | Reject; the cloud revises or asks. |
| `answer` | `{ questionId, choices }` | Answer a single/multiple-choice question (`choices` is an array; length 1 for single). |
| `tool-result` | `{ id, ok, result?, error? }` | Result of a tool-call the cloud requested (relayed from the parent via postMessage). |
| `status` | `{ snapshot }` | A forwarded rig snapshot (throttled, ~2s). Compact live state the agent reasons over and the real-time watcher scans. Shape: `{ tab, mount:{connected,ra,dec,tracking,slewing}, camera:{connected,temperature}, guider:{guiding,rmsTotal,appState}, focus:{hfr}, meridian:{minutesToFlip}, liveStack:{active} }` (fields absent when the device is missing). |
| `cancel` | `{}` | Abort the current turn / plan execution. |

### cloud → client

| type | payload | meaning |
|---|---|---|
| `assistant` | `{ text, streaming?, done? }` | Assistant chat text (may stream in deltas). |
| `notice` | `{ key, text, severity? }` | A proactive, rule-based session alert (guiding lost, RMS spike, meridian soon, focus drift, mount/camera dropout). Pushed unprompted by the real-time watcher (no LLM call) when a forwarded `status` snapshot crosses a threshold (edge-triggered, per-`key` cooldown). `severity` is `warn` (default) or `info`. The client renders it as a distinct heads-up bubble. |
| `plan` | `{ planId, title, steps: [{ n, summary, tool?, mutates }], rationale? }` | A proposed plan to approve/review/reject. |
| `question` | `{ questionId, prompt, options: [{ id, label, description? }], multi }` | A single/multiple-choice question. |
| `tool-call` | `{ id, tool, method, path, query?, body? }` | An intent to call a Polaris endpoint. The client relays it to the parent and returns a `tool-result`. |
| `ui` | `{ id, action, params }` | An intent to steer the Polaris UI (e.g. `navigate` to a panel) so the user SEES the action. The client relays it to the parent as `assistant:ui` and returns a `tool-result`. |
| `watch` | `{ on }` | Ask the client to start/stop status forwarding (client relays to parent as `assistant:watch`). |
| `done` | `{}` | Turn complete. |
| `error` | `{ message, code? }` | Error (auth, entitlement, provider, etc.). |

### Interaction model (Claude-Code-style Auto)

1. `user` → cloud reasons → emits a `plan` (with per-step `mutates` flags) and/or a
   `question`.
2. Client renders plan cards + choice widgets. User approves/answers.
3. On `approve`, the cloud walks the steps, emitting `tool-call` intents; the client
   bridges each to the parent and returns `tool-result`.
4. Read-only steps may run without a plan; anything with `mutates:true` only runs inside
   an approved plan (or a per-action confirm the client shows).
5. `done`. Mid-session, forwarded `status` lets the cloud propose corrections as a new
   `plan`.
