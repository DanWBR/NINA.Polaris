# Relay (remote internet access)

By default Polaris listens on your LAN only, port 5000 on the Pi.
To access it from outside (cellular, remote dark site, friend's
network) without exposing the Pi to the internet directly, Polaris
ships with a **relay server** mode: a TLS-tunneled HTTPS endpoint
hosted on a cloud VPS that forwards traffic to your local Polaris
through an outbound WebSocket tunnel.

Architecture:

```
Mobile browser ──HTTPS──► relay.example.com:443 ──WS tunnel──► Pi:5000 (local Polaris)
                                  ▲                                 ▼
                                  └────── auth tokens + TLS ────────┘
```

The Pi initiates an outbound WebSocket connection to the relay server.
All inbound HTTPS traffic from browsers goes through the relay +
gets forwarded over the open WebSocket. No port forwarding, no
dynamic DNS, no exposing the Pi directly.

## When you need this

- **Remote site imaging**, Pi at your dark-sky location, you're
  hours away
- **Tablet imaging while inside the house**, fine without Relay if
  on the same WiFi
- **Friend wants to check on your setup**, share a tenant token

## When you don't

- Tablet/phone on the same WiFi as the Pi, just hit `http://nina.local:5000`
- Always-local Pi in your backyard, LAN only is simpler + faster

## Server-side setup (one-time)

The relay server is a separate ASP.NET Core project
(`src/NINA.Relay.Server`). Build + deploy to any VPS with a public
hostname:

1. DNS A record: `relay.yourdomain.com → VPS-IP`
2. Open ports 443 + (optionally 80 for ACME redirect)
3. Run: `./NINA.Relay.Server`. It uses LettuceEncrypt to obtain a
   real Let's Encrypt cert automatically on first request
4. Web admin UI at `https://relay.yourdomain.com/admin` (separate
   admin token in env var)
5. Create your first tenant: `polaris-yourname` + generates 32-char
   token + sets quota (monthly bytes, expiring tokens, per-tenant
   audit log)

Persistent state in `tenants.json` (JSON file backed up easily).

## Client-side setup (per Pi)

In `appsettings.json` on the Pi:

```jsonc
{
  "Relay": {
    "Enabled": true,
    "ServerUrl": "wss://relay.yourdomain.com/tunnel",
    "TenantId": "polaris-yourname",
    "Token": "the-32-char-token-from-server-admin",
    "ReconnectIntervalSeconds": 5,
    "UseMtls": false  // set true + provide ClientCert/Key files for mTLS
  }
}
```

Restart Polaris → the `RelayClient` hosted service opens the
WebSocket tunnel to the server. Status visible in the activity bar
("Relay: ON").

## Access from browser

Once tunnel is established, the relay serves the Polaris UI at:

```
https://relay.yourdomain.com/t/polaris-yourname/
```

Login prompt asks for the tenant token. Same UI as `http://nina.local:5000`
, just routed through the relay.

WebSocket streams (`/ws/status`, `/ws/image-stream`) tunnel through
too, so the LIVE preview + sequence updates work in real time.

## Security features

The relay server is hardened for direct internet exposure:

- **TLS** via LettuceEncrypt (free Let's Encrypt certs, auto-renewed)
- **Per-tenant tokens** with optional expiration dates
- **Per-tenant monthly byte quotas**, auto-throttle when exceeded
- **Per-tenant rate limits**, request count per second
- **Per-tenant audit log**, every request logged with timestamp, IP,
  endpoint, byte size
- **mTLS for tunnel auth** (optional), beyond the bearer token, the
  Pi presents a client cert
- **Admin token** for the web admin UI (separate from tenant tokens)

## Tunnel behavior

The Pi's `RelayClient` reconnects on every interruption with a 5-second
backoff. Brief network drops are invisible to the browser side, the
relay buffers a short window of inbound requests and replays them
when the tunnel comes back.

## Common pitfalls

**Browser shows "Tunnel not connected" 502**, Pi's `RelayClient`
isn't running or the token is wrong. Check Polaris logs on the Pi.

**Connection drops every 30s**, your VPS provider's idle WS timeout
is shorter than the default keepalive. Tune `WebSocket:PingInterval`
in `appsettings.json`.

**Quota exceeded**, monthly cap. Check `/admin` → tenant detail.
Image stream is the bandwidth hog; switch to JPEG mode (lower bitrate)
or live with status-only access until the month rolls over.

## See also

- [Installation](installation.md), LAN access setup
- Relay-specific docs: `docs/relay-deployment.md` (server build +
  deploy), `docs/relay-tls.md` (LettuceEncrypt config), `docs/relay-mtls.md`
  (client cert setup)
