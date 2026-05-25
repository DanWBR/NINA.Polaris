# HTTPS + WebGPU on the LAN

Polaris generates a **self-signed TLS certificate** on first boot and
listens on **port 5001 (HTTPS)** in addition to the default
**port 5000 (HTTP)**. The HTTPS endpoint exists for one specific
reason: Chrome (and every other modern browser) gates a handful of
powerful Web APIs behind a "secure context" check. The ones we care
about for Polaris:

- **WebGPU** — required for the in-browser GraXpert AI pipelines
  (BGE / Denoise / Deconvolution) to use the client device's GPU. On
  plain HTTP via a hostname like `polaris.local`, `navigator.gpu` is
  not even defined, so the pipelines fall back to single-threaded
  WASM. A heavy Denoise run can go from ~10 seconds (WebGPU on a
  modern dGPU) to ~30 minutes (WASM single-thread).
- **SharedArrayBuffer** — required for multi-threaded WASM. When
  available, the ORT WASM backend uses every CPU core instead of one,
  for a 4–8× speedup as the GPU-less fallback.

`localhost` is a "secure context" without any cert dance — but only
when the browser literally types `localhost`. Reaching the same server
via `http://polaris-app.local`, `http://192.168.1.42`, or any other
hostname does **not** count. That's where the self-signed HTTPS
endpoint comes in.

## What Polaris does for you

On first boot:

1. Enumerates every DNS name + IPv4 / IPv6 address you might use to
   reach this host — `localhost`, the machine's hostname,
   `hostname.local`, `polaris.local`, `polaris-app.local`, every
   non-loopback / non-link-local IP from every active NIC.
2. Generates an RSA-2048 self-signed certificate with all of those
   names baked into its Subject Alternative Name (SAN) extension.
3. Persists the cert as a PFX at
   `{LocalApplicationData}/NINA.Polaris/cert/polaris.pfx`
   (Windows: `%LOCALAPPDATA%\NINA.Polaris\cert\polaris.pfx`,
   Linux: `~/.local/share/NINA.Polaris/cert/polaris.pfx`).
4. Configures Kestrel to serve HTTPS on port 5001 using that cert.

The cert is reused on subsequent boots unless:

- the file is missing,
- it's within 30 days of expiry (default validity is 5 years), or
- the set of hostnames / IPs has changed substantially (you moved the
  box to a new LAN, the hostname changed, etc.) — keyed by a SHA-256
  hash of the SAN list stored next to the PFX.

## Per-device setup

For each client device (laptop, tablet, phone) that needs WebGPU:

1. Open Polaris via the **HTTPS URL** instead of HTTP. The Settings →
   "🔒 HTTPS endpoints" section lists ready-to-click URLs covering
   every name in the cert.
2. The browser will show a warning — *"Your connection is not
   private"* / *"NET::ERR_CERT_AUTHORITY_INVALID"*. That's expected:
   it's a self-signed cert that no public CA vouches for.
3. Click **Advanced → Proceed to ... (unsafe)** (Chrome) or
   **Advanced → Accept the Risk and Continue** (Firefox).
4. After that, Chrome remembers the exception per origin per device.
   Refreshes work normally.

To check it worked: in DevTools console, run
`navigator.gpu.requestAdapter().then(a => console.log(a))`. A non-null
adapter means WebGPU is ready.

### Verifying the cert fingerprint

If you're paranoid (good!), compare the SHA-1 fingerprint Chrome shows
in its cert-details dialog against the one Settings prints. They must
match — that's how you know the cert your browser sees is the cert
Polaris generated, not a man-in-the-middle.

## Permanent trust (optional)

If you don't want to click through the warning every time you launch
a fresh Chrome profile, install the cert as trusted at the OS level:

- **Windows**: copy `polaris.pfx` to the client, double-click, install
  to *Local Machine → Trusted Root Certification Authorities*. Restart
  Chrome.
- **macOS**: copy + open in Keychain Access, drag to *System →
  Certificates*, set *Always Trust*.
- **Linux** (Chrome): import via Chrome's *Settings → Privacy and
  security → Security → Manage certificates → Authorities → Import*.

This is per-device. Polaris does **not** auto-distribute the cert —
treat it as a privileged operation.

## Disabling HTTPS

If you only ever reach Polaris from the same machine (`localhost`),
HTTPS doesn't add anything. Set in your `appsettings.json` (or
environment override):

```jsonc
"Server": {
  "Https": { "Enabled": false }
}
```

HTTPS port can also be moved off 5001 via `Server:Https:Port` if you
need 5001 for something else.

## Why not Let's Encrypt?

For LAN-only servers there's no path to a publicly-trusted cert —
Let's Encrypt requires a domain name resolvable on the public
internet plus the ability to prove control via HTTP or DNS challenge.
A Pi sitting in an observatory at `polaris.local` doesn't have that.

Polaris's **Relay** feature (a separate stack — see
[`docs/relay.md`](relay.md)) DOES use LettuceEncrypt for publicly
reachable HTTPS on the relay endpoint. That solves the
remote-access-from-the-internet case with a properly trusted cert.
For LAN access, the self-signed approach is the only practical
option.
