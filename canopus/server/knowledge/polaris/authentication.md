# Authentication

Polaris ships with a password gate that protects the local HTTP API
+ WebSockets + embedded sub-apps (PHD2 GUI, INDI Web Manager,
Stellarium). On a fresh install you set a password the first time
you open the UI; on every subsequent device you sign in with that
password once and the browser remembers the session.

## First-run wizard

When you open `https://polaris-pi.local:5000` for the first time
(after the cert warning) you see a full-screen overlay:

> **Set a password**  
> Polaris is reachable from any device on this network. Pick a
> password that protects this rig from unauthorized access.

Type a password (minimum 8 characters), confirm it, click
**Set password & continue**. The app loads.

There is no default password. The wizard cannot be skipped. This is
intentional: hardcoded defaults like `polaris1234` get forgotten and
never changed.

## Day-to-day login

On every other device (laptop, phone, second tablet) you'll see a
**Sign in** overlay instead of the wizard. Type the password, tick
**Remember on this device** if you want to stay logged in across
browser restarts, click **Sign in**.

- Unchecked: token lives in `sessionStorage` and clears when you
  close the tab.
- Checked: token lives in `localStorage` and survives until you
  sign out, change the password, or restart the Polaris server.

Sessions expire after 24h of inactivity by default (the timer
resets on every request). Use the gate without thinking about it.

## Change password

Settings → **Authentication** → **🔑 Change password**.

Modal asks for the current password, the new one, and a confirm.
On success every OTHER session is invalidated immediately, so any
device that was logged in with the old password gets bounced back
to the **Sign in** overlay next request. Your current session
keeps its token.

## Sign out

Settings → **Authentication** → **🚪 Sign out**.

Drops your session token + tells the server to forget it. You go
back to the **Sign in** overlay.

## Loopback bypass

Connections from `127.0.0.1` / `::1` (loopback) skip auth
entirely. Use cases:

- SSH-tunneling Polaris to your laptop: `ssh -L 5080:localhost:5080 polaris@pi` then `curl http://localhost:5080/api/system/status` — no token needed.
- Running scripts ON the Pi against `http://localhost:5080/`: same.
- Local dev: `dotnet run` + browser on the same machine: no friction.

This matches the convention Jupyter / Grafana / RStudio use. The
reasoning: anyone who can already open a shell on the Pi has
control over Polaris through other channels (kill the process,
edit the config, restart with new credentials). Auth at the HTTP
layer adds no extra protection there.

## Disable the gate (closed LAN / field setup)

For a setup where the LAN is genuinely trusted (offline observatory,
isolated star-party network, dev rig in your house with no guests)
you can turn auth off entirely.

Settings → **Authentication** → **Disable auth…**, type your
current password to confirm, click **Disable**. Now anyone on the
network reaches Polaris without a password.

Re-enable: Settings → **Authentication** → **Re-enable auth**,
type the password when prompted. The same password you set in the
wizard still works.

## Forgot the password

Recovery is by SSH (or any way to edit files on the Pi). There is
no email-based reset, no security questions, nothing automatic.

```bash
ssh polaris@polaris-pi.local
nano ~/.config/NINA.Polaris/profiles/active.json
```

Find the lines:

```json
  "AuthPasswordHash": "...some-base64...",
  "AuthPasswordSalt": "...some-base64...",
```

Set both to empty strings:

```json
  "AuthPasswordHash": "",
  "AuthPasswordSalt": "",
```

Save + restart the service:

```bash
sudo systemctl restart polaris.service
```

Next browser hit triggers the first-run wizard again — set a new
password there. All previous sessions are invalidated by the
restart.

## How it works under the hood

- **Hash**: PBKDF2-SHA256, 100,000 iterations, 16-byte random salt
  per password. Stored as base64 in `AuthPasswordHash` +
  `AuthPasswordSalt` on the active profile.
- **Session token**: 32 bytes of `RandomNumberGenerator.Fill`
  output, base64-url encoded (43 chars, 256-bit entropy). Lives in
  an in-memory dictionary on the server; restart invalidates all
  sessions. Sliding 24h expiration, 10-min sweeper.
- **Token transport**: 3 fallbacks, in order:
  1. `Authorization: Bearer <token>` HTTP header (added by JS).
  2. `?token=...` query string (file download URLs that open in
     `<img>` or `<a>` with no header support).
  3. `polaris_session` cookie, `HttpOnly`, `SameSite=Strict`,
     `Path=/`, no `Max-Age` (dies with the browser). Auto-attached
     by the browser on same-origin XHR / fetch / WebSocket
     upgrades / iframe loads.
- **Gated paths**: `/api/*` (except `/api/auth/*` and
  `/api/system/version`), `/ws/*`, `/phd2-gui/*`, `/indi-web/*`,
  `/sky/*`. Everything else (login page, CSS, JS, images, fonts,
  `/data/*`) is open so the login UI itself can load.
- **Rate limit**: 5 failed login attempts per IP per minute, then
  exponential backoff capped at 1h. A successful login clears the
  bucket. Per-IP so one bad actor on the WiFi doesn't lock out the
  whole network.
- **Constant-time compare**: `CryptographicOperations.FixedTimeEquals`
  on every password check. Defeats timing attacks.

## What auth does NOT protect against

- An attacker who can already SSH into the Pi as `polaris` or
  `root`. They can edit `active.json` or read it (the hash is
  PBKDF2 + salt; brute-forcing offline still takes serious time
  but the salt + hash is right there).
- Network sniffing of the cleartext password during login over
  plain HTTP. Polaris forces HTTPS on the LAN-facing port (5000),
  so this only matters if you opted into HTTP via reverse proxy.
- The Relay tunnel feature (`NINA.Relay.Server`). Relay has its
  own per-tenant token + rate-limit + mTLS layer that runs
  separately from this; the two are designed to compose.

## See also

- [Raspberry Pi setup](raspberry-pi-setup.md) — first-boot defaults
  including WiFi hotspot credentials.
- [WiFi management](#) — switching the Pi between Hotspot and
  Station modes.
