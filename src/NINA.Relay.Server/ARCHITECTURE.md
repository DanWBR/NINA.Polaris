# NINA.Relay.Server, Architecture

Standalone ASP.NET Core service deployed to a **public VPS**. Lets
Polaris instances (running on private LANs at telescope locations) be
accessed from anywhere on the internet through a TLS-tunneled
reverse-proxy, without exposing the Pi directly.

This is a **completely separate process** from `NINA.Polaris`. They
share types via `NINA.Relay.Protocol`, and the Pi-side client lives
in `src/NINA.Polaris/Services/RelayClient.cs`.

For the end-user perspective + deployment guide, read
[docs/user-guide/relay.md](../../docs/user-guide/relay.md).

## Layout

```
src/NINA.Relay.Server/
  NINA.Relay.Server.csproj
  Program.cs                     # Kestrel + LettuceEncrypt + endpoint mapping
  appsettings.json               # admin token, ACME config, port bindings
  tenants.sample.json            # example tenant store

  TunnelHandler.cs               # accepts the outbound WS from each Pi
  PublicProxy.cs                 # accepts inbound HTTPS from browsers,
                                 # forwards over the matching tunnel
  TenantConfig.cs                # per-tenant settings record
  TenantRegistry.cs              # CRUD over tenants.json
  TenantUsageStore.cs            # monthly byte counters per tenant
  RateLimiter.cs                 # token-bucket rate limit per tenant
  AuditLog.cs                    # per-tenant request log
  wwwroot/                       # /admin web UI (HTML/JS)
  Properties/                    # launchSettings
```

## Architecture

```
                          ┌───────────────────────┐
   browser ── HTTPS ────► │  relay.example.com    │
   (https://...)          │  (NINA.Relay.Server)  │
                          └──────────┬────────────┘
                                     │   tunneled HTTP
                                     │   request frame
                                     ▼
                          ┌───────────────────────┐
                          │  WS tunnel (outbound  │
                          │  from Pi, kept open)  │
                          └──────────┬────────────┘
                                     │
   ┌────────────────────────────────▼─────────────────────────────┐
   │ Pi running NINA.Polaris + RelayClient (initiates tunnel)    │
   └──────────────────────────────────────────────────────────────┘
```

The Pi initiates the tunnel **outbound** (no port forwarding, no
inbound firewall rules on the Pi side). Browser traffic arrives at
the public relay over HTTPS and gets forwarded through the open
WebSocket tunnel back to the Pi.

## The two main handlers

### `TunnelHandler`

Endpoint: `wss://relay.example.com/tunnel`.

Each Polaris Pi connects here once + keeps the WS open. On HELLO:

1. Reads `HelloFrame { tenantId, token, protocolVersion }`
2. Validates against `TenantRegistry`
3. Sends `HelloAckFrame { ok, error?, serverVersion }`
4. On success, registers this WS as the active tunnel for `tenantId`
   in an in-memory dictionary
5. Pumps frames in both directions until the WS closes

mTLS optional, if `UseMtls = true` in the tenant config, the Pi
also presents a client cert in addition to the bearer token.

### `PublicProxy`

Endpoint: `https://relay.example.com/t/{tenantId}/{**path}`.

For each browser HTTP request:

1. Looks up the active tunnel for `tenantId`
2. If no tunnel → 502 "Tunnel not connected"
3. Builds an `HttpRequestFrame { method, path, headers, body }`
4. Sends through the tunnel
5. Awaits the matching `HttpResponseFrame` (correlated by request ID)
6. Writes the response back to the browser
7. Records byte counts in `TenantUsageStore` + log entry in `AuditLog`

WebSocket upgrades (`/ws/status`, `/ws/image-stream`) follow the same
path but with `WsOpenFrame` → `WsMessageFrame` bidirectional pumping.

## Security features

- **TLS** via [LettuceEncrypt](https://github.com/natemcmaster/LettuceEncrypt)
 , auto Let's Encrypt cert on first request, auto-renewed
- **Per-tenant tokens** (32-char) with optional expiration date
- **Per-tenant monthly byte quotas**, throttled when exceeded
- **Per-tenant rate limits**, token bucket per second
- **Per-tenant audit log**, every request: timestamp, IP, endpoint,
  byte size, response code
- **mTLS**, optional client-cert auth on the tunnel WS in addition
  to the bearer token
- **Admin token** for the `/admin` web UI (separate from tenant tokens)

## Persistence

`tenants.json`, JSON file on disk. Backed up easily, edited by hand
in emergencies. `TenantRegistry` loads on startup + writes on every
mutation through the `/admin` UI.

`TenantUsageStore` and `AuditLog` write to per-tenant SQLite files
(`audit-{tenantId}.db`, `usage-{tenantId}.db`).

## Deployment

The relay server is deployed independently from Polaris. Typical
setup:

```bash
# On the VPS
cd NINA.Relay.Server/bin/Release/net10.0/linux-x64/publish
./NINA.Relay.Server
```

DNS A record → VPS-IP. Open ports 443 (+ 80 for ACME). First request
to the public hostname triggers LettuceEncrypt to mint the cert.

See [docs/user-guide/relay.md](../../docs/user-guide/relay.md) for
the end-to-end procedure.

## See also

- [NINA.Relay.Protocol/ARCHITECTURE.md](../NINA.Relay.Protocol/ARCHITECTURE.md)
 , wire types
- `src/NINA.Polaris/Services/RelayClient.cs`, the Pi-side client
- [docs/user-guide/relay.md](../../docs/user-guide/relay.md)
- [Root ARCHITECTURE.md](../../ARCHITECTURE.md)
