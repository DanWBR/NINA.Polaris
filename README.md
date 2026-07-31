# Polaris Astro Controller

**Cross-platform headless astronomy controller for Raspberry Pi, ARM64 SBCs, and Windows mini PCs.**

> ⚠️ **Polaris Astro Controller is a community-driven fork of [N.I.N.A.](https://nighttime-imaging.eu/)** It is **not** affiliated with or supported by the official N.I.N.A. development team. Please **don't** ask them for support with this fork, open issues here instead.

Polaris Astro Controller is a lightweight, browser-controlled astrophotography system built on ASP.NET Core. It brings the power of [N.I.N.A.](https://nighttime-imaging.eu/) (Nighttime Imaging 'N' Astronomy) to single-board computers and small-form-factor PCs, with a responsive Web UI accessible from any device on the network.

The Raspberry Pi (or Windows mini PC) acts as a data broker, controlling hardware, saving FITS files, and streaming images, while your laptop, tablet, or phone handles all the heavy rendering in the browser.

```
Browser (laptop / tablet / phone)        Raspberry Pi / Mini PC
┌──────────────────────────────┐         ┌──────────────────────────┐
│  Web UI (Alpine.js)          │◄──HTTP─►│  ASP.NET Core + Kestrel  │
│  Live preview (Canvas/WebGL) │◄──WS───►│  INDI client (TCP 7624)  │
│  Image processing (client)   │         │  Plate solving (ASTAP)   │
│  Sky explorer                │         │  Sequence engine         │
└──────────────────────────────┘         │  Live stacking           │
                                         └──────────────────────────┘
```


## Contents

- [Features](#features) · full list in **[docs/FEATURES.md](docs/FEATURES.md)**
- [Tested hardware](#tested-hardware)
- [Documentation](#documentation)
- [Architecture](#architecture) · [Technology Stack](#technology-stack)
- [Getting Started](#getting-started)
- [Deployment](#deployment)
- [API & Configuration](#api--configuration)
- [Performance Targets](#performance-targets)
- [Support the project](#support-the-project)
- [Contributing](#contributing) · [License](#license)

> **Looking for the full tooling matrix?** See [REQUIREMENTS.md](REQUIREMENTS.md)
> for the complete required + optional dependency list per platform
> (Windows / Linux ARM-RPi / Linux x64), with firewall rules and hardware
> sizing guidance.

## Features

A single self-hosted backend that drives the whole night from any browser:
acquisition, guiding, focusing, planning, live stacking, and a full
post-processing suite with in-browser AI. Highlights:

- **Equipment**: INDI (400+ Linux drivers), ASCOM/Alpaca over the network,
  direct ASCOM COM on Windows, and native ZWO / SVBony / Player One / ToupTek /
  Altair camera SDKs. DSLR/mirrorless (Canon, Nikon, Sony). Multi-rig profiles, an
  INDI property browser, and a one-click equipment simulator for hardware-free
  testing.
- **Acquisition**: real-time image streaming (LZ4 raw or JPEG, adaptive),
  plate-solve & center, PHD2 + a built-in native autoguider, manual focus
  assist + V-curve auto-focus, meridian-flip automation, dithering.
- **Planning & automation**: tree-based Advanced Sequencer, ASIAIR-style
  multi-target night planner (PLAN), mosaic planner, simple sequences, a
  plugin system, and a sky catalog / atlas / map with weather and
  Tonight's-Best.
- **EAA & video**: live stacking with calibration, SNR/HFR, and
  client-side (WASM) compute offload for weak hosts; planetary / lucky-imaging
  video capture and stacking.
- **Post-processing (STUDIO + EDITOR)**: calibrate, integrate, channel
  combine (RGB/LRGB/PixelMath), color calibration (BG-neutralize / manual /
  photometric PCC), crop, and a non-destructive editor. In-browser AI via
  ONNX/WebGPU: GraXpert 🌃 BGE / ☄ Denoise / 🌌 Deconvolution, plus 🌠 star
  removal (nox / starrem2k13 / StarNet++) and 🎚️ Image Blend. Optional NPU
  acceleration on RK35xx boards.
- **Access & ops**: responsive Web UI, in-app help/tutorials, authentication,
  relay server for remote access without port-forwarding, WiFi hotspot↔station
  switch, mDNS (`nina.local`), remote terminal (SSH in the browser), debug
  logging, polar alignment (TPPA + rudimentary), and SBC self-update.

## Tested hardware

Polaris supports a huge range of gear through INDI (400+ Linux drivers),
ASCOM/Alpaca, and the native camera SDKs, far more than any one person can
own. The list below is the hardware that has actually been used with Polaris in
the field, as a starting point; anything INDI/ASCOM/Alpaca-compatible should
work even if it isn't listed. Reports of other working (or broken) gear are
welcome via an issue or PR.

| Category | Tested devices |
|---|---|
| **Mounts** | ZWO AM3 · AM3N · AM5 · OnStep (LX200) |
| **Cameras (OSC / DSLR)** | ZWO ASI585MC Pro · ASI678MC · ASI715MC · SVBony SV405CC · SV605CC · SV105 · Canon EOS Rebel T100 (4000D) · Canon EOS Rebel SL2 (200D) |
| **Focus motors** | ZWO EAF · Gemini Focusing Motor / Focuser Pro |
| **Guide cameras** | ZWO ASI120MM |
| **Filter wheels** | ZWO EFW Mini |
| **SBCs (server host)** | Raspberry Pi 4 (8 GB) · Raspberry Pi 5 (4 GB) · Orange Pi 4 Pro (4 GB) · Orange Pi 5 Pro (4 GB) · Radxa Dragon Q6A (6 GB) |

> **Recommended board:** the Orange Pi 4 Pro (4 GB). It is the best balance of cost
> and capability measured so far, scoring 180 for roughly $50 to $70. The Radxa
> Dragon Q6A is the fastest (296); the Orange Pi 5 Pro (274) is the one with the
> proven GPU and NPU acceleration path.
>
> Windows and x86 Linux mini-PCs also run the backend (see [Deployment](#deployment)).
> For benchmark scores per board, see the benchmark reference in the user guide.

## Documentation

The full documentation lives in [`docs/`](docs/README.md); that index
organizes every page by area. The essentials:

- **[User Guide](docs/user-guide/README.md)**: start here. Install →
  first night → end-to-end workflow, plus a reference page per sidebar tab.
- **[Feature overview](docs/FEATURES.md)**: what every tab does, at a glance.
- **[API & Configuration reference](docs/api-reference.md)**: REST endpoints,
  WebSocket streams, `appsettings.json`, environment variables.
- **[Requirements matrix](REQUIREMENTS.md)**: dependencies per platform.
- **[Architecture](ARCHITECTURE.md)** · **[Contributing](CONTRIBUTING.md)**
- Setup guides: [Raspberry Pi](docs/user-guide/raspberry-pi-setup.md),
  [GraXpert](docs/graxpert-setup.md), [Siril](docs/siril-setup.md),
  [DSLR (Canon](docs/dslr-windows-canon.md) /
  [Nikon](docs/dslr-windows-nikon.md) /
  [Sony](docs/dslr-windows-sony.md) /
  [Linux](docs/dslr-linux.md)), [mounts & WiFi](docs/mounts-wifi.md).

## Architecture

```
nina-polaris/
├── src/
│   ├── NINA.Polaris/              ← ASP.NET Core app (Kestrel, Minimal API)
│   │   ├── Program.cs              ← Host builder, service registration
│   │   ├── Endpoints/              ← REST API (13 endpoint groups)
│   │   ├── WebSocket/              ← Image stream + status broadcast
│   │   ├── Services/               ← Business logic layer
│   │   └── wwwroot/                ← Web UI (HTML, JS, CSS)
│   │
│   ├── NINA.Core.Portable/         ← Shared enums, models, utilities (net10.0)
│   ├── NINA.Image.Portable/        ← Image processing, FITS I/O, statistics (net10.0)
│   └── NINA.INDI/                  ← INDI protocol client (net10.0)
│       ├── Protocol/               ← XML parser/writer
│       ├── Client/                 ← TCP client, blob receiver, connection manager
│       └── Devices/                ← 9 device type implementations
│
│   ├── NINA.Relay.Protocol/        ← Shared multiplexed frame format (net10.0)
│   └── NINA.Relay.Server/          ← Standalone reverse-tunnel relay (ASP.NET Core)
│
├── tests/
│   └── NINA.Polaris.Test/         ← 294 unit tests (NUnit)
│
├── deploy/                         ← Deployment scripts
│   ├── nina-polaris.service        ← systemd unit file
│   ├── install.sh                   ← Linux installer
│   ├── publish-linux-arm64.sh       ← RPi build script
│   ├── publish-win-x64.ps1         ← Windows build script
│   └── docker-build.sh             ← Multi-arch Docker buildx
│
├── Dockerfile                      ← Multi-stage, linux/amd64 + arm64
└── docker-compose.yml              ← NINA + optional indiserver sidecar
```

### Technology Stack

| Layer | Technology | Purpose |
|-------|-----------|---------|
| Web server | Kestrel (standalone) | Native .NET, no nginx/IIS needed |
| API | ASP.NET Core Minimal API | Low overhead, AOT-friendly |
| Real-time (images) | WebSocket (binary) | JPEG or LZ4-compressed raw frames, adaptive |
| Real-time (status) | WebSocket (JSON) | Equipment + sequence + guider + AF + meridian flip at 1Hz |
| Frontend framework | Alpine.js v3 | Reactive UI (~15KB, no build step) |
| UI typeface | Inter (SIL OFL 1.1, self-hosted) | Variable woff2 for every weight + italic, ~740 KB total. No external CDN call, the UI looks the same online and offline |
| Charts | Chart.js v4 | Guiding, focus, HFR, temperature, histogram, altitude |
| Sky map | stellarium-web-engine (AGPLv3, sandboxed in `/sky/` iframe) | WebGL2 sky viewer with Gaia stars, DSO surveys, constellation art, atmosphere, HiPS Milky Way tiles |
| Image viewer | OpenSeadragon | Full-resolution zoom/pan over last frame |
| Image rendering | WebGL2 shaders | GPU debayer + MTF stretch (CPU fallback) |
| Image encoding | SkiaSharp | Cross-platform JPEG / PNG encoding (incl. STUDIO previews + thumbnails) |
| FITS I/O | Custom FITSWriter | Extended headers per N.I.N.A. manual spec |
| XISF I/O | Custom XISFWriter | PixInsight native, LZ4-compressed, FITSKeyword mirrored |
| TIFF export | Custom TiffWriter | Baseline uncompressed 8-bit / 16-bit grayscale (SkiaSharp doesn't ship TIFF) |
| STUDIO frame index | Microsoft.Data.Sqlite | On-disk metadata cache so 2000-frame sessions list in &lt; 50 ms |
| Astronomy ephemeris | CosineKitty.AstronomyEngine | Planet positions for the Tonight's Best panel (MIT, ~150 KB, no native deps) |
| Sun / moon math | SunCalc (BSD-2, vendored) | Sunset / sunrise / twilight / moon phase for the Weather panel |
| Weather forecast | 7Timer ASTRO (HTTP, no key) | Cloud / seeing / transparency, 3-day window in 3 h slots |
| Compression | K4os.Compression.LZ4 | Fast image compression (~2GB/s) |
| Equipment drivers | INDI protocol (TCP/XML) + Alpaca (HTTP) | 400+ Linux drivers + ASCOM over network |
| Plate solving | ASTAP / PlateSolve3 / Astrometry.net (online + local) | Strategy dispatcher with primary + blind fallback |
| Guiding | PHD2 (TCP/JSON-RPC, port 4400), fully managed | Profile switch, equipment connect, process launch/shutdown |
| Remote access | NINA.Relay.Server reverse tunnel | Public access without inbound port-forwarding |
| Discovery | Makaretu.Dns.Multicast | mDNS announcer for `nina.local` |
| Geocoding | Nominatim (OpenStreetMap, proxied) | Address → coordinates for location setup |
| Stellarium sync | HTTP (Remote Control plugin, port 8090) | Pull selected object as target |
| Logging | Serilog | Structured logging to console + file |
| Target framework | .NET 10.0 | Latest LTS, cross-platform |

## Getting Started

### Prerequisites

Minimum to build + run from source:

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Git (with submodules: stellarium-web-engine is pulled at build time)
- On Linux for hardware control: `sudo apt install indi-full`
- Optional plate-solving: [ASTAP](https://www.hnsky.org/astap.htm) +
  H17/H18 database

For the complete tooling matrix, Windows + Linux ARM (Raspberry Pi) +
Linux x64, required vs optional per feature, firewall rules, hardware
sizing, see **[REQUIREMENTS.md](REQUIREMENTS.md)**.

### Build & Run (Development)

```bash
git clone https://github.com/DanWBR/nina-polaris.git
cd nina-polaris
dotnet build
dotnet run --project src/NINA.Polaris
```

Open `http://localhost:5000` in your browser.

### Run Tests

```bash
dotnet test
```

## Deployment

### Raspberry Pi 4 / 5 (one-line install)

The `.deb` package (built automatically by GitHub Actions on every
tag push) handles user creation, systemd unit, indi-web venv, apt
dependencies, and self-signed HTTPS cert generation. End-user install:

```bash
wget https://github.com/DanWBR/NINA.Polaris/releases/latest/download/polaris_arm64.deb
sudo apt install ./polaris_arm64.deb
# 30 seconds later: Polaris running at https://<hostname>.local:5000
```

The postinst prints the URL, sets up the service, and starts it.
Full breakdown in [packaging/README.md](packaging/README.md). Pi-
specific end-to-end recipe (hardware checklist, OS flashing, optional
SSD mount) in [docs/user-guide/raspberry-pi-setup.md](docs/user-guide/raspberry-pi-setup.md).

**Manage the service:**

```bash
sudo systemctl status polaris       # Check status
sudo journalctl -u polaris -f       # Follow logs
sudo systemctl restart polaris      # Restart
```

### Other Linux (portable tarball)

For non-Debian distros (Fedora, Arch, etc) or when you prefer no
systemd integration:

```bash
wget https://github.com/DanWBR/NINA.Polaris/releases/latest/download/polaris-linux-arm64.tar.gz
tar -xzf polaris-linux-arm64.tar.gz
cd polaris-linux-arm64
./NINA.Polaris   # foreground; wire your own service unit if needed
```

Replace `linux-arm64` with `linux-x64` for Intel/AMD 64-bit Linux.

### Windows Mini PC

Download the portable zip from
[GitHub Releases](https://github.com/DanWBR/NINA.Polaris/releases/latest):

```powershell
# x64 (most desktops/laptops):
Invoke-WebRequest -Uri "https://github.com/DanWBR/NINA.Polaris/releases/latest/download/polaris-win-x64.zip" -OutFile polaris.zip
Expand-Archive polaris.zip
cd polaris-win-x64
.\NINA.Polaris.exe

# ARM64 (Surface Pro X, some Copilot+ PCs):
Invoke-WebRequest -Uri "https://github.com/DanWBR/NINA.Polaris/releases/latest/download/polaris-win-arm64.zip" -OutFile polaris.zip
Expand-Archive polaris.zip
cd polaris-win-arm64
.\NINA.Polaris.exe
```

Open `https://localhost:5000` (accept the self-signed cert once).

For unattended Windows installs, wire your own service via `sc.exe`,
NSSM, or a scheduled task. Build-from-source path:

```powershell
.\deploy\publish-win-x64.ps1
```

### Docker

Multi-stage `Dockerfile` and `docker-compose.yml` are checked in. Builds for both `linux/amd64` and `linux/arm64`:

```bash
# Single host build (uses your platform)
docker compose up -d --build

# Multi-arch build + push to registry
REGISTRY=ghcr.io/yourname ./deploy/docker-build.sh latest
```

The default compose file runs in `network_mode: host` so mDNS and INDI LAN
reach work out of the box. Add `--profile indi` to also start an
indiserver sidecar with the standard simulators (good for testing with no
hardware).

Persistence:
- `nina-data` volume → profiles + trained-flat exposures
- `./images` bind-mount → captured FITS output


## API & Configuration

The Web UI is built entirely on a documented REST + WebSocket surface, so
everything it does is scriptable. The complete endpoint list, WebSocket stream
payloads, `appsettings.json` keys, and environment variables are in:

**[docs/api-reference.md](docs/api-reference.md)**

## Performance Targets

| Metric | Target | Notes |
|--------|--------|-------|
| Memory | < 500 MB | RPi 4 with 2GB RAM |
| Startup | < 5 seconds | RPi 4 |
| Image relay | ~3-10 MB/frame | LZ4 compressed, fits WiFi 5GHz |
| JPEG preview | ~200-400 KB | For mobile/weak clients |
| Frontend bundle | ~580 KB total | Alpine.js + libs, cacheable |
| WASM live-stack bundle | ~12 MB on disk, ~3 MB gzipped | One-time download per browser |
| Status broadcast | 1 Hz | Equipment + sequence state |

### Testing without hardware

Polaris ships with a one-click button to spawn a fake telescope +
camera + focuser + filter wheel. Open Settings → Equipment simulator
→ Launch. The simulated camera renders **real stars** from the GSC
catalog at whatever RA/Dec the simulated mount is pointing at,
plate solve, auto-focus, live stacking all work end-to-end against
it. Linux/macOS uses INDI simulators (`apt install indi-bin`);
Windows uses Alpaca Omni Simulator. See
[docs/user-guide/simulator-mode.md](docs/user-guide/simulator-mode.md).

### Client-side compute offload (CLST)

Live stacking can run **in your browser** via a WebAssembly module
that reuses the same `NINA.Image.Portable` algorithms the server
runs. On Pi 2 / Pi 3 hosts this is the only way to keep up, the
Pi just orchestrates equipment + relays raw frames, the browser
does StarDetector + alignment + accumulator. Auto-detected on WS
handshake; per-rig override in the LIVE tab toolbar. See
[docs/user-guide/client-side-compute.md](docs/user-guide/client-side-compute.md).

## Support the project

If Polaris Astro Controller saves you an evening of fiddling with rigs and you want to
chip in for hosting / a coffee / dark-sky travel:

[**❤️ Donate via Stripe**](https://buy.stripe.com/9B68wPeoLcMSgOz2iJbMQ02)

Donations are entirely optional, the project stays free and open-source
either way. Bug reports and PRs are just as welcome (see below).

## Contributing

Contributions are welcome! This project follows the same coding standards as the main [N.I.N.A. repository](https://github.com/isbeorn/nina).

### Project Structure for Contributors

- **Endpoints** are in `src/NINA.Polaris/Endpoints/`, each is an extension method on `WebApplication`
- **Services** are in `src/NINA.Polaris/Services/`, registered as singletons in `Program.cs`
- **INDI devices** follow a consistent pattern in `src/NINA.INDI/Devices/`
- **Frontend** is plain HTML/JS/CSS in `src/NINA.Polaris/wwwroot/`, no build step required
- **Tests** go in `tests/NINA.Polaris.Test/` using NUnit

## Data attribution

When the Photometric Color Calibration (PCC) workflow is enabled,
Polaris uses the AAVSO **APASS DR10** star catalog under a CC-BY
4.0 license. The catalog is downloaded by `scripts/download-apass.py`
to `wwwroot/catalogs/apass/apass.db` (gitignored). If you publish
images calibrated with PCC, please credit:

> Henden, A. A., Levine, S., Terrell, D., Welch, D. L., Munari, U.,
> & Kloppenborg, B. K. (2018). "The APASS Data Release 10." VizieR
> On-line Data Catalog: II/336. https://www.aavso.org/apass

## Acknowledgements

Polaris Astro Controller stands on the shoulders of a large community of astronomy and
open-source projects. Some we derive code from, some we studied as a reference,
and many ship inside the capture and processing stack. The same list is shown
in-app under **HELP -> Credits & acknowledgements**. Thank you to every author
below. Per-component license details are in [`### Third-party licenses`](#third-party-licenses),
the [`licenses/`](licenses/) folder, and the bundled `3rd-party-licenses` notice.

**Built on**

- [N.I.N.A. - Nighttime Imaging 'N' Astronomy](https://nighttime-imaging.eu/) - Stefan Berg and the N.I.N.A. contributors. Polaris is derived from N.I.N.A. (MPL-2.0).

**Guiding & gear simulation**

- [PHD2 - Open PHD Guiding](https://openphdguiding.org/) - Andy Galasso, Bret McKee, Craig Stark and the PHD2 contributors. Managed external guider, and the reference for the native autoguider + gear simulator (BSD-3-Clause).

**Image processing & AI**

- [GraXpert](https://www.graxpert.com/) - the GraXpert development team. Background extraction, denoise and deconvolution ONNX models, and the default auto-stretch algorithm.
- [nox](https://github.com/charvey2718/nox) - charvey2718. StarNet-like star-removal model (native colour + gray), the default behind FILES → Remove stars. Code and weights MIT.
- [starrem2k13](https://github.com/code2k13/starrem2k13) - code2k13. pix2pix-style U-Net star-removal model, an alternative in FILES → Remove stars. Code and weights MIT.
- [StarNet++](https://github.com/nekitmm/starnet) - Nikita Misiura (nekitmm). The original star-removal neural network. Code MIT; pre-trained weights © Nikita Misiura, CC BY-NC-SA 4.0 (NonCommercial).
- [Siril](https://siril.org/) - the Free-Astro / Siril team. Optional external pre-processing and stacking.

**Plate solving**

- [ASTAP](https://www.hnsky.org/astap.htm) - Han Kleijn. Default fast offline solver and star database.
- [Astrometry.net](https://astrometry.net/) - Dustin Lang, David W. Hogg and collaborators. Local and online blind solving.
- [PlateSolve3](https://planewave.com/) - PlaneWave Instruments. Alternative solver.

**Equipment, protocols & camera SDKs**

- [INDI Library](https://indilib.org/) - Jasem Mutlaq and the INDI community. Primary equipment-control protocol.
- [ASCOM Initiative & Alpaca](https://ascom-standards.org/) - the ASCOM Initiative. Windows COM drivers and the cross-platform Alpaca protocol.
- [ZWO ASI SDK](https://www.zwoastro.com/) - Suzhou ZWO Co., Ltd.
- [SVBony SDK](https://www.svbony.com/) - SVBONY.
- [Player One SDK](https://player-one-astronomy.com/) - Player One Astronomy.
- [ToupTek SDK](https://www.touptek-astro.com/) - ToupTek Astro.
- [Altair SDK](https://www.altairastro.com/) - Altair Astro (altaircam, a ToupTek OEM SDK).
- [Nikon SDK](https://sdk.nikonimaging.com/) - Nikon Corporation. Nikon DSLR / mirrorless support.
- [Canon EDSDK](https://developercommunity.usa.canon.com/) - Canon Inc. Canon EOS DSLR / mirrorless support on Windows.
- [Sony Camera Remote SDK](https://support.d-imaging.sony.co.jp/app/sdk/en/index.html) - Sony Corporation. Sony Alpha camera support on Windows.
- [libgphoto2 / gPhoto2](http://www.gphoto.org/) - the gPhoto team. DSLR / mirrorless support on Linux (via the INDI gphoto driver).

**Sky data, catalogs & astrometry**

- [OpenNGC](https://github.com/mattiaverga/OpenNGC) - Mattia Verga. Bundled NGC/IC/Messier/Caldwell catalog (CC BY-SA 4.0).
- [APASS - AAVSO Photometric All-Sky Survey](https://www.aavso.org/apass) - the AAVSO. Reference photometry for color calibration.
- [Aladin Lite](https://aladin.cds.unistra.fr/) - CDS, Universite de Strasbourg / CNRS. Interactive sky atlas.
- [Stellarium Web Engine](https://github.com/Stellarium/stellarium-web-engine) - Stellarium Labs / Guillaume Chereau and contributors. WebGL planetarium sky view (AGPL-3.0).
- [Astronomy Engine](https://github.com/cosinekitty/astronomy) - Don Cross. High-precision ephemeris and coordinate math.

**In-browser UI**

- [Alpine.js](https://alpinejs.dev/) - Caleb Porzio and contributors.
- [Chart.js](https://www.chartjs.org/) - the Chart.js contributors.
- [OpenSeadragon](https://openseadragon.github.io/) - the OpenSeadragon contributors.
- [noVNC](https://novnc.com/) - the noVNC authors (MPL-2.0).
- [xterm.js](https://xtermjs.org/) - the xterm.js authors.
- [SunCalc](https://github.com/mourner/suncalc) - Vladimir Agafonkin.
- [SortableJS](https://sortablejs.github.io/Sortable/) - the SortableJS contributors.

**Server & .NET**

- [Silk.NET](https://github.com/dotnet/Silk.NET) - the .NET Foundation. OpenCL bindings for the SBC GPU compute backend.
- [SkiaSharp](https://github.com/mono/SkiaSharp) - Microsoft, wrapping Google's Skia.
- [ONNX Runtime](https://onnxruntime.ai/) - Microsoft. Runs the GraXpert AI models in the browser.
- [YARP](https://github.com/microsoft/reverse-proxy) - Microsoft. Reverse proxy for embedded device web UIs.
- [.NET Community Toolkit (MVVM)](https://github.com/CommunityToolkit/dotnet) - the .NET Foundation.
- [K4os.Compression.LZ4](https://github.com/MiloszKrajewski/K4os.Compression.LZ4) - Milosz Krajewski.
- [LettuceEncrypt](https://github.com/natemcmaster/LettuceEncrypt) - Nate McMaster.
- [Serilog](https://serilog.net/) - the Serilog contributors.
- [Json.NET](https://www.newtonsoft.com/json) - James Newton-King.
- [SSH.NET](https://github.com/sshnet/SSH.NET) - the SSH.NET contributors.
- [SMBLibrary](https://github.com/TalAloni/SMBLibrary) - Tal Aloni. Pure-managed SMB1/SMB2 client for the "auto-push images to network storage" backend.
- [net-mdns (Makaretu.Dns)](https://github.com/richardschneider/net-mdns) - Richard Schneider. `nina.local` discovery.
- [SQLite](https://www.sqlite.org/) - D. Richard Hipp and the SQLite team.
- [Rockchip RKNN runtime](https://github.com/airockchip/rknn-toolkit2) - Rockchip. Optional NPU acceleration on RK35xx boards.

Plus the wider amateur astronomy and free-software communities. If your work is
used here and not listed, it is an oversight, not an intent, please
[let us know](https://github.com/DanWBR/NINA.Polaris).

## License

Polaris Astro Controller as a whole is licensed under the **GNU Affero General Public
License v3.0** (AGPL-3.0). See [`LICENSE.txt`](LICENSE.txt) and
[`NOTICE`](NOTICE).

Portions are derived from [N.I.N.A. - Nighttime Imaging 'N' Astronomy](https://nighttime-imaging.eu/),
Copyright (C) Stefan Berg and the N.I.N.A. contributors, under the **Mozilla
Public License 2.0** ([`licenses/MPL-2.0.txt`](licenses/MPL-2.0.txt)). Those
files keep their MPL header; per MPL-2.0 section 3.3 they are combined into
this AGPL-3.0 Larger Work and a recipient may use those specific files under
either the MPL-2.0 or the AGPL-3.0.

A limited additional permission (linking exception) covers proprietary camera
vendor SDKs and dynamically-loaded plugins - see
[`licenses/LINKING-EXCEPTION.txt`](licenses/LINKING-EXCEPTION.txt).

> Because Polaris is a network-served application, AGPL-3.0 section 13 applies:
> if you run a modified version as a service, you must offer its source to
> users. Releases published before the relicensing remain available under the
> MPL-2.0.

### Third-party licenses

- **PHD2 (OpenPHDGuiding)** -- BSD-3-Clause. The native autoguider
  (`NINA.Guider.Portable`) ports PHD2's core guiding math (single-star
  centroid, calibration, Hysteresis + Resist-Switch algorithms,
  camera/mount transforms) to C#. Each ported file carries the PHD2
  BSD-3 header; the full license text is in
  [`licenses/PHD2-LICENSE.txt`](licenses/PHD2-LICENSE.txt). PHD2 is
  Copyright (c) the Open PHD Guiding development team and the Max Planck
  Society. See https://openphdguiding.org.
- **Silk.NET** -- MIT. Managed OpenCL bindings used by the optional SBC GPU
  compute backend (`NINA.Polaris.Services.OpenCl`). Copyright (c) .NET
  Foundation and Contributors. See https://github.com/dotnet/Silk.NET.
