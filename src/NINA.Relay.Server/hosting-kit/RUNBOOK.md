# Hosted relay: operator runbook

Step-by-step for standing up **one shared, multi-tenant relay** for Polaris
users (the "Phase 1" of [HOSTING.md](../HOSTING.md)). Everything here uses
the server as it ships; there is no code to write.

Companion files in this folder:

| File | What it is |
|---|---|
| `polaris-relay.service` | systemd unit for the relay binary |
| `Caddyfile.sample` | Caddy front with a DNS-01 wildcard cert |
| `appsettings.hosted.sample.json` | production server config |
| `tenants.hosted.sample.json` | tenant tiers with conservative quotas |
| `token-issuance.md` | how to create, deliver, and revoke tokens |
| `TERMS-OF-SERVICE.draft.md` | ToS draft to adapt before launch |
| `PRIVACY-POLICY.draft.md` | privacy policy draft to adapt before launch |

## Routing mode: subdomain, not path-prefix

The relay supports two routing modes. For the full Polaris web app **use
subdomain routing** (`alice.relay.example.com`). Path-prefix routing
(`relay.example.com/t/alice/`) forwards the request it matches but does not
rewrite the returned HTML, and the Polaris SPA references its assets by
absolute path (`/js/app.js`, `/css/...`): under a path prefix those requests
arrive at the relay root with no tenant and fail. Path-prefix remains fine
for plain API calls; the browser UI needs subdomains.

Subdomain routing implies a **wildcard certificate** for
`*.relay.<your-domain>`, and Let's Encrypt only issues wildcards through a
**DNS-01** challenge. The built-in LettuceEncrypt mode is HTTP-01 only, so a
hosted instance puts **Caddy in front** to do DNS-01 (option A below) or
rides Cloudflare's edge certificates (option B).

## Option A (recommended): Hetzner VPS + Caddy DNS-01 wildcard

Uses the domain you already own. DNS for the zone must live at a provider
with an API that Caddy has a DNS plugin for (Cloudflare's free DNS-only plan
works and does not proxy any traffic; the registrar does not change).

1. **VPS.** Hetzner CX22 (~4.5 EUR/month, 20 TB traffic included) or an
   OVH/DigitalOcean equivalent. Ubuntu 24.04. Do not use AWS/GCP/Azure:
   egress pricing makes a relay uneconomical (see HOSTING.md).
2. **DNS.** Create `relay.<domain>` A record to the VPS IP and
   `*.relay.<domain>` A record to the same IP. If the zone is not yet on an
   API-capable DNS host, move the nameservers (free on Cloudflare DNS-only;
   the registrar keeps the domain).
3. **Build.** On any machine with the .NET 10 SDK:

   ```bash
   dotnet publish src/NINA.Relay.Server -c Release -r linux-x64 \
       --self-contained -o publish/relay
   rsync -a publish/relay/ root@VPS:/opt/polaris-relay/
   ```

4. **Configure.** Copy `appsettings.hosted.sample.json` to
   `/opt/polaris-relay/appsettings.json` and set the hostname suffix, the
   admin password, and keep `Tls:Mode=off` (Caddy terminates TLS). Copy
   `tenants.hosted.sample.json` to `/opt/polaris-relay/tenants.json`.
5. **Caddy.** Install a Caddy build that includes your DNS plugin (for
   Cloudflare: `caddy add-package github.com/caddy-dns/cloudflare`), then
   install `Caddyfile.sample` as `/etc/caddy/Caddyfile` with the domain and
   the DNS API token filled in. Caddy fetches and renews the wildcard cert.
6. **Service.** Install `polaris-relay.service` to
   `/etc/systemd/system/`, create the service user, enable and start:

   ```bash
   useradd --system --home /opt/polaris-relay --shell /usr/sbin/nologin relay
   chown -R relay:relay /opt/polaris-relay
   systemctl enable --now polaris-relay caddy
   ```

7. **Smoke test.** `curl https://relay.<domain>/_health` returns OK; open
   `https://relay.<domain>/admin/`, log in with the admin password, create a
   test tenant, point a Polaris host at
   `wss://relay.<domain>/_tunnel` with that token (the SETTINGS card), and
   load `https://<tenant>.relay.<domain>/`.

## Option B: Cloudflare-proxied dedicated domain

Buy a cheap dedicated domain for the relay (for example
`example-relay.com`, ~10 USD/year) and onboard it to Cloudflare's free
plan with the proxy enabled. Tenants live at `alice.example-relay.com`,
which is a **first-level** wildcard: Cloudflare's free universal certificate
covers it (it does not cover second-level wildcards like
`*.relay.example.com`, which is why this option uses a dedicated domain).

Same VPS + systemd steps as option A, but TLS is Cloudflare's problem: the
edge terminates HTTPS, the origin runs a free long-lived Cloudflare Origin
CA cert (`Tls:Mode=pfx`) or plain HTTP over a firewalled origin pull.
Egress through the proxy is not billed, and the VPS IP is hidden behind
Cloudflare's DDoS front. WebSockets are supported on the free plan; the
tunnel's 30 s ping keeps connections alive.

Trade-off: traffic decrypts at Cloudflare's edge in addition to the relay
itself, and heavy video streaming through the proxy can brush against
Cloudflare's terms. For a small user base option A is simpler to reason
about; option B scales further for free.

## Quotas and tiers

`tenants.hosted.sample.json` ships three tiers; adjust to taste:

| Tier | monthlyBytes | requestsPerSecond | bytesPerSecond | Meant for |
|---|---|---|---|---|
| standard | 30 GB | 10 | 2 MB/s | a normal remote-monitoring user |
| trial | 5 GB, `expiresAt` +30 days | 5 | 1 MB/s | evaluation tokens |
| heavy | 100 GB | 10 | 5 MB/s | by-request, known users |

The quota machinery is what makes a shared instance financially safe:
over-quota tenants get HTTP 402 and their tunnel auth is refused until the
1st-of-month UTC reset, so no tenant can run up the operator's bill.

## Routine operations

- **Issue / revoke tokens:** see `token-issuance.md`. Everything is done in
  the `/admin/` UI; `tenants.json` hot-reloads.
- **Backup:** `tenants.json` + `tenant-state.json` (usage counters) +
  `appsettings.json`; a nightly `tar` to object storage is plenty. The
  audit log rotates itself at 50 MB.
- **Update:** `dotnet publish` again, rsync over `/opt/polaris-relay/`,
  `systemctl restart polaris-relay`. Tunnels auto-reconnect with backoff;
  a restart costs users a few seconds.
- **Monitoring:** `GET /_health` for liveness (wire it to any uptime
  pinger), `/_tunnels` for connected rigs, the admin UI usage bars for
  quota pressure. Watch the VPS's bandwidth graph the first month to
  validate the ~5 GB/user/month assumption from HOSTING.md.
- **Abuse:** the audit log records every request with tenant, IP, path,
  size, and outcome. Disable a tenant (`enabled: false`) to cut them off
  immediately; rate limits contain the damage in the meantime.

## Before announcing it

1. Adapt and publish the ToS and privacy policy drafts (they are drafts,
   not legal advice; have them reviewed if the user base grows).
2. Decide the request channel for tokens (Discord post pinned with a form,
   or DM) and the tiers you will actually offer.
3. Set a calendar reminder for the 1st of the month to glance at usage
   after the automatic quota reset.
4. Keep self-hosting documented as a first-class path: the shared instance
   is an opt-in convenience, not the default (project posture is LAN-first).
