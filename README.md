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

ASTAP-based plate solving with automated centering workflow:

- **Slew & Center** — Automated loop: slew to target → capture → solve → compute error → sync → re-slew (converges in 2-3 iterations, ~30-60s total)
- Configurable tolerance (default: 30 arcsec)
- Async job tracking with real-time status polling
- Supports search radius, FOV, and downsample configuration

### Sky Catalog & Object Search

Embedded deep sky catalog with 200+ objects:

- All 110 Messier objects
- Popular Caldwell objects
- Notable NGC/IC targets
- Fuzzy search by designation, common name, or alias (e.g., "M31", "Andromeda", "NGC 224")
- Object metadata: coordinates (J2000), magnitude, type, size, common names

### Sequence Engine

Target list execution with automated imaging:

- Multi-target sequences with per-target exposure, gain, binning, filter, and frame count
- Automatic slew-to-target before capture
- Pause/resume with SemaphoreSlim gate
- Real-time progress tracking via WebSocket (1Hz status broadcast)
- Per-item status: completed frames, active state

### Web UI

Responsive, dark-themed interface inspired by ASIAIR:

- **Live View** — Real-time camera preview with crosshair overlay, exposure/gain/binning/filter controls, capture/loop/stop, live stack toggle, session stats, image history
- **Mount Control** — NSEW directional pad, tracking toggle, park/unpark, GoTo via Sky Explorer
- **Focus** — Step-based focuser control with in/out buttons, adjustable step size, temperature readout
- **Guider** — Guiding status display (placeholder for PHD2 integration)
- **Sky Explorer** — Object search, target info panel with coordinates/magnitude/aliases, Slew & Center with progress, Add to Sequence
- **Sequence** — Target list editor, inline editing, progress bars, start/pause/resume/stop
- **Settings** — INDI connection, device selection, observatory location, sensor/optics config, profile management

**Night Mode** — Red-on-black theme that preserves dark adaptation (critical for field use).

**Mobile Responsive** — Full functionality on phones and tablets with bottom tab navigation.

### Profile Management

JSON-based settings persistence with multi-profile support:

- Observatory location, sensor dimensions, focal length
- Default imaging parameters (exposure, gain, binning)
- INDI connection settings
- Plate solver configuration
- Image output directory and naming patterns
- Save, load, and rename profiles

### Network Resilience

Built for unreliable field WiFi:

- WebSocket auto-reconnect with exponential backoff + jitter
- Request deduplication for in-flight API calls
- Server reachability detection with automatic recovery
- Toast notifications for connection state changes
- 15-second request timeout with abort controller

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
├── tests/
│   └── NINA.Headless.Test/         ← 64 unit tests (NUnit)
│
└── deploy/                         ← Deployment scripts
    ├── nina-headless.service        ← systemd unit file
    ├── install.sh                   ← Linux installer
    ├── publish-linux-arm64.sh       ← RPi build script
    └── publish-win-x64.ps1         ← Windows build script
```

### Technology Stack

| Layer | Technology | Purpose |
|-------|-----------|---------|
| Web server | Kestrel (standalone) | Native .NET, no nginx/IIS needed |
| API | ASP.NET Core Minimal API | Low overhead, AOT-friendly |
| Real-time (images) | WebSocket (binary) | JPEG or LZ4-compressed raw frames |
| Real-time (status) | WebSocket (JSON) | Equipment + sequence status at 1Hz |
| Frontend framework | Alpine.js v3 | Reactive UI (~15KB, no build step) |
| Image encoding | SixLabors.ImageSharp | Cross-platform JPEG encoding |
| Compression | K4os.Compression.LZ4 | Fast image compression (~2GB/s) |
| Equipment drivers | INDI protocol (TCP/XML) | 400+ Linux astronomy drivers |
| Plate solving | ASTAP (external process) | Offline astrometric solver |
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

### Docker (optional)

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY publish/linux-arm64/ .
EXPOSE 5000
ENTRYPOINT ["./NINA.Headless"]
```

## API Reference

