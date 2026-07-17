# Tool schema

One catalog entry is **both** the LLM function-calling definition and the browser executor
recipe. The cloud loads `name`/`description`/`parameters` as tools; the FOSS host loads the
`polaris` block as its call recipe + (with the manifest allowlist) its whitelist.

## Entry shape

```jsonc
{
  "name": "get_tonights_best",            // function-calling name (snake_case)
  "description": "…for the LLM…",          // when + why to use it
  "category": "planning",                  // planning|status|mount|focus|guide|capture|livestack|postproc
  "mutates": false,                        // true = moves hardware / changes running session
  "requiresApproval": false,               // true = only run inside an approved plan or per-action confirm
  "parameters": {                          // JSON Schema for the LLM args
    "type": "object",
    "properties": { "limit": { "type": "integer", "default": 20 } },
    "required": []
  },
  "polaris": {                             // executor recipe (browser)
    "method": "GET",
    "path": "/api/sky/tonights-best",      // may contain {placeholders} filled from args/context
    "query": { "lat": "$ctx.lat", "lon": "$ctx.lon", "limit": "$args.limit" },
    "body": null                            // for POST/PUT: object with $args.* / $ctx.* substitution
  }
}
```

## Substitution tokens (resolved by the FOSS host executor)

- `$args.X` — the tool argument `X` (from the LLM call).
- `$ctx.X` — context the host knows locally: `lat`, `lon`, `elevation` (from Polaris
  profile), `activeRigId`, and anything the host chooses to expose. Never secrets.
- Literals pass through unchanged.
- `null`/absent `query`/`body` are omitted.

Path templates (`/api/plan/{id}/start`) take `{id}` from `$args.id`.

## Flags drive safety

- `mutates:false, requiresApproval:false` → runs freely (reads).
- `mutates:true` → the client requires an approved plan (or a per-action confirm) before
  emitting the `tool-call`. The host allowlist/denylist is defense-in-depth.

## UI tools (client-side)

A tool may carry a `ui` block instead of `polaris` — a client-side action that
steers the Polaris UI (no API call). The client emits `assistant:ui` (not
`assistant:tool-call`); the host runs it from its fixed, safe vocabulary
(see postmessage-protocol.md). Example:

```jsonc
{
  "name": "show_panel",
  "description": "Switch the Polaris UI to a panel so the user sees what you're doing.",
  "category": "ui",
  "mutates": false,
  "requiresApproval": false,
  "parameters": { "type": "object", "properties": { "tab": { "type": "string" } }, "required": ["tab"] },
  "ui": { "action": "navigate", "params": { "tab": "$args.tab" } }
}
```

UI tools are non-destructive (they only show panels), so `mutates:false` and no
approval — the whole point is to guide the user by navigating.

### Local tools (cloud-side)

An entry with a `local` marker runs **in the cloud agent**, not on the browser or
Polaris. There is no `tool-call` over the WS and no allowlist entry; the agent
resolves it in-process and feeds the result straight back to the model. Used for
retrieval over the cloud-hosted astrophotography knowledge base (`knowledge.py`).

```json
{ "name": "search_knowledge", "local": "knowledge", "mutates": false, "requiresApproval": false }
```

### Image tools (vision)

An entry with an `image` block (instead of `polaris`) fetches a frame for the
assistant's vision model. It carries `returnsImage: true`; the agent emits a
`tool-call` with `responseType:"image"`, the host executes the GET, downscales the
frame to a JPEG data URL, and returns `{ dataUrl }`. The agent then attaches that
image as a follow-up `user` message so the model inspects it directly.

```json
{
  "name": "analyze_frame", "returnsImage": true, "mutates": false, "requiresApproval": false,
  "image": { "method": "GET", "path": "/api/image/latest/preview", "query": { "quality": "85" } }
}
```

## Coverage

`catalog.json` seeds planning + status + a few mutating tools (P0). P4 expands to full
capture/focus/guide/live-stack/post-processing coverage across the 57 Polaris endpoint
groups (see `nina-polaris/docs/api-reference.md`).
