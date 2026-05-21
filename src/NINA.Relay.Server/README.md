# NINA Relay Server

Reverse-tunnel relay so NINA Headless instances behind a NAT (typical
home or remote-observatory setup) can be reached from the public internet
without inbound port-forwarding or DDNS.

```
 Browser  ──HTTPS──►  relay.example.com  ──reverse WebSocket──►  Raspberry Pi
                       (this project)                              (NINA Headless,
                                                                    no public IP)
```

The NINA Headless instance opens an **outbound** WebSocket to this relay
server, authenticates with a bearer token, and stays connected. The relay
exposes a public HTTP endpoint and forwards browser requests through that
tunnel.

## Status & scope (first cut)

Implemented:
- [x] Outbound WebSocket registration + authentication
- [x] Multiplexed binary framing (multiple concurrent HTTP requests on one tunnel)
- [x] HTTP request/response forwarding (GET / POST / PUT / DELETE / PATCH / HEAD / OPTIONS)
- [x] Subdomain routing (`alice.relay.example.com`) **and** path-prefix routing (`/t/alice/...`)
- [x] Auto-reconnect with exponential backoff on the client side
- [x] Ping/pong keepalive (30 s)
- [x] Per-request timeout (default 60 s)
- [x] `/_health` and `/_tunnels` admin endpoints

Not yet:
- [ ] WebSocket forwarding (image stream / status stream won't work over the
  relay yet — REST API and static UI do). Sketch: opcodes `WsOpen` / `WsMessage`
  / `WsClose` are already reserved in the protocol; the server-side proxy
  needs an upgrade detector + the client needs to open a local WS and proxy
  messages bidirectionally.
- [ ] TLS termination (run it behind nginx / Caddy / Traefik for HTTPS)
- [ ] Persistent tenant store (currently in-memory from `appsettings.json`)
- [ ] Per-tenant rate limiting & quotas

## Configuration

```jsonc
// appsettings.json on the relay server
{
  "Kestrel": {
    "Endpoints": { "Http": { "Url": "http://0.0.0.0:6000" } }
  },
  "Proxy": {
    "TimeoutSeconds": 60,
    // Hosts ending in this suffix route by subdomain.
    // Set to null/empty to use only path-prefix routing.
    "HostnameSuffix": ".relay.example.com"
  },
  "Tenants": {
    // <token>: <hostname-slug>
    "REPLACE-WITH-LONG-RANDOM-TOKEN-PER-USER": "alice"
  }
}
```

Generate tokens with any source of entropy:
```bash
openssl rand -hex 32
```

On the NINA Headless side, add to `appsettings.json` or env vars:

```jsonc
{
  "Relay": {
    "Enabled": true,
    "ServerUrl": "wss://relay.example.com/_tunnel",
    "Token": "the-same-long-random-token-from-the-server"
  }
}
```

(env-var form: `Relay__Enabled=true Relay__ServerUrl=… Relay__Token=…`)

Once it connects, the browser can reach the headless instance at
`https://alice.relay.example.com/` (subdomain routing) or
`https://relay.example.com/t/alice/` (path-prefix routing).

## Running

Local test (no TLS):

```bash
cd src/NINA.Relay.Server
dotnet run
# → listens on http://0.0.0.0:6000
```

Production: put it behind nginx or Caddy for TLS + Let's Encrypt.

Example Caddyfile:

```caddyfile
*.relay.example.com, relay.example.com {
    reverse_proxy 127.0.0.1:6000
}
```

## Self-hosting vs hosted

Two reasonable deployment modes:

1. **You host it once for your users**: cheap (a single $5/month VPS handles
   dozens of low-traffic tunnels), but you take on uptime + abuse
   responsibility, need TLS, need to manage tokens.

2. **Each user self-hosts**: any small VPS with a public IP works; the user
   issues their own tokens. The compose file in the repo root can run both
   the relay server and an indiserver alongside NINA Headless.

The protocol and wire format are identical either way.

## Wire protocol (one-page summary)

Every relay frame on the tunnel WebSocket is binary:

```
+--------+--------+--------+--------+--------+--------+--------+--------+--------+
| opcode |    stream id (uint32 BE)          |    length (int32 BE)              | payload...
+--------+--------+--------+--------+--------+--------+--------+--------+--------+
```

| Opcode | Direction | Name         | Payload                                   |
|--------|-----------|--------------|-------------------------------------------|
| 0x01   | C → S     | Auth         | bearer token (UTF-8)                      |
| 0x02   | S → C     | AuthOk       | assigned hostname slug (UTF-8)            |
| 0x03   | S → C     | AuthFail     | reason (UTF-8)                            |
| 0x10   | S → C     | HttpRequest  | textual header block + body bytes         |
| 0x11   | C → S     | HttpResponse | textual header block + body bytes         |
| 0x20   | S → C     | WsOpen       | reserved (not implemented)                |
| 0x21   | C → S     | WsOpenAck    | reserved                                  |
| 0x22   | both      | WsMessage    | reserved                                  |
| 0x23   | both      | WsClose      | reserved                                  |
| 0xF0   | both      | Ping         | empty                                     |
| 0xF1   | both      | Pong         | empty                                     |

HTTP request payload format:
```
<METHOD> <pathAndQuery>\r\n
<Header-Name>: <value>\r\n
... (zero or more)
\r\n
<body bytes>
```

HTTP response payload format: same shape, status code on line 1.

Hop-by-hop headers (Connection / Keep-Alive / TE / Trailer /
Transfer-Encoding / Upgrade / Proxy-* / Host) are stripped before
serialisation. `X-Forwarded-Host`, `X-Forwarded-For`, `X-Forwarded-Proto`
are added by the relay so the headless instance knows the original
request context.
