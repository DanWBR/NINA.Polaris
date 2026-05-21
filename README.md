# NINA Headless

**Cross-platform headless astronomy controller for Raspberry Pi, ARM64 SBCs, and Windows mini PCs.**

NINA Headless is a lightweight, browser-controlled astrophotography system built on ASP.NET Core. It brings the power of [N.I.N.A.](https://nighttime-imaging.eu/) (Nighttime Imaging 'N' Astronomy) to single-board computers and small-form-factor PCs, with a responsive Web UI accessible from any device on the network.

The Raspberry Pi (or Windows mini PC) acts as a data broker — controlling hardware, saving FITS files, and streaming images — while your laptop, tablet, or phone handles all the heavy rendering in the browser.

```
Browser (laptop / tablet / phone)        Raspberry Pi / Mini PC
┌──────────────────────────────┐         ┌──────────────────────────┐
│  Web UI (Alpine.js)          │◄──HTTP──►│  ASP.NET Core + Kestrel  │
│  Live preview (Canvas/WebGL) │◄──WS────►│  INDI client (TCP 7624)  │
│  Image processing (client)   │         │  Plate solving (ASTAP)   │
│  Sky explorer                │         │  Sequence engine         │
└──────────────────────────────┘         │  Live stacking           │
                                         └──────────────────────────┘
```

## Features

### Equipment Control via INDI

Full INDI protocol client with support for 400+ Linux drivers:

- **Camera** — Capture, exposure control, gain, binning, ROI, cooler temperature
- **Telescope/Mount** — Slew, GoTo, park/unpark, tracking (sidereal/lunar/solar), NSEW manual control
- **Focuser** — Absolute/relative move, step control, temperature readout
- **Filter Wheel** — Position selection by slot number or filter name
- **Guider** — Pulse guiding in 4 directions, guide camera exposure
- **Dome** — Azimuth slew, shutter open/close, park/unpark, slave mode
- **Rotator** — Angle positioning, reverse toggle
- **Weather** — Temperature, humidity, dew point, wind, pressure, cloud cover, SQM, rain, safety status
- **Flat Panel** — Light on/off, brightness control, dust cap open/close

### Real-Time Image Streaming

Dual-mode WebSocket image streaming with automatic format negotiation:

- **JPEG mode** (default) — Server-side auto-stretch and JPEG encoding, works on all browsers (~300KB per frame)
- **Raw mode** — LZ4-compressed 16-bit pixel data with client-side WebGL debayer and MTF stretch (~3-10MB per frame)
- Backpressure handling — slow clients skip frames instead of falling behind
- Dead client eviction after consecutive send failures
- REST endpoint for latest preview image (`/api/image/latest/preview`)

### Live Stacking (EAA)

Real-time stacking for electronically assisted astronomy:

- Star detection via flood-fill HFR algorithm
- Triangle-based star matching for alignment
- Affine transform registration (translation + rotation + scale)
- Running average accumulation buffer
- Start/stop/reset controls with frame counter

### Plate Solving & Centering

Strategy-based plate-solving dispatcher with four interchangeable backends and
a primary + blind-fallback pipeline:

- **ASTAP** — fast offline solver, hint-driven, the default primary
- **PlateSolve3** — PlaneWave's CLI, excellent at long focal lengths and small FOVs (≤10 stars), requires hints
- **Astrometry.net online** — REST API at nova.astrometry.net, truly blind, slow but robust (API key required)
- **Astrometry.net local** — `solve-field` wrapper with ±20% pixel-scale window, blind-capable
- Configurable per-installation: `PlateSolve:PrimarySolver`, `PlateSolve:BlindSolver`, `PlateSolve:UseBlindFallback`
- **Slew & Center** — Automated loop: slew to target → capture → solve → compute error → sync → re-slew (converges in 2-3 iterations, ~30-60s total)
- Configurable tolerance (default: 30 arcsec), async job tracking with real-time status polling
- Result carries `SolverUsed` so the UI knows which backend produced it

### Guiding (PHD2) — full management

PHD2 is a first-class managed device, not just a black box we send commands to.

**Connection + telemetry:**
- JSON-RPC 2.0 line-framed protocol with async event loop on TCP (default port 4400)
- Ring buffer of last 300 GuideSteps with running RMS RA / RMS Dec / total RMS + peak values
- Live RA/Dec error chart (Chart.js) with auto-scaling Y-axis
- Calibration data captured automatically after a successful CalibrationComplete event
- Alert + settle status surfaced in the UI as toasts and banners
- Shows which guide camera + mount PHD2 is actually using (via `get_current_equipment`)

**Management — control PHD2 itself from the Web UI:**
- **Profile switcher** — list every PHD2 profile, switch with one click (auto-disconnects equipment first as PHD2 requires)
- **Equipment connect/disconnect** — tell PHD2 to wire up its own gear
- **Exposure** — dropdown populated from `get_exposure_durations` (e.g. "1.0s" / "100ms")
- **Dec guide mode** — Auto / North / South / Off
- **Auto-detect install location** — walks the well-known PHD2 install paths per OS (Windows: Program Files / Program Files (x86) / `%LocalAppData%\Programs`; macOS: `/Applications/PHD2.app`; Linux: `/usr/bin`, `/usr/local/bin`, `/opt/phd2/bin`, `/snap/bin`, plus a `$PATH` walk). When not detected the Guider tab surfaces an inline "Download PHD2" banner with a direct link
- **Launch / Shutdown PHD2 process** — when an executable is detected (or `PHD2:ExecutablePath` is set), NINA Headless can spawn PHD2 on the same host (loopback only) and gracefully shut it down via the `shutdown` RPC (falls back to process kill only if we own the process)
- **Auto-start on boot** — a single checkbox in the Guider tab makes the headless app launch PHD2 and connect the JSON-RPC client ~2s after every startup. Persisted per profile; survives restarts. Backed by a hosted service that retries the connect 5× in case PHD2's event server is slow to come up
- Commands: start guiding / stop / loop / pause / resume / dither (with settle pixels + time + timeout) / auto-select star / clear calibration / clear history

### Auto-Focus (V-Curve)

Automated focus point determination via symmetric sweep:

- Captures N exposures around the current focuser position, measures HFR per sample via flood-fill star detection (median HFR for robustness against outliers)
- Least-squares parabola fit through valid samples; moves to the vertex
- Configurable step size, point count, exposure, minimum stars, backlash compensation, post-focus confirmation frame
- Live V-curve chart (Chart.js scatter) with fitted parabola and best-position marker
- Restores starting position automatically on cancel or failure

### Meridian Flip Automation

Hands-off pier-side change during a sequence:

- Static LST/GMST math validated against USNO J2000 reference
- Workflow: pause PHD2 → re-slew to target (mount firmware flips) → settle → plate-solve recenter via Slew & Center → optional auto-focus → resume PHD2 guiding
- Configurable minutes-after-meridian trigger threshold (default 5 min)
- Live "Meridian in 1h 23m" countdown in the Sequence tab
- Safe failure paths: errors and cancels always try to resume guiding

### Advanced Sequencer (tree-based)

A full conditional-execution engine alongside (not replacing) the legacy
Simple Sequencer. Toggle the default tab via **Settings → Sequencer →
"Use Advanced Sequencer by default"** — both stay available either way.

**Tree model:**
- **Containers** group children: `Sequential` (run in order), `Parallel`
  (run concurrently, fail-fast on any child), `DeepSkyObject` (slew &
  plate-solve-center on a target before running children),
  `Templated` (paste-in a saved reusable fragment)
- **Instructions** are atomic actions; 30+ shipping:
  - Mount: Slew, Center (plate-solve), Park / Unpark, SetTracking, SolveAndSync
  - Camera: TakeExposure (N frames + filter / gain / binning / image type),
    CoolCamera (setpoint + tolerance), WarmCamera (gradual ramp at °C/min)
  - Focuser: AutoFocus (V-curve), MoveFocuser, MoveToFilterOffset
  - Filter wheel: SwitchFilter (by name or position)
  - Guider: StartGuiding, StopGuiding, Dither, AutoSelectStar
  - Dome: Open / Close shutter, Park, SlewToAzimuth, SyncToScope (Alt/Az math)
  - Flat panel: Open/Close cover, SetBrightness, ToggleLight
  - Rotator: RotateToAngle
  - Flow control: WaitForTime, WaitUntilTime (UTC), WaitUntilAltitude,
    WaitForSunBelowHorizon (low-precision sun alt for twilight),
    WaitForMoon (Above / Below altitude)
  - External: RunExternalScript (stdout/stderr captured, exit-code aware),
    SendHttpRequest (webhooks for Discord / Slack / dashboards)
- **Conditions** are loop predicates (containers with `isLoop=true` keep
  iterating while every condition holds): Until Time / Altitude / N Exposures
  / Duration / Moon Sets / While Safe (cloud cover + wind from weather device)
- **Triggers** fire between every child step:
  - Auto-focus on Temperature Change / HFR Increase / Every N Minutes / Filter Change
  - Meridian Flip (delegates to the existing service)
  - Dither After N Exposures (skipped silently when PHD2 isn't guiding)
  - Center After Drift (periodic plate-solve check against pinned coords)
  - Safety (cloud cover / wind / mount disconnect → graceful abort with reason)

**Persistence + execution:**
- Polymorphic JSON format with `$type` discriminator and `Version` field;
  every entity carries a stable Id so the editor can reference nodes
  across edits
- Validate() bubbles up errors with breadcrumb paths
  (`[DSO 'M31'/Lights] Exposure must be positive`); the engine refuses
  Start on a failing tree
- File-based template store (`Sequencer:TemplateDir`,
  default `./sequencer-templates/`) for reusable fragments hydrated
  into `TemplatedContainer` at load time
- Background runner with cancellation that propagates to every child;
  containers honour an `AbortRequested` flag (set by the Safety trigger)
  between every step

**Tree editor UI:**
- New "Adv" tab opens a tri-pane layout: palette (categorised by device),
  tree (status colour-coded per node), properties (auto-generated form
  by field type)
- Sortable.js drag-handle on the type badge to reorder siblings
- Save / Download JSON / Upload JSON / Validate / Start / Stop in the toolbar
- Live status mirroring — during a run the tree colours update every 2s
  showing what's Running / Completed / Failed / Skipped, plus the
  Safety-trigger abort reason if one fires

### Dithering

Random pixel-offset between frames to defeat fixed-pattern noise:

- Calls PHD2 `dither` RPC after every N successfully captured frames
- Waits for SettleDone event before next exposure
- Configurable dither pixels, every-N-frames, RA-only toggle, settle parameters
- Silent skip with debug log when PHD2 isn't connected or guiding — sequence never aborts

### Sky Catalog & Sky Atlas

Embedded deep sky catalog with 200+ objects:

- All 110 Messier objects + popular Caldwell + notable NGC/IC targets
- Fuzzy search by designation, common name, or alias ("M31", "Andromeda", "NGC 224")
- **Filtered browser** — type / magnitude range / declination range, sorted brightest first
- **Altitude chart** — target altitude across tonight's window (sunset → sunrise) with civil / nautical / astronomical twilight transitions
- Object metadata: coordinates (J2000), magnitude, type, common names

### Sky Map (Aladin Lite)

Embedded WebGL sky viewer for visual target selection:

- HiPS tile surveys: DSS2 color/red, 2MASS, SDSS9, Pan-STARRS DR1, Mellinger
- Camera-FOV overlay calculated from sensor + focal length (auto-applies cos(Dec) compensation)
- Click-to-pick targets, "Center on mount" button
- Stellarium Remote Control sync — pull the currently-selected object from Stellarium with one click

### Sequence Engine + Image Persistence

Target list execution with automated imaging:

- Multi-target sequences with per-target exposure, gain, binning, filter, and frame count
- Automatic slew-to-target before capture
- Pause/resume with SemaphoreSlim gate
- Real-time progress tracking via WebSocket (1Hz status broadcast)
- **Persisted output** in two formats, selectable per profile:
  - **FITS** — extended headers (camera / telescope / focuser / rotator / filter / weather / observer / target — 30+ keywords per the N.I.N.A. manual spec)
  - **XISF** (PixInsight native) — UInt16 monochrome with optional LZ4 compression (~3-10× smaller than FITS); native `<Property>` elements alongside `<FITSKeyword>` mirrors so any downstream tool works
- Configurable filename pattern (`{target}_{filter}_{exposure}s_{date}_{seq}` etc.)
- Optional dithering between frames + automatic meridian flip
- Focal length / focal ratio come from the **active equipment rig** (see below), not a global setting

### Flat Wizard

Automated flat-field acquisition:

- Binary search on exposure time per filter until median ADU lands within tolerance of target (default 30000 ADU ± 5%)
- Captures N flat frames at the converged exposure, tagged `IMAGETYP=FLAT`
- Per-(filter, binning) trained exposures persisted to `trained-flats.json` for next session

### Web UI

Responsive, dark-themed interface inspired by ASIAIR:

- **Live View** — Real-time camera preview with WebGL2 GPU rendering (debayer + MTF stretch on GPU), star annotations overlay, crosshair + 3x3 grid, hover pixel readout (raw ADU or RGB), manual stretch sliders, image-history thumbnail strip, HFR + star-count history chart, detailed statistics panel + histogram, full-resolution zoom viewer (OpenSeadragon)
- **Equipment** — Rig selector bar at the top, then cards for Camera (with auto-detected sensor dimensions + temperature + cooler power chart), Mount, Focuser, Filter Wheel, Rotator, Flat Panel, Dome, Weather, Guider (PHD2). Per-device select / connect / disconnect plus quick controls. "💾 Save selections" button captures the current dropdown picks into the active rig. "Manage rigs…" opens a modal with inline editing of focal lengths, cooler target, and per-rig device assignments
- **Mount Control** — NSEW directional pad, tracking toggle, park/unpark, GoTo via Sky Explorer
- **Focus** — Manual stepper + full Auto-Focus V-curve panel (start/abort, live progress bar, fitted parabola chart, best-position marker)
- **Guider** — PHD2 connection panel + **Launch PHD2** button (spawns the auto-detected install) + **Auto-start on boot** checkbox (persists in profile, launches PHD2 on every Headless start), inline "Download PHD2" banner when not installed, PHD2-management bar (profile dropdown, exposure dropdown, Dec-mode, connect-equipment toggle, Shutdown), live RA/Dec error chart with RMS readouts, settle parameters, full control buttons (Guide / Loop / Auto-select Star / Pause / Resume / Stop / Dither). Surfaces PHD2's own guide camera + mount names
- **Sky Explorer** — Aladin Lite sky map with HiPS surveys + camera FOV overlay. Object search, filtered catalog browser (type / magnitude / Dec), "Tonight's altitude" chart with twilight bands, Stellarium sync, Slew & Center, Add to Sequence
- **Sequence** — Target list editor with progress bars, collapsible Meridian Flip + Dithering panels, start/pause/resume/stop
- **Settings** — INDI connection, observatory location, image output (format: FITS or XISF), profile management. Sensor dimensions are auto-read from the connected camera; focal length lives per-rig (Equipment → Manage rigs)
- **First-run** — Location-setup modal with **address geocoder** (OpenStreetMap/Nominatim), browser geolocation, or manual lat/lon entry

**Night Mode** — Red-on-black theme that preserves dark adaptation (critical for field use).

**Mobile Responsive** — Full functionality on phones and tablets with bottom tab navigation.

### Equipment Rigs (multi-rig support)

One physical NINA Headless host frequently serves multiple physical setups —
"backyard SCT", "travel APO", "remote site mono camera + AO". Each user
profile carries a list of named **rigs**; switch in one click and every device
selector + per-rig default (cooler temperature, focuser step size, focal
length, PHD2 host/port) is reapplied automatically.

Per-rig stored data:
- Device names: Camera / Telescope / Focuser / FilterWheel / Rotator / FlatDevice / Dome / Weather (INDI names as returned by getProperties)
- Cooler target temperature, default gain / offset / binning
- Focuser step size + backlash
- **Main scope focal length** (drives FOV calculation + FITS FOCALLEN)
- **Guide scope focal length** (record-keeping + PHD2 pixel-scale sanity check)
- PHD2 endpoint (host + port)

CRUD via `/api/equipment/rigs/*`. UI: dropdown in the Equipment tab header
plus a manage-rigs modal with inline editing (rename, focal lengths,
cooler target) and quick actions (Activate, Duplicate, Delete, Save current
device selections).

Existing profiles auto-migrate on first load — the pre-existing
`LastCamera` / `LastTelescope` / etc. fields become the rig named "Default".

### Profile Management

JSON-based settings persistence with multi-profile support:

- Observatory location
- INDI connection settings
- Plate solver configuration (primary, blind fallback, paths, API keys)
- Image output directory, naming pattern, format (FITS / XISF)
- List of equipment rigs (see above)
- Save, load, and rename profiles

### Remote Access (Relay Server)

For accessing a NINA Headless host on a remote LAN (observatory site, friend's
house) without inbound port-forwarding or dynamic DNS, this repo ships a
companion **NINA.Relay.Server** project that acts as a reverse tunnel.

```
 Browser  ──HTTPS──►  relay.example.com  ──reverse WebSocket──►  Raspberry Pi
                       (NINA.Relay.Server)                        (NINA Headless,
                                                                   no public IP)
```

- NINA Headless opens an **outbound** WebSocket to the relay (firewall-friendly)
- Multiplexed binary protocol: many concurrent HTTP requests on one socket
- Auto-reconnect on the client side with exponential backoff (2s → 60s)
- Subdomain routing (`alice.relay.example.com`) OR path-prefix (`/t/alice/...`)
- Multi-tenant: each headless host has its own bearer token
- **WebSocket-over-tunnel forwarding** — image stream + status broadcasts +
  any other browser-side WS endpoint now work end-to-end through the relay
  (browser ↔ relay ↔ tunnel client ↔ local Kestrel WS, bidirectional pump)
- **JSON tenant store** (`tenants.json`) — hot-reloaded on file change; no
  server restart needed when adding or revoking tokens (legacy
  `appsettings.json` `Tenants:` section still works for trivial setups)
- **Per-tenant rate limiting** — token-bucket on both HTTP requests/sec and
  bytes/sec (request + response counted together), with configurable burst.
  Exceeding either bucket returns HTTP 429 with a `Retry-After` header
  naming which bucket tripped
- **Monthly byte quotas** + **expiring tokens** — per-tenant `monthlyBytes`
  cap (counter persists across restarts in `tenant-state.json`, auto-resets
  on the 1st UTC, HTTP 402 when exhausted) and `expiresAt` timestamp
  (auth refused after expiry — useful for trials)
- **Built-in TLS** — `Tls:Mode=letsencrypt` (LettuceEncrypt fetches +
  renews certs from Let's Encrypt automatically) or `Tls:Mode=pfx` (load a
  static `.pfx`). No reverse proxy required
- **Web admin UI** at `/admin/` — gated by `Admin:Password` (HTTP Basic);
  add/edit/delete tenants, view live tunnels + monthly usage bars,
  generate cryptographically-random tokens, reset usage counters,
  browse the audit log with per-tenant filter
- **Per-request audit log** (JSON-lines `audit.log`) — timestamp, tenant,
  method, path, status, bytes in/out, duration, source IP, outcome
  reason. Auto-rotates at 50 MB. In-memory ring buffer surfaced via
  `/_admin/audit?tenant=&limit=` and the admin UI
- **mTLS for tunnel auth** — per-tenant `clientCertThumbprint` pins the
  X.509 cert the tunnel client must present. Bearer token alone is the
  default; mTLS is opt-in per tenant. NINA Headless client points at a
  `.pfx` via `Relay:ClientCertPath` (+ optional password)
- Admin endpoints: `/_health`, `/_tunnels` (with per-tunnel byte counters),
  `/_admin/tenants` (full CRUD), `/_admin/generate-token`,
  `/_admin/usage/{token}/reset`, `/_admin/audit`, `/_admin/reload-tenants`
- Two deployment models — self-host on a $5 VPS, or use a hosted instance
- See [`src/NINA.Relay.Server/README.md`](src/NINA.Relay.Server/README.md) for
  deployment instructions, Caddy reverse-proxy example, full `tenants.json`
  schema, and protocol details

### Network Resilience

Built for unreliable field WiFi:

- WebSocket auto-reconnect with exponential backoff + jitter
- Request deduplication for in-flight API calls
- Server reachability detection with automatic recovery
- Toast notifications for connection state changes
- 15-second request timeout with abort controller
- **Adaptive bandwidth** — server measures actual WebSocket send latency and auto-downgrades raw clients to JPEG when bandwidth degrades, upgrades back when it recovers

### Discovery & Cross-Platform Drivers

- **mDNS announcer** — host reachable at `nina-<hostname>.local:5000` from any device on the LAN (no IP needed)
- **Alpaca (ASCOM HTTP) support** — UDP discovery on port 32227 plus base Camera / Telescope wrappers, so you can drive Windows-only ASCOM drivers exposed over the network

## Architecture

```
nina-headless/
├── src/
│   ├── NINA.Headless/              ← ASP.NET Core app (Kestrel, Minimal API)
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
│   └── NINA.Headless.Test/         ← 160 unit tests (NUnit)
│
├── deploy/                         ← Deployment scripts
│   ├── nina-headless.service        ← systemd unit file
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
| Charts | Chart.js v4 | Guiding, focus, HFR, temperature, histogram, altitude |
| Sky map | Aladin Lite v3 | HiPS sky surveys (DSS / 2MASS / SDSS / Pan-STARRS) |
| Image viewer | OpenSeadragon | Full-resolution zoom/pan over last frame |
| Image rendering | WebGL2 shaders | GPU debayer + MTF stretch (CPU fallback) |
| Image encoding | SkiaSharp | Cross-platform JPEG encoding |
| FITS I/O | Custom FITSWriter | Extended headers per N.I.N.A. manual spec |
| XISF I/O | Custom XISFWriter | PixInsight native, LZ4-compressed, FITSKeyword mirrored |
| Compression | K4os.Compression.LZ4 | Fast image compression (~2GB/s) |
| Equipment drivers | INDI protocol (TCP/XML) + Alpaca (HTTP) | 400+ Linux drivers + ASCOM over network |
| Plate solving | ASTAP / PlateSolve3 / Astrometry.net (online + local) | Strategy dispatcher with primary + blind fallback |
| Guiding | PHD2 (TCP/JSON-RPC, port 4400) — fully managed | Profile switch, equipment connect, process launch/shutdown |
| Remote access | NINA.Relay.Server reverse tunnel | Public access without inbound port-forwarding |
| Discovery | Makaretu.Dns.Multicast | mDNS announcer for `nina.local` |
| Geocoding | Nominatim (OpenStreetMap, proxied) | Address → coordinates for location setup |
| Stellarium sync | HTTP (Remote Control plugin, port 8090) | Pull selected object as target |
| Logging | Serilog | Structured logging to console + file |
| Target framework | .NET 10.0 | Latest LTS, cross-platform |

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (for building)
- [INDI](https://www.indilib.org/) server and drivers (Linux) — `sudo apt install indi-full`
- [ASTAP](https://www.rongent.com/astap/) (optional, for plate solving)

### Build & Run (Development)

```bash
git clone https://github.com/DanWBR/nina-headless.git
cd nina-headless
dotnet build
dotnet run --project src/NINA.Headless
```

Open `http://localhost:5000` in your browser.

### Run Tests

```bash
dotnet test
```

## Deployment

### Raspberry Pi / Linux ARM64

1. **Build on your development machine:**

```bash
chmod +x deploy/publish-linux-arm64.sh
./deploy/publish-linux-arm64.sh
```

2. **Copy to the Pi:**

```bash
scp -r publish/linux-arm64/* pi@raspberrypi:/tmp/nina-headless/
```

3. **Install on the Pi:**

```bash
ssh pi@raspberrypi
sudo bash /tmp/nina-headless/deploy/install.sh /tmp/nina-headless
```

This creates a `nina` system user, installs to `/opt/nina-headless`, enables and starts the systemd service.

4. **Access:** Open `http://raspberrypi:5000` from any device on the network.

**Manage the service:**

```bash
sudo systemctl status nina-headless    # Check status
sudo journalctl -u nina-headless -f    # Follow logs
sudo systemctl restart nina-headless   # Restart
```

### Windows Mini PC

```powershell
.\deploy\publish-win-x64.ps1
```

**Run as console app:**

```powershell
.\publish\win-x64\NINA.Headless.exe
```

**Install as Windows Service:**

```powershell
.\deploy\publish-win-x64.ps1 -InstallService
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

## API Reference

### Equipment

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/equipment/devices` | List all discovered INDI devices |
| POST | `/api/equipment/connect` | Connect to all selected devices |
| POST | `/api/equipment/disconnect` | Disconnect all devices |
| GET | `/api/equipment/status` | Aggregated status of every selected device (includes auto-derived sensor dimensions) |

### Equipment Rigs (multi-rig profiles)

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/equipment/rigs` | All rigs + active id |
| GET | `/api/equipment/rigs/active` | Active rig (full payload) |
| POST | `/api/equipment/rigs` | Create empty rig `{ name }` |
| POST | `/api/equipment/rigs/clone` | Duplicate the active rig `{ newName }` |
| PUT | `/api/equipment/rigs/{id}` | Update a rig (selections, defaults, focal lengths, PHD2 endpoint) |
| POST | `/api/equipment/rigs/{id}/activate` | Switch to this rig |
| DELETE | `/api/equipment/rigs/{id}` | Delete a rig (refuses to delete the last one) |

### Camera

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/camera/select/{name}` | Select camera by INDI device name |
| POST | `/api/camera/connect` | Connect selected camera |
| POST | `/api/camera/capture` | Capture an image `{ exposure, gain, binning, filter }` |
| POST | `/api/camera/abort` | Abort current exposure |
| POST | `/api/camera/cooler` | Set cooler `{ enabled, targetTemperature }` |
| GET | `/api/camera/status` | Camera status |

### Telescope

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/telescope/select/{name}` | Select mount |
| POST | `/api/telescope/slew` | Slew to coordinates `{ ra, dec }` |
| POST | `/api/telescope/move/{direction}` | Manual move (north/south/east/west/stop) |
| POST | `/api/telescope/park` | Park mount |
| POST | `/api/telescope/unpark` | Unpark mount |
| POST | `/api/telescope/tracking` | Toggle tracking `{ enabled }` |
| POST | `/api/telescope/abort` | Emergency stop |

### Focuser

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/focuser/select/{name}` | Select focuser |
| POST | `/api/focuser/move/relative` | Move relative `{ steps }` |
| POST | `/api/focuser/move/absolute` | Move to position `{ position }` |
| POST | `/api/focuser/abort` | Abort movement |

### Filter Wheel

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/filterwheel/status` | Current filter and position |
| POST | `/api/filterwheel/position/{slot}` | Move to slot number |
| POST | `/api/filterwheel/filter/{name}` | Move to filter by name |

### Imaging

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/image/latest/preview` | Latest image as JPEG |
| GET | `/api/image/latest/stats?withStars` | Image dimensions + mean/median/min/max/stddev/MAD (+ optional star detection HFR stats) |
| GET | `/api/image/latest/histogram?bins=256` | Pixel-value histogram |
| GET | `/api/image/latest/stars?maxStars&sigma` | Detected star list with (x, y, HFR, flux, peak) |
| GET | `/api/image/stream/clients` | Per-client WebSocket diagnostics (mode, latency, streaks) |
| POST | `/api/image/stream/adaptive` | Toggle adaptive bandwidth `{ enabled }` |

### Guider (PHD2)

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/guider/connect` | Connect to PHD2 `{ host, port }` |
| POST | `/api/guider/disconnect` | Disconnect |
| GET | `/api/guider/status` | App state, RMS, peak, settle, pixel scale, last alert |
| GET | `/api/guider/equipment` | Guide camera + mount + aux mount + AO names (`get_current_equipment`) |
| GET | `/api/guider/steps?limit=N` | Recent GuideStep history |
| POST | `/api/guider/guide` | Start guiding `{ settlePixels, settleTime, settleTimeout, recalibrate }` |
| POST | `/api/guider/dither` | Dither `{ pixels, raOnly, settle* }` |
| POST | `/api/guider/stop` / `/loop` / `/pause` / `/resume` | State changes |
| POST | `/api/guider/find-star` / `/clear-calibration` / `/clear-history` | Maintenance |
| GET | `/api/guider/profiles` | List PHD2 profiles + current one |
| POST | `/api/guider/profile/{id}` | Switch PHD2 profile (auto-disconnects equipment first) |
| GET | `/api/guider/equipment/connected` | Whether PHD2's own equipment is connected |
| POST | `/api/guider/equipment/{connect,disconnect}` | Toggle PHD2's own equipment |
| GET | `/api/guider/exposure` | Current exposure ms + list of available durations |
| POST | `/api/guider/exposure/set/{ms}` | Set guide exposure |
| GET | `/api/guider/dec-mode` | Current Dec guide mode |
| POST | `/api/guider/dec-mode/{Auto\|North\|South\|Off}` | Set Dec mode |
| GET | `/api/guider/process/status` | Is PHD2 running? did we launch it? path configured? |
| POST | `/api/guider/process/launch` | Spawn PHD2 (loopback only, polls port 4400 for up to 30s) |
| POST | `/api/guider/process/shutdown` | Graceful JSON-RPC shutdown, falls back to kill only if we own it |
| GET | `/api/guider/install-info` | Detected install (`installed`, `resolvedPath`, `downloadUrl`, `os`, `searchedPaths`) — UI uses this to surface "Download PHD2" when missing |
| POST | `/api/guider/auto-start/{true\|false}` | Persist auto-start-on-boot preference in the user profile |

### Auto-Focus

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/autofocus/start` | Start V-curve `{ steps, stepSize, exposureSeconds, minStars, backlashSteps }` |
| POST | `/api/autofocus/abort` | Abort + restore start position |
| GET | `/api/autofocus/status` | Live progress + sampled points |
| GET | `/api/autofocus/result` | Most recent completed run + fitted parabola coefficients |

### Meridian Flip

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/meridianflip/settings` | Current configuration |
| PUT | `/api/meridianflip/settings` | Update settings |
| GET | `/api/meridianflip/status` | State + LST + hour angle + minutes-to-meridian |
| POST | `/api/meridianflip/trigger` | Manual flip `{ ra, dec }` |
| POST | `/api/meridianflip/abort` | Abort in-progress flip |

### Flat Wizard

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/flatwizard/start` | Start automated flat acquisition `{ filters, framesPerFilter, targetAdu, tolerance, minExposure, maxExposure, binning }` |
| POST | `/api/flatwizard/abort` | Abort |
| GET | `/api/flatwizard/status` | Live progress + per-filter results |
| GET | `/api/flatwizard/trained` | Persisted (filter+binning → exposure) dictionary |

### Alpaca (ASCOM HTTP)

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/alpaca/discover?timeoutMs=3000` | UDP-broadcast discovery on port 32227 + per-server `/management/v1/configureddevices` enrichment |
| GET | `/api/alpaca/devices?host=&port=` | Direct device list query (skip discovery) |
| GET | `/api/alpaca/camera/info?host=&port=&device=` | Camera probe (sensor, cooler, binning) |
| GET | `/api/alpaca/telescope/info?host=&port=&device=` | Telescope probe (pointing, tracking, pier side) |
| POST | `/api/alpaca/{camera,telescope}/connect?host=&port=&device=&connect=` | Connect / disconnect |

### Stellarium

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/stellarium/target?host=&port=` | Pull currently-selected object from Stellarium Remote Control plugin |
| GET | `/api/stellarium/view?host=&port=` | Current view direction (alt / az / fov) |

### Live Stacking

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/livestack/start` | Start live stacking |
| POST | `/api/livestack/stop` | Stop live stacking |
| POST | `/api/livestack/reset` | Reset stack buffer |
| GET | `/api/livestack/status` | Stack frame count and state |

### Simple Sequence (flat list)

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/sequence` | Current sequence items and state |
| POST | `/api/sequence` | Load sequence `[{ name, exposure, gain, ... }]` |
| POST | `/api/sequence/start` | Start execution |
| POST | `/api/sequence/pause` | Pause execution |
| POST | `/api/sequence/resume` | Resume from pause |
| POST | `/api/sequence/stop` | Stop execution |
| GET | `/api/sequence/status` | Detailed progress |

### Advanced Sequencer (tree-based)

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/sequencer/document` | Current `SequenceDocument` + state + lastError + abortReason |
| POST | `/api/sequencer/document` | Load a `SequenceDocument` (JSON object) |
| GET | `/api/sequencer/document/json` | Raw JSON download for "save sequence to file" |
| POST | `/api/sequencer/document/json` | Accept raw JSON body — "load sequence from file" |
| POST | `/api/sequencer/start` | Validate + run the tree in the background |
| POST | `/api/sequencer/stop` | Cancel the run via the engine's CTS |
| POST | `/api/sequencer/validate` | Walk Validate() across the tree, return errors |
| GET | `/api/sequencer/types` | Palette listing — every known `(type, category, kind)` |
| GET | `/api/sequencer/templates` | List saved templates + their store dir |
| GET | `/api/sequencer/templates/{name}` | Load a named template |
| POST | `/api/sequencer/templates/{name}` | Save a `SequenceDocument` as a named template |
| DELETE | `/api/sequencer/templates/{name}` | Delete a template |

### Sky & Plate Solving

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/sky/catalog/search?query=M31` | Search embedded DSO catalog |
| GET | `/api/sky/catalog/{name}` | Get object by exact name |
| GET | `/api/sky/catalog/types` | Distinct object types (for filter dropdowns) |
| GET | `/api/sky/catalog/filter?query&type&minMag&maxMag&minDec&maxDec&limit` | Filtered catalog query |
| GET | `/api/sky/altitude?ra&dec&stepMinutes` | Target altitude track across tonight's window + twilight transitions |
| GET | `/api/sky/fov` | Current FOV based on optics config |
| GET | `/api/sky/solver/status` | Primary + blind solver availability and identity |
| GET | `/api/sky/solver/list` | All four plate-solver backends with id / name / available / blind flag |
| POST | `/api/sky/slew-and-center` | Start slew & center job `{ ra, dec, toleranceArcsec }` |
| GET | `/api/sky/slew-and-center/{id}/status` | Job progress |
| POST | `/api/sky/slew-and-center/{id}/cancel` | Cancel job |

### Sequence (Dither)

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/sequence/dither` | Current dither settings |
| PUT | `/api/sequence/dither` | Update dither settings `{ enabled, pixels, everyNFrames, raOnly, settle* }` |

### System

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/system/status` | System info (CPU, RAM, uptime) |
| GET | `/api/system/geocode?query=&limit=` | Address geocoding via Nominatim (rate-limited, User-Agent set) |
| GET | `/api/system/relay` | Relay tunnel status (`state`, `hostname`, `lastError`) |
| GET | `/api/system/profiles` | List profiles |
| GET | `/api/system/profile` | Active profile |
| PUT | `/api/system/profile` | Update settings |
| POST | `/api/system/profile/save-as` | Save profile as new name |
| POST | `/api/system/profile/load/{id}` | Load profile by ID |

### WebSocket Streams

| Endpoint | Type | Description |
|----------|------|-------------|
| `/ws/image-stream` | Binary | Live image frames (JPEG or raw+LZ4) |
| `/ws/status` | JSON | Equipment + sequence status at 1Hz |

**Image stream negotiation:** After connecting, send `{"mode":"jpeg"}` or `{"mode":"raw"}` to select format.

**Status message format:**

```json
{
  "type": "status",
  "equipment": {
    "indi": { "connected": true },
    "camera": { "name": "ZWO ASI2600MC", "temperature": -10.0 },
    "telescope": { "ra": 0.713, "dec": 41.27, "tracking": true, "slewing": false },
    "focuser": { "position": 12500, "temperature": 15.2 },
    "filterWheel": { "position": 3, "currentFilter": "Ha", "filters": ["L","R","G","B","Ha","OIII","SII"] }
  },
  "liveStack": { "isRunning": true, "frameCount": 42 },
  "sequence": { "state": "running", "currentItemIndex": 1, "totalFrames": 100, "totalFramesCompleted": 37 }
}
```

## Configuration

### appsettings.json

```json
{
  "Indi": {
    "Host": "localhost",
    "Port": 7624
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information"
    }
  }
}
```

### Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `ASPNETCORE_URLS` | `http://0.0.0.0:5000` | Listen address and port |
| `DOTNET_gcServer` | `0` | Use Workstation GC (saves RAM on RPi) |
| `Indi__Host` | `localhost` | INDI server hostname |
| `Indi__Port` | `7624` | INDI server port |
| `PHD2__ExecutablePath` | (auto-detected) | Override the path to phd2.exe / phd2 binary. By default the app walks the standard install paths per OS — only set this for non-standard installs |
| `PHD2__Host` / `PHD2__Port` | `localhost` / `4400` | PHD2 event server endpoint |
| `PHD2__InstanceNumber` | `1` | PHD2 `-i N` instance number |
| `PHD2__AutoStart` | `false` | Fallback for `PHD2AutoStart` profile flag. UI checkbox in Guider tab is the normal way to set this |
| `Sequencer__TemplateDir` | `sequencer-templates` | Folder where Advanced Sequencer templates are stored (one JSON file per template) |
| `PlateSolve__PrimarySolver` | `astap` | One of `astap`, `platesolve3`, `astrometry-net-online`, `astrometry-net-local` |
| `PlateSolve__BlindSolver` | `astrometry-net-online` | Fallback when primary fails |
| `PlateSolve__UseBlindFallback` | `true` | Disable to lock to the primary only |
| `PlateSolve__AstapPath` | (auto) | ASTAP CLI path |
| `PlateSolve__PlateSolve3Path` | (none) | PlateSolve3.exe path |
| `PlateSolve__SolveFieldPath` | `/usr/bin/solve-field` | Local Astrometry.net binary |
| `PlateSolve__AstrometryApiKey` | (none) | nova.astrometry.net API key |
| `Mdns__Enabled` / `Mdns__InstanceName` | `true` / `nina-<hostname>` | mDNS announcer |
| `Relay__Enabled` | `false` | Enable reverse-tunnel client |
| `Relay__ServerUrl` | (none) | e.g. `wss://relay.example.com/_tunnel` |
| `Relay__Token` | (none) | Bearer token matching a tenant entry on the relay server |
| `Relay__ClientCertPath` | (none) | Path to a `.pfx` to present on tunnel TLS handshake (mTLS) |
| `Relay__ClientCertPassword` | (none) | Password for the `.pfx` (optional) |

Relay **server** side (different process, same `Relay__*` prefix in `appsettings.json`):

| Key | Default | Purpose |
|-----|---------|---------|
| `Relay__TenantsFile` | `tenants.json` | Path to the JSON tenant store; hot-reloaded on change. Falls back to the legacy `Tenants:` section if empty/missing |
| `Relay__UsageStateFile` | `tenant-state.json` | Persistent monthly-byte counter file |
| `Proxy__TimeoutSeconds` | `60` | Per-request timeout (long enough for plate-solving uploads) |
| `Proxy__HostnameSuffix` | (none) | e.g. `.relay.example.com` to enable subdomain routing |
| `Admin__Password` | (empty) | Password for `/_admin/*` and the `/admin/` Web UI. Empty = admin API disabled (returns 503) |
| `Audit__Enabled` | `true` | Set to false to disable the audit log |
| `Audit__Path` | `audit.log` | JSON-lines audit log path |
| `Audit__MaxFileBytes` | `52428800` | Rotate at this size (default 50 MB) |
| `Audit__RingBufferSize` | `5000` | In-memory ring for `/_admin/audit` |
| `Tls__Mode` | `off` | `off` / `pfx` / `letsencrypt` |
| `Tls__ClientCertificateMode` | `request` | `none` / `request` / `require` — Kestrel client-cert behaviour (mTLS) |
| `Tls__HttpsPort` | `443` | HTTPS bind port when TLS is enabled |
| `Tls__RedirectHttpToHttps` | `false` | 308-redirect plain HTTP to HTTPS |
| `Tls__PfxPath` / `Tls__PfxPassword` | — | Static cert when `Tls:Mode=pfx` |
| `Tls__LetsEncrypt__Domains` | — | string[] of domains for ACME issuance |
| `Tls__LetsEncrypt__EmailAddress` | — | Contact email for Let's Encrypt |
| `Tls__LetsEncrypt__UseStaging` | `false` | Use Let's Encrypt staging API while testing |

## Performance Targets

| Metric | Target | Notes |
|--------|--------|-------|
| Memory | < 500 MB | RPi 4 with 2GB RAM |
| Startup | < 5 seconds | RPi 4 |
| Image relay | ~3-10 MB/frame | LZ4 compressed, fits WiFi 5GHz |
| JPEG preview | ~200-400 KB | For mobile/weak clients |
| Frontend bundle | ~580 KB total | Alpine.js + libs, cacheable |
| Status broadcast | 1 Hz | Equipment + sequence state |

## Roadmap

### Implemented

**Core**
- [x] ASP.NET Core Minimal API with Kestrel
- [x] Full INDI protocol client (TCP/XML, async, auto-reconnect)
- [x] 9 INDI device types (Camera, Telescope, Focuser, FilterWheel, Guider, Dome, Rotator, Weather, FlatDevice)
- [x] WebSocket image streaming (JPEG + raw LZ4 dual mode, adaptive)
- [x] Live stacking with star-based alignment
- [x] Plate solving via ASTAP + Slew & Center automated workflow
- [x] Embedded DSO catalog (200+ objects)
- [x] Sequence engine with target list execution + on-disk FITS archive
- [x] Responsive Web UI with night mode + first-run location setup
- [x] Profile management (JSON persistence)
- [x] Network resilience (reconnect, dedup, backpressure, adaptive bandwidth)
- [x] systemd + Docker (linux/amd64 + linux/arm64) + Windows publish scripts
- [x] 160 unit tests

**Acquisition essentials (Phase A)**
- [x] PHD2 guider integration (TCP/JSON-RPC, RMS, dither, settle, alerts)
- [x] Auto-focus V-curve (parabola fit, backlash compensation, confirmation frame)
- [x] Meridian flip automation (LST math, full pause→flip→recenter→AF→resume loop)
- [x] Dithering between frames (configurable cadence, settle integration)
- [x] Equipment cards for Rotator / Flat Panel / Dome / Weather

**UI/UX (Phase B)**
- [x] Chart.js for guiding / focus / HFR history / temperature / histogram / altitude
- [x] Aladin Lite sky map (HiPS surveys + camera FOV overlay + click-to-pick)
- [x] OpenSeadragon full-resolution image viewer
- [x] WebGL2 shader pipeline (GPU debayer + MTF stretch, CPU fallback)
- [x] Image statistics panel + histogram endpoint
- [x] Manual stretch controls (real-time re-render via shader uniforms)
- [x] Image history thumbnail gallery
- [x] Star annotations overlay + crosshair + 3x3 grid + pixel ADU readout

**Advanced Sequencer (Phase C — complete)**
- [x] C1 Tree-based entity model (containers, instructions, conditions, triggers)
- [x] C2 30+ instructions library (mount, camera, focuser, filter, guider, dome, flat, rotator, flow control, external)
- [x] C3 6 loop conditions (Until Time / Altitude / N Exposures / Duration / Moon Sets / While Safe)
- [x] C4 8 event triggers (Auto-focus on Temp / HFR / Time / Filter, Meridian Flip, Dither, Center After Drift, Safety abort)
- [x] C5 Polymorphic JSON IO + persistent template store + execution engine + REST endpoints
- [x] C6 Tri-pane tree editor UI with palette / tree / properties + Sortable.js drag-reorder
- [x] C7 Coexistence with the legacy Simple Sequencer — both tabs always available, `preferAdvancedSequencer` profile flag toggles the default

**Nice-to-have (Phase D — almost complete)**
- [x] D1 Alpaca HTTP device support (discovery + Camera + Telescope)
- [x] D2 mDNS announcer (`nina-<hostname>.local`)
- [x] D3 Adaptive bandwidth (raw ↔ JPEG auto-switch per client)
- [x] D4 Docker image + multi-arch buildx script + compose
- [x] D6 Flat Wizard (binary-search ADU, trained exposure cache)
- [x] D7 Extended FITS headers (30+ keywords per N.I.N.A. manual spec)
- [x] D8 XISF format support (PixInsight native, LZ4-compressed)
- [x] D9 Additional plate solvers (PlateSolve3, Astrometry.net online/local) + primary+blind dispatcher
- [x] D10 Sky Atlas filters + tonight's altitude chart with twilight bands
- [x] D11 Stellarium Remote Control sync

**Extras (beyond original plan)**
- [x] First-run observatory location modal with browser geolocation
- [x] Address geocoder (OpenStreetMap/Nominatim) — search by city/address/observatory name
- [x] Auto-derived sensor dimensions from connected camera (no more manual entry)
- [x] PHD2 guide-camera / mount surfaced from `get_current_equipment`
- [x] Equipment Rigs — multi-rig profiles with one-click switching, per-rig defaults, inline editing
- [x] Full PHD2 management — profile switcher, equipment connect/disconnect, exposure, Dec mode, process launch/shutdown
- [x] PHD2 auto-detect + auto-start on boot — walks per-OS install paths, inline "Download PHD2" banner when missing, one-click checkbox to launch + connect at every Headless startup
- [x] Per-rig main + guide-scope focal length (drives FOV + FITS headers from the active rig)
- [x] Reverse-tunnel relay server for remote internet access — full HTTP **and** WebSocket forwarding (image stream + status broadcasts work end-to-end through the relay)
- [x] Relay JSON tenant store (`tenants.json`) with hot-reload + per-tenant rate limiting (request + bandwidth token-buckets, HTTP 429 on exceed)
- [x] Relay monthly byte quotas + expiring tokens (HTTP 402 on quota, auto-reset 1st UTC, persistent counter)
- [x] Relay built-in TLS — LettuceEncrypt for automatic Let's Encrypt, or static `.pfx`
- [x] Relay web admin UI at `/admin/` — full tenant CRUD, live tunnel + quota dashboard, token generator, audit-log browser with per-tenant filter
- [x] Relay per-request audit log (JSON-lines `audit.log`, auto-rotated 50 MB, outcome reasons, in-memory ring buffer + `/_admin/audit` API)
- [x] Relay mTLS for tunnel auth — per-tenant `clientCertThumbprint` pins the X.509 cert the client must present (bearer token still required)

### Planned

**Phase D — Remaining**
- [ ] D5: Mosaic planner with grid overlay
- [ ] D12: Plugin system (extension API for custom instructions / UI panels / device types)

**Relay server**
- Feature-complete for now — open a discussion if you want a new follow-up

## Support the project

If NINA Headless saves you an evening of fiddling with rigs and you want to
chip in for hosting / a coffee / dark-sky travel:

[**❤️ Donate via Stripe**](https://buy.stripe.com/9B68wPeoLcMSgOz2iJbMQ02)

Donations are entirely optional — the project stays free and open-source
either way. Bug reports and PRs are just as welcome (see below).

## Contributing

Contributions are welcome! This project follows the same coding standards as the main [N.I.N.A. repository](https://github.com/isbeorn/nina).

### Project Structure for Contributors

- **Endpoints** are in `src/NINA.Headless/Endpoints/` — each is an extension method on `WebApplication`
- **Services** are in `src/NINA.Headless/Services/` — registered as singletons in `Program.cs`
- **INDI devices** follow a consistent pattern in `src/NINA.INDI/Devices/`
- **Frontend** is plain HTML/JS/CSS in `src/NINA.Headless/wwwroot/` — no build step required
- **Tests** go in `tests/NINA.Headless.Test/` using NUnit

## License

This project is part of the N.I.N.A. ecosystem. See the main [N.I.N.A. repository](https://github.com/isbeorn/nina) for license details.
