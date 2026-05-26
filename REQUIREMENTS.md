# N.I.N.A. Polaris, Requirements

Complete inventory of every piece of software N.I.N.A. Polaris depends on or
talks to, split by **required** (the app won't boot or won't have the
named functionality without it) and **optional** (lights up an extra feature
when present, gracefully degrades otherwise). Two columns: **Windows** (mini-PC,
desktop, laptop) and **Linux ARM/x64** (Raspberry Pi 4/5, Intel SBCs, generic
Linux servers).

Anything in the **Required** sections is also linked from the [Installation
guide](docs/user-guide/installation.md) with copy-paste setup commands.

---

## TL;DR, bare-minimum to boot

| | Windows | Linux (RPi / x64) |
| --- | --- | --- |
| Runtime | .NET 10 Desktop Runtime | .NET 10 Runtime (linux-arm64 or linux-x64) |
| Polaris binary | `NINA.Polaris.exe` (self-contained publish) | `NINA.Polaris` (self-contained publish) |
| Network | TCP 5000 open on the LAN side | TCP 5000 open on the LAN side |

That's it for the home page. Every other feature has its own dependency
which is described below, Polaris detects what's installed at runtime and
hides UI for what isn't available.

---

## Required for development (building from source)

These are needed by anyone running `dotnet build` / `dotnet test` against the
repository. Not needed by end users on a Pi / mini-PC running a published
binary.