### Equipment

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/equipment/devices` | List all discovered INDI devices |
| POST | `/api/equipment/connect` | Connect to all selected devices |
| POST | `/api/equipment/disconnect` | Disconnect all devices |

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
| GET | `/api/image/latest/stats` | Image dimensions and metadata |

### Live Stacking

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/livestack/start` | Start live stacking |
| POST | `/api/livestack/stop` | Stop live stacking |
| POST | `/api/livestack/reset` | Reset stack buffer |
| GET | `/api/livestack/status` | Stack frame count and state |

### Sequence

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/sequence` | Current sequence items and state |
| POST | `/api/sequence` | Load sequence `[{ name, exposure, gain, ... }]` |
| POST | `/api/sequence/start` | Start execution |
| POST | `/api/sequence/pause` | Pause execution |
| POST | `/api/sequence/resume` | Resume from pause |
| POST | `/api/sequence/stop` | Stop execution |
| GET | `/api/sequence/status` | Detailed progress |

### Sky & Plate Solving

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/sky/catalog/search?query=M31` | Search embedded DSO catalog |
| GET | `/api/sky/catalog/{name}` | Get object by exact name |
| GET | `/api/sky/fov` | Current FOV based on optics config |
| GET | `/api/sky/solver/status` | Plate solver availability |
| POST | `/api/sky/slew-and-center` | Start slew & center job `{ ra, dec, toleranceArcsec }` |
| GET | `/api/sky/slew-and-center/{id}/status` | Job progress |
| POST | `/api/sky/slew-and-center/{id}/cancel` | Cancel job |

### System

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/system/status` | System info (CPU, RAM, uptime) |
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

- [x] ASP.NET Core Minimal API with Kestrel
- [x] Full INDI protocol client (TCP/XML, async, auto-reconnect)
- [x] 9 INDI device types (Camera, Telescope, Focuser, FilterWheel, Guider, Dome, Rotator, Weather, FlatDevice)
- [x] WebSocket image streaming (JPEG + raw LZ4 dual mode)
- [x] Client-side canvas rendering with auto-stretch
- [x] Live stacking with star-based alignment
- [x] Plate solving via ASTAP
- [x] Slew & Center automated workflow
- [x] Embedded DSO catalog (200+ objects)
- [x] Sequence engine with target list execution
- [x] Responsive Web UI with night mode
- [x] Profile management (JSON persistence)
- [x] Network resilience (reconnect, dedup, backpressure)
- [x] BLOB receiver with file-based streaming
- [x] Connection manager with device type inference
- [x] systemd service + deploy scripts (Linux ARM64, Windows x64)
- [x] 64 unit tests

### Planned

- [ ] Aladin Lite sky map integration (WebGL sky viewer)
- [ ] OpenSeadragon full-resolution zoom viewer
- [ ] WebGL shader pipeline (debayer, stretch, histogram)
- [ ] Chart.js graphs (guiding error, focus curve, temperature)
- [ ] PHD2 guider integration (TCP client)
- [ ] Auto-focus V-curve routine
- [ ] Mosaic planner with grid overlay
- [ ] Alpaca HTTP device support
- [ ] mDNS/Avahi discovery (`nina.local`)
- [ ] Adaptive bandwidth (raw vs JPEG auto-switch)
- [ ] Docker image for easy deployment
- [ ] Plugin system

## Contributing

Contributions are welcome! This project follows the same coding standards as the main [N.I.N.A. repository](https://bitbucket.org/Isbeorn/nina/).

### Project Structure for Contributors

- **Endpoints** are in `src/NINA.Headless/Endpoints/` — each is an extension method on `WebApplication`
- **Services** are in `src/NINA.Headless/Services/` — registered as singletons in `Program.cs`
- **INDI devices** follow a consistent pattern in `src/NINA.INDI/Devices/`
- **Frontend** is plain HTML/JS/CSS in `src/NINA.Headless/wwwroot/` — no build step required
- **Tests** go in `tests/NINA.Headless.Test/` using NUnit

## License

This project is part of the N.I.N.A. ecosystem. See the main [N.I.N.A. repository](https://bitbucket.org/Isbeorn/nina/) for license details.
