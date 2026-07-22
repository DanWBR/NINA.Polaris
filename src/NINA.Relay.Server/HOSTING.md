# Operating a hosted relay for many users

This is a reference for anyone who wants to run a **shared, hosted relay** for
Polaris users — the way ZWO's "Telescope Network" (Seestar / ASIAIR) gives
zero-config remote access from a vendor-operated cloud — instead of asking each
user to stand up their own VPS.

Nobody operates such an instance today, and this project intentionally does not:
the default posture is **LAN-only**, and the relay is **self-host-first and off
by default** (see [`README.md`](README.md) and
[`../../book/27-networking.qmd`](../../book/27-networking.qmd)). This document
exists so the feasibility, options, and costs are written down for whoever
decides to try it.

## What is (and isn't) already built

The relay is **not** a thing you need to write. It ships complete and tested:

- `NINA.Relay.Server` — the cloud-side server, already **multi-tenant**: per-tenant
  tokens, hostnames, rate limits (requests/s + bytes/s), **monthly byte quotas**
  with a persistent counter, expiring tokens, optional mTLS, a web admin UI, and a
  per-request audit log. Built-in Let's Encrypt TLS.
- `NINA.Relay.Protocol` — the binary multiplexing wire protocol.
- `RelayClient` (in the Polaris host) — the outbound tunnel client, off unless
  `Relay:Enabled=true`.

So the missing piece for a "Telescope Network"-style experience is **not code** —
it is **someone operating an instance** and handing out tenant tokens. The
per-tenant quota/rate-limit machinery that makes a shared instance safe to run is
already there.

## The real decision: who hosts

| Option | What it is | Project $ cost | User friction | Notes |
|--------|-----------|----------------|---------------|-------|
| **A. Self-host only (status quo)** | Every user runs their own relay VPS | $0 | High (needs VPS + domain) | Fine for technical users; the opposite of the ZWO UX |
| **B. Project-run shared relay** | You run *one* multi-tenant instance, issue tokens | ~$5–15/mo (see cost model) | None (zero-config) | The ZWO model. Code is ready; cost is dominated by bandwidth |
| **C. Managed third-party tunnel** | Cloudflare Tunnel / Tailscale Funnel / ngrok / frp | $0–low | Medium | Offloads bandwidth, TLS, DDoS; ToS limits on heavy streaming; per-user accounts add friction |
| **D. P2P WebRTC** | Browser↔host connect directly; relay only for signaling + TURN fallback | Lowest at scale | None | Cuts cloud bandwidth to near-zero for most users, but is the **only option that needs new engineering** (port the transport to a WebRTC data channel) |

Options B and C reuse the tunnel that already exists. Option D is a rewrite of the
transport and should only be considered if bandwidth actually becomes the
bottleneck.

## Cost model (bandwidth is the variable that matters)

A relay's real cost is **egress**, and it swings ~10x depending on where you host.

Rough Polaris consumption per active remote user:

- Casual monitoring (status stream + a preview JPEG per sub): **~50–200 MB/night**.
- Continuous live view / focus loop (JPEG ~200 KB at ~1 fps): **~0.7 GB/hour** —
  this is the blow-up case.
- Pulling a full-resolution light: **30–60 MB each**.

A realistic average for an active remote user is **~5 GB/month**. 200 active users
≈ **1 TB/month**. What that 1 TB costs, by host (approximate, 2026 ballpark):

| Host | Base price | Included transfer | 1 TB/mo effective cost |
|------|-----------|-------------------|------------------------|
| Hetzner CX22 | ~€4.5/mo | 20 TB | **covered** (~€4.5) |
| OVH / DigitalOcean | ~$6/mo | 1–2 TB | **covered** (~$6) |
| Cloudflare Tunnel in front | $0 egress | effectively unlimited* | **~$0** (*subject to ToS) |
| AWS / GCP / Azure | egress $0.08–0.12/GB | ~0 | **~$90/mo** |

Plus a wildcard domain (`*.relay.example.com`, ~$12/yr) and TLS (Let's Encrypt,
$0 — already built in via LettuceEncrypt).

**Takeaway:** on a VPS with bundled bandwidth (Hetzner/OVH) or behind Cloudflare, a
shared relay for **hundreds of users costs on the order of $5–15/month**. On a
hyperscaler, don't — egress alone makes it uneconomical. This matches the
README's "a single $5/month VPS handles dozens of low-traffic tunnels."

Cost is bounded **by design**: give each tenant a conservative `monthlyBytes`
(e.g. 20–50 GB/mo) in `tenants.json`. Over-quota tenants get HTTP 402 and their
tunnel auth is refused until the 1st-of-month reset, so no single user can run up
the bill.

## Costs that aren't money (and usually matter more)

- **Liability / privacy.** You become an intermediary for telescope control and,
  potentially, the observatory's location. That is a trust surface and brings data
  protection obligations (GDPR/LGPD). A hardware vendor absorbs this; a FOSS
  project operator is choosing to. TLS is end-to-end to the host, and the relay
  does not inspect payloads, but you still hold billing metadata and connection
  logs — say so explicitly in a privacy policy.
- **Operations / uptime.** On-call, DDoS, tenant abuse, token rotation. The
  built-in rate limiting, quotas, audit log, and optional mTLS reduce this a lot,
  but someone has to watch it.
- **Legal.** Terms of service + a privacy policy are effectively mandatory once you
  proxy other people's equipment.

## Recommended path (if someone does this)

1. **Phase 1 — one shared opt-in instance (Option B), cheap.** Run a single
   `NINA.Relay.Server` on a **bundled-bandwidth VPS (Hetzner/OVH)** or **behind
   Cloudflare Tunnel** to neutralize bandwidth + DDoS. Issue tenant tokens with a
   conservative `monthlyBytes`. Keep self-host as the default; this is an
   opt-in convenience tier. This already delivers the ZWO-style UX at ~$5–15/mo.
2. **Phase 2 — scale out if needed.** Shard tenants across VPSes, or move fully
   behind Cloudflare as the front.
3. **Phase 3 — WebRTC P2P (Option D), only if bandwidth becomes the real
   constraint.** Direct browser↔host connections remove 80%+ of cloud traffic,
   leaving the relay as signaling + a TURN fallback for symmetric-NAT users. This
   is the only phase that requires new engineering.

## Client configuration reminder (matches the code)

The Polaris host reads exactly these keys (env form `Relay__Key=value`):

```jsonc
{
  "Relay": {
    "Enabled": true,
    "ServerUrl": "wss://relay.example.com/_tunnel",
    "Token": "the-long-random-token-issued-by-the-operator",
    "ClientCertPath": "/etc/nina/relay-client.pfx",   // optional (mTLS)
    "ClientCertPassword": "optional-pfx-password"
  }
}
```

Note the tunnel route is **`/_tunnel`** (underscore) and the hostname is assigned
by the server from the token (there is no `TenantId` on the client). See
[`README.md`](README.md) for the full server + tenant setup.