| Tool | Windows | Linux | Notes |
| --- | --- | --- | --- |
| **.NET 10 SDK** | [installer](https://dotnet.microsoft.com/download/dotnet/10.0) | `sudo apt install dotnet-sdk-10.0` (or [Microsoft repo](https://learn.microsoft.com/dotnet/core/install/linux)) | Required. Build + test target `net10.0`. |
| **Git** | [installer](https://git-scm.com/) | `sudo apt install git` | Required. Clone + git submodules (stellarium-web-engine). |
| **Git LFS** | comes with Git for Windows | `sudo apt install git-lfs && git lfs install` | Optional, only if you'll touch the docs PDF / large binary assets. |
| **Bash / sh** | Git Bash (bundled) or WSL | native | Required for some helper scripts (`deploy/*.sh`). On pure Windows you can run the PowerShell equivalents under `deploy/*.ps1`. |
| **Docker** | [Docker Desktop](https://www.docker.com/products/docker-desktop) | `sudo apt install docker.io docker-compose-plugin` | Optional, needed by `scripts/build-stellarium-web.sh` (Emscripten via container) and `deploy/docker-build.sh` (multi-arch Polaris image). |
| **Emscripten** (only if rebuilding stellarium-web) | via Docker | via Docker | Optional. Submodule's pinned `.js/.wasm` is committed, so you don't need Emscripten unless you bump the engine. |

---

## Runtime, required

### .NET 10 Runtime

- **Windows**: [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0). Self-contained publishes (`deploy/publish-win-x64.ps1`) bundle the runtime, so end users can skip this.
- **Linux (RPi 4/5, Intel SBC, server)**: `sudo apt install dotnet-runtime-10.0`, OR use the self-contained publish (`deploy/publish-linux-arm64.sh` for the Pi, `deploy/publish-linux-x64.sh` for Intel) which embeds the runtime.

### Network

- **TCP 5000** inbound on the LAN interface (Polaris HTTP listener).
- **UDP 5353** inbound for mDNS / Bonjour so `polaris-app.local` resolves. Both ports are blocked by Windows Defender Firewall and `ufw` by default.

#### Windows firewall, allow Polaris

Run in **Admin PowerShell**:

```powershell
New-NetFirewallRule -DisplayName "N.I.N.A. Polaris" `
    -Direction Inbound -Protocol TCP -LocalPort 5000 `
    -Action Allow -Profile Private,Domain
# mDNS (Bonjour) discovery
New-NetFirewallRule -DisplayName "mDNS (Polaris)" `
    -Direction Inbound -Protocol UDP -LocalPort 5353 `
    -Action Allow -Profile Private,Domain
```

#### Linux firewall, allow Polaris

```bash
sudo ufw allow 5000/tcp
sudo ufw allow 5353/udp
```

Or, if `ufw` isn't installed, plain `iptables -A INPUT -p tcp --dport 5000 -j ACCEPT`.

### mDNS resolver (so `polaris-app.local` works on the client)

| Platform | What you need |
| --- | --- |
| Windows 10 1803+ | Built-in. Nothing to install. |
| Windows ≤10 1709 / Windows 7 | [Bonjour Print Services](https://support.apple.com/kb/dl999) (~5 MB, Apple-signed) |
| macOS / iOS | Built-in (Bonjour) |
| Linux desktop | `sudo apt install avahi-daemon libnss-mdns` then enable. Most distros pre-install it. |
| Android | Built-in since Android 12. Older versions: works on most LANs anyway because Chrome runs its own resolver. |

---

## Equipment drivers, required per device family

Polaris talks to telescopes / cameras / focusers / filter wheels / etc. through
two driver buses. **At least one** is required to control hardware; both can
coexist.

### Option A: INDI (preferred on Linux + supported on Windows)

| Platform | Install |
| --- | --- |
| **Linux (Pi / desktop)** | `sudo apt install indi-full`, meta-package with the daemon + 100+ drivers (ZWO, Canon, EQMod, Celestron, FlatPanel, weather stations, ...) |
| **Windows** | INDI runs natively but most users prefer the Alpaca path on Windows. If you really want INDI: [windi](https://github.com/indilib/indi/wiki/Windows) build, OR run [indiserver inside WSL2](https://www.indilib.org/get-indi/download.html) and expose the port. |

### Option B: ASCOM / Alpaca

| Platform | Install |
| --- | --- |
| **Windows** | [ASCOM Platform](https://ascom-standards.org/Downloads/Index.htm) (free) + the driver pack for each device. Alpaca is enabled by default in ASCOM Platform 7+. |
| **Linux** | Run an [Alpaca-compatible](https://github.com/ASCOMInitiative/Alpyca) device server. Most modern ASCOM drivers expose Alpaca natively. Polaris discovers them over the LAN via UDP. |

Polaris's RIGS tab lists devices from both buses; the active rig stores per-
device driver choice (`indi` / `alpaca` / vendor SDK).

### Option C: Vendor SDKs (DSLR / Mirrorless on Windows only)

- **Canon EDSDK**, register at [developer.canon-asia.com](https://developer.canon-asia.com/), download EDSDK 13.x+, drop DLLs in `plugins/canon-edsdk/`. Detected at startup; appears in RIGS → Camera → Driver dropdown.
- **Nikon SDK**, [developer.nikonimaging.com](https://developer.nikonimaging.com/), drop in `plugins/nikon-sdk/`.
- **Sony Camera Remote SDK**, [developer.sony.com/imaging-products](https://developer.sony.com/imaging-products/), drop in `plugins/sony-sdk/`.

DSLRs / mirrorless on **Linux** use `indi_gphoto_ccd` from `indi-full` (see Option A). No vendor SDKs needed on Linux.

See `docs/dslr-windows-canon.md`, `docs/dslr-windows-nikon.md`, `docs/dslr-windows-sony.md`, `docs/dslr-linux.md` for end-to-end instructions per vendor.

---

## Plate solving, required for "Slew & Center", optional for "Slew Only"

Polaris ships a multi-solver dispatcher with automatic fallback. **At least
one** of the four solvers below must be installed for the Slew & Center
button to do its centering loop. Without any solver, Slew Only still slews
the mount; the centering iteration just doesn't run.

| Solver | Windows | Linux | When to pick it |
| --- | --- | --- | --- |
| **ASTAP** | [astap.exe](https://www.hnsky.org/astap.htm) installer + the H17/H18 star database (~2 GB) | `sudo apt install astap` + `astap-data` | Default. Fast (~1-3 s), works offline, MIT. Recommended for everyone. |
| **PlateSolve3** | [PlateSolve3](https://planewave.com/software-and-downloads/) installer | n/a | Faster than ASTAP at long focal lengths. Windows only. Free for non-commercial. |
| **Astrometry.net (local)** | not practical | `sudo apt install astrometry.net` + index files | The gold standard but slow on Pi (~30-60 s). |
| **Astrometry.net (online)** | API key only | API key only | Last resort. Needs internet, ~30-60 s per solve, free with API key from [nova.astrometry.net](https://nova.astrometry.net). |

Polaris detects all installed solvers at startup and dispatches `primary →
blind fallback`. Configure paths / API keys in `appsettings.json` under
`PlateSolve:*`.

---

## Optional, autoguiding

### PHD2 (full integration with embedded GUI)

- **Windows**: download [PHD2](https://openphdguiding.org/) installer (free, GPLv3). Polaris auto-detects, can launch / shutdown it, swap profiles, run Smart Calibrate, and broadcast guide stats in real time.
- **Linux (Pi)**: `sudo apt install phd2`.
- **Linux embedded GUI** (Polaris's GUIDE tab → "PHD2 GUI" panel): requires `xpra` 6.0+, `sudo apt install xpra xserver-xorg-video-dummy`. **Not supported on 32-bit Raspberry Pi (Pi 2/3 with 32-bit OS)**; gated automatically. See [docs/phd2-gui-embedding.md](docs/phd2-gui-embedding.md) for the Xorg-dummy tweak.

PHD2 connection (TCP 4400) is auto-attempted on Polaris startup along with INDI / Alpaca discovery.

---

## Optional, post-processing tools

| Tool | Windows | Linux | Why |
| --- | --- | --- | --- |
| **Siril** | [Siril](https://siril.org/) installer | `sudo apt install siril` | Replaces the C# stacking pipeline in STUDIO with the user's existing Siril scripts (5 bundled, plus your `~/.siril/scripts`). |
| **GraXpert** | [GraXpert](https://www.graxpert.com/) v3.0+ installer | [AppImage release](https://github.com/Steffenhir/GraXpert/releases) | Background extraction (auto per-frame during sequences if toggled), deconvolution, denoise. v3.0+ unlocks decon + denoise; v2.x has BGE only. |

Polaris detects both via the `Services/External/BinaryLocator` at startup and grays out the corresponding STUDIO buttons when missing. See [docs/siril-setup.md](docs/siril-setup.md) and [docs/graxpert-setup.md](docs/graxpert-setup.md).

---

## Optional, simulators (testing without hardware)

| Platform | Install |
| --- | --- |
| **Linux / macOS** | `sudo apt install indi-bin` (renders real stars from the GSC catalog at the simulated mount position, perfect for testing the whole pipeline). |
| **Windows** | [Alpaca Omni Simulator](https://github.com/ASCOMInitiative/ASCOMRemote/releases), single .exe that exposes the ASCOM Camera/Telescope/Focuser/FilterWheel simulators over Alpaca. |

Settings → "Equipment simulator" detects the install + lets you spawn the stack with a single click. See [docs/user-guide/simulator-mode.md](docs/user-guide/simulator-mode.md).

---

## Optional, Remote terminal in Settings

Browser-based SSH (xterm.js + SSH.NET) lets you restart services from the
Polaris tab when the host is headless.

| Platform | Install |
| --- | --- |
| **Polaris config** (both) | Set `Terminal:Enabled = true` in `appsettings.json` (default `false`). Polaris returns 403 on `/ws/terminal` otherwise. |
| **Windows host** (SSH-to-self) | Install OpenSSH Server: `Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0` then `Start-Service sshd; Set-Service sshd -StartupType Automatic` (Admin). |
| **Linux host** | `sudo apt install openssh-server && sudo systemctl enable --now ssh`. Already installed on Raspberry Pi OS by default. |

Credentials are entered per-connection and never persisted. See [docs/user-guide/remote-terminal.md](docs/user-guide/remote-terminal.md).

---

## Optional, Stellarium remote control sync

- Stellarium desktop ([free, GPLv2](https://stellarium.org/)), any platform. Enable Plugins → "Remote Control" → bind to `127.0.0.1:8090`.
- Polaris's SKY tab can pull the currently-selected object from Stellarium as a target.

---

## Optional, Docker deployment

| | Tool |
| --- | --- |
| **Both** | Docker 20.10+ + Compose v2. `docker compose up -d --build` for a single-host run; `deploy/docker-build.sh latest` for multi-arch images (amd64 + arm64) for push. |
| | The Dockerfile is multi-stage (SDK → runtime). Volumes mount `/config` (profiles) and `/images` (FITS output). |

The container hits the same TCP 5000 / UDP 5353 ports, port-forward both in compose or with `--network host` on Linux.

---

## Optional, Relay server (remote internet access)

For accessing your Pi-based observatory rig from anywhere over the
internet without exposing it directly.

- Public VPS (any provider) with a domain name pointed at it
- Linux x64 or arm64 build of `NINA.Relay.Server` (in this repo)
- Port 80/443 open (Let's Encrypt via LettuceEncrypt is built in)

The relay reverse-tunnels Polaris over HTTPS with per-tenant tokens, mTLS,
rate limiting, monthly byte quotas, and a web admin UI. See
[src/NINA.Relay.Server/README.md](src/NINA.Relay.Server/README.md) and
[docs/user-guide/relay.md](docs/user-guide/relay.md).

Polaris (the rig side) needs nothing extra, just point it at the relay
endpoint in Settings.

---

## Optional, DSLR / Mirrorless live preview (Windows)

Already covered under "Equipment drivers → Option C". Listed here as a
reminder that DSLR support on Windows needs vendor DLLs that the user
downloads after registering with the vendor (EULA prevents redistribution).

---

## Optional, Astrometric calculations

- **Pre-installed** with Polaris via NuGet (`CosineKitty.AstronomyEngine`, MIT, ~150 KB). No external install. Used for Tonight's Best ephemerides + altitude charts.

---

## Optional, Internet-dependent features

These features need an internet connection (one-time or recurring). All
others work fully offline.

| Feature | What it fetches | Where from |
| --- | --- | --- |
| Weather forecast | Astronomical forecast (clouds, seeing, transparency) | [7Timer.info](https://www.7timer.info) (free, no key) |
| Tonight's Best thumbnails | DSO / planet / Moon photos | NASA Image Library + Wikipedia REST API (free, no key) |
| Reverse-geocoding from lat/lng | "City, Country" label below the home clock | OpenStreetMap [Nominatim](https://nominatim.org) (free, throttled) |
| SKY tab DSS imagery | High-res deep-sky tiles when zoomed in | CDS Strasbourg [Aladin](https://aladin.cds.unistra.fr) HiPS server (free, public) |
| Astrometry.net online solver | Plate-solve fallback | nova.astrometry.net (free, API key) |
| Relay public access | Anywhere-on-internet access to a LAN Polaris | Your VPS (you host) |

All have graceful offline fallbacks. The home page works at the airlock of a Mars colony.

---

## Hardware sizing

| Host | Works? | Notes |
| --- | --- | --- |
| **Raspberry Pi 5 (8 GB)** | ✅ Recommended | Lossless. Live stacking + simultaneous Studio jobs OK. |
| **Raspberry Pi 4 (4 GB / 8 GB)** | ✅ Workhorse | Default target. PHD2 GUI embed works; client-side WASM live-stack offloads the heavy math. |
| **Raspberry Pi 3 (1 GB)** | ⚠ Limited | App runs but PHD2 GUI embed is auto-disabled (32-bit ARM gate). Stacking on the Pi side gets tight; use client-side WASM offload. |
| **Raspberry Pi 2 (512 MB)** | ⚠ Bare minimum | Sequence + live stacking work; STUDIO batch jobs not realistic on-host. |
| **Intel mini-PC (N100, J4125, etc.)** | ✅ | Headless Linux x64 publish. |
| **Windows mini-PC / desktop / laptop** | ✅ | win-x64 publish or `dotnet run`. Same feature set as Linux + vendor DSLR drivers. |
| **macOS** | ⚠ Untested | Should run via `dotnet run` on Apple Silicon; no publish target shipped. |

---

## Browser requirements (the UI client)

| | Minimum | Recommended |
| --- | --- | --- |
| **Chromium-based** (Chrome, Edge, Brave, Vivaldi) | 100+ | Latest |
| **Firefox** | 100+ | Latest |
| **Safari** | 16+ | Latest |
| **Mobile** (Chrome Android, Safari iOS) | recent two majors | Latest |

WebGL2 is required for the WebGL stretch pipeline and the SKY engine. Without it, those features fall back to server-side JPEG rendering (slower) and to a flat-background sky (no DSS imagery).

WebAssembly with SIMD (Chromium 91+, Firefox 90+, Safari 16.4+) unlocks client-side live-stack offload via the `NINA.Polaris.Wasm` module.

---

## What Polaris DOESN'T need

- **No** database server (SQLite file under AppData).
- **No** message broker / cache (it's a single .NET process).
- **No** Node.js / npm (frontend is plain Alpine.js + vendored libs, no build step).
- **No** Python runtime.
- **No** Java.
- **No** cloud account for the basic install. Everything except the optional integrations listed above runs on your LAN.

---

If something here is wrong, outdated, or you want to add a tooling profile
(e.g. a new observing planetary imaging vendor), edit this file and PR. The
goal is for anyone landing here to install the smallest possible set of
dependencies for the features they actually want.
