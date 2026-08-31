# Token issuance and lifecycle

The flow the operator runs when a user asks for access. Everything happens
in the `/admin/` UI; no server access needed after setup.

## Issue

1. User asks through the agreed channel (a pinned Discord post or DM)
   with the hostname slug they'd like (`alice` becomes
   `alice.relay.<domain>`). Slugs are lowercase, no dots.
2. In `/admin/`: **+ New tenant**, pick the tier limits (see
   `tenants.hosted.sample.json`), use the one-click random-token
   generator, put the user's Discord handle and the issue date in `note`.
   For trial tokens set `expiresAt` to issue date + 30 days.
3. Send the user, in a DM (never a public channel):
   - the tunnel URL: `wss://relay.<domain>/_tunnel`
   - the token
   - where to paste it: SETTINGS, the **Remote access relay** card,
     enable, paste both, **Save and connect**
   - their public address: `https://<slug>.relay.<domain>/`
   - the tier limits, and a reminder to set a Polaris password
     (the relay does not replace the app's own login).

## Revoke / pause

- Flip **enabled** off in the admin UI: tunnel auth is refused on the
  next connect and the running tunnel dies on the next reload.
- Trial tokens expire on their own via `expiresAt`.
- Deleting the tenant frees the hostname slug.

## Monthly rhythm

- Quota counters reset automatically on the 1st, 00:00 UTC.
- Glance at the admin usage bars after the reset; a tenant who filled
  the bar mid-month is a candidate for the heavy tier or a chat about
  leaving the live view running.
- The audit log (admin UI, filter by tenant) answers "what was all that
  traffic" without touching the server.

## Rules of thumb

- One token per rig. A user with two rigs gets two tenants.
- Never reuse a revoked token; generate a fresh one.
- The token is a bearer credential: whoever holds it can reach that
  rig's login page. Treat leaks as compromise, revoke and reissue, and
  tell the user to check their Polaris password.
