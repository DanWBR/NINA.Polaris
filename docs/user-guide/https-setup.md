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

## Permanent trust — install the cert as a trusted root

One-time per device. After install, the browser shows the normal
green padlock instead of "Not secure", every time, forever (until
the cert expires in ~5 years).

### Step 1 — download the cert (every OS)

On the device you want to add: open Polaris over HTTPS, click
through the warning once, go to **Settings → 🔒 HTTPS endpoints**,
and click **⬇ Download certificate**. The file `polaris-root.crt`
saves to your Downloads folder.

You can also download it directly: `https://polaris-app.local:5000/api/system/server-cert`
(replace the hostname with whichever name resolves on your LAN).
The endpoint returns only the **public certificate** (PEM-encoded);
the private key never leaves the server.

### Step 2 — install + trust it

#### Windows (Chrome / Edge)

1. Double-click `polaris-root.crt` in Downloads.
2. **Install Certificate…** → **Local Machine** → **Next**.
3. **Place all certificates in the following store** → **Browse…** →
   **Trusted Root Certification Authorities** → **OK** → **Next** → **Finish**.
4. Windows shows a security prompt with the cert fingerprint —
   compare it to the one in Polaris Settings, click **Yes**.
5. Restart Chrome / Edge. Reload `https://polaris-app.local:5000` —
   green padlock.

#### macOS (Safari / Chrome / Firefox)

1. Double-click `polaris-root.crt` — opens **Keychain Access**.
2. The cert appears under **login → Certificates**. Drag it to
   **System → Certificates**, authenticate with your password.
3. Double-click the cert in **System**, expand **Trust**, set
   **When using this certificate** → **Always Trust**, close the
   window, authenticate again.
4. Restart Safari / Chrome. Reload — green padlock.

#### iPhone / iPad (Safari)

1. Tap the **Download certificate** button in Safari. iOS shows
   *"This website is trying to download a configuration profile.
   Do you want to allow this?"* → **Allow**.
2. **Settings app** → at the very top, **Profile Downloaded** →
   tap it → **Install** (top-right) → enter passcode → **Install**
   again → **Done**.
3. **CRITICAL extra step** that catches everyone: **Settings →
   General → About → Certificate Trust Settings** → toggle ON the
   *polaris-root* entry. Without this iOS only trusts the cert
   for VPN/Mail purposes, not for Safari TLS validation.
4. Reload Polaris in Safari — green padlock.

#### Android (Chrome)

⚠ **Limitation**: Chrome on Android 7+ does **not** trust
user-installed certificates by default — only system-level certs
(which require root) work for browser TLS validation. So on stock
Android you can install the cert system-wide for **apps that opt
in** (the WiFi captive portal handler, Polaris's PWA via
WebView…), but `chrome://` itself will keep showing "Not secure".

What works:

- **Add Polaris to home screen as PWA** (Chrome → ⋮ menu → *Install
  app*) — opens in a chromeless window where the warning is hidden.
- **Use Firefox** — it has its own CA store and respects user-
  installed certs.
- **Use the Polaris Relay** for proper Let's Encrypt cert
  (see *Why not Let's Encrypt?* below).

To install the cert as system-wide trust anyway (works for some
WebViews):
**Settings → Security → Encryption & credentials → Install a
certificate → CA certificate** → confirm the warning → pick
`polaris-root.crt` from Downloads.

#### Linux (Chrome)

```bash
# Import via NSS DB (Chrome uses this on Linux):
sudo apt install libnss3-tools   # or your distro's equivalent
certutil -d sql:$HOME/.pki/nssdb -A -t "C,," -n "polaris-root" -i polaris-root.crt
```

Restart Chrome. Reload — green padlock.

For Firefox: **Settings → Privacy & Security → Certificates →
View Certificates → Authorities → Import** → pick
`polaris-root.crt` → tick *"Trust this CA to identify websites"*.

### Verification

After install, the cert SHA-1 fingerprint your browser shows in
"Cert details" should match the one Polaris Settings prints. If
they match, you installed the right file. If they don't, you
installed someone else's cert — re-download from the trusted
server and try again.

The install is per-device, **not** per-browser-profile. Once
installed at the OS / system trust store level, every browser on
that device that respects the system store trusts Polaris.

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
