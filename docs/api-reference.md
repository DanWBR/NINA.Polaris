# Polaris Astro Controller: API & Configuration Reference

REST endpoints, WebSocket streams, `appsettings.json`, and environment
variables. The Web UI is built entirely on these endpoints, so anything the UI
does is scriptable. For a feature overview see [FEATURES.md](FEATURES.md); for
how-to guides see the [user guide](user-guide/README.md).

---

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

### INDI control panel (property browser)

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/indi/properties?device=` | Full device → group → property tree (optionally filtered to one device) |
| POST | `/api/indi/properties/set` | Set a property `{ device, property, type, numbers/switches/texts }` |
| POST | `/api/indi/properties/refresh` | Wipe the device cache and re-issue getProperties |
| POST | `/api/indi/properties/config/{save\|load\|default}?device=` | Drive the driver's CONFIG_PROCESS |
| GET | `/api/indi/properties/notes` | Operator's saved help notes (keyed by property name) |
| POST | `/api/indi/properties/note` | Set or clear a note `{ property, text }` (empty text clears) |

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
| GET | `/api/guider/install-info` | Detected install (`installed`, `resolvedPath`, `downloadUrl`, `os`, `searchedPaths`), UI uses this to surface "Download PHD2" when missing |
| POST | `/api/guider/auto-start/{true\|false}` | Persist auto-start-on-boot preference in the user profile |
| POST | `/api/guider/profile/sync` | Sync a rig (default: active rig) to its matching PHD2 profile + apply preset. Body: `{ rigId? }` |
| GET | `/api/guider/profile/sync/status` | Last sync phase / error / profileMissing flag |
| POST | `/api/guider/calibrate/smart` | Start smart calibration job. Body: `SmartCalibrateOptions` (slewToEquator, exposureMsOverride, calibrationStepMsOverride, timeoutSeconds). Returns `{ jobId }` |
| GET | `/api/guider/calibrate/smart/{jobId}` | Poll calibration state (phase + stepMs + pixelScale + calibration + warnings) |
| POST | `/api/guider/calibrate/smart/{jobId}/abort` | Abort running calibration |
| GET | `/api/guider/algo-presets` | Curated algorithm presets (Default / Reactive / Smooth) with the (axis, name, value) triples each applies |
| POST | `/api/guider/algo-preset/{name}` | Apply preset live + persist on the active rig |
| GET | `/api/guider/algo-params` | Live values: per axis, every param `get_algo_param_names` reports |
| PUT | `/api/guider/algo-params` | Set a single live knob `{ axis, name, value }` + flip preset to "Custom" |
| GET | `/api/guider/gui-session/status` | xpra-hosted PHD2 GUI lifecycle (xpra installed? version? session running? bind port) |
| POST | `/api/guider/gui-session/{start,stop,restart}` | Manage the embedded PHD2 GUI session (Linux only; 501 elsewhere) |
| ALL | `/phd2-gui/{**}` | Reverse-proxy to xpra HTML5 client (HTTP + WebSocket). Same-origin so iframe sessionStorage works |

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

### Weather Forecast

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/weather/forecast?lat=&lon=` | 7Timer ASTRO 3-day forecast in 3 h slots with computed `observationScore` (0-100) per slot. Server-cached 15 min |

### Tonight's Best

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/sky/tonights-best?lat=&lon=&limit=` | Ranked list of DSOs / Moon / planets / comets observable during tonight's window |
| GET | `/api/sky/image?name=` | Resolve thumbnail URL for a celestial object (NASA Image Library → Wikipedia fallback, disk-cached 30 days) |
| POST | `/api/sky/image/prefetch` | Walk the full DSO catalog + Moon + planets + comets and pull all thumbnails to disk for offline use |

### STUDIO, Post-Processing

Frame browser, master integration, calibration, batch stacking, debayer,
background extraction, noise reduction, sharpening, and multi-format
export.

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/studio/rescan` | Walk `ImageOutputDir` recursively, header-only FITS scan, upsert SQLite index |
| GET | `/api/studio/rescan/status` | Rescan progress |
| GET | `/api/studio/frames?type=&filter=&target=&dateFrom=&dateTo=&limit=&offset=` | Paginated frame list |
| GET | `/api/studio/frames/{id}` | Full row + FITS keyword dump |
| GET | `/api/studio/frames/{id}/thumb` | Auto-stretched 256 px JPEG thumbnail (cached on disk) |
| GET | `/api/studio/stats` | Aggregate: total lights, total exposure (h), distinct targets / filters |
| GET | `/api/studio/frames/{id}/preview?black=&mid=&white=&max=&format=jpg\|png` | Stretched preview (debounced slider re-renders hit this) |
| GET | `/api/studio/frames/{id}/autostretch` | Auto-stretch black/mid/white triple to seed UI sliders |
| GET | `/api/studio/frames/{id}/stats?stars=` | Full ImageStatistics + StarDetector output + histogram |
| POST | `/api/studio/frames/{id}/export?format=tif\|png\|jpg&stretched=&black=&mid=&white=` | Export to `{rig}/processed/{target}/` |
| POST | `/api/studio/masters` | Start master-frame integration `{ frameIds, type: Bias\|Dark\|Flat\|DarkFlat, method: Mean\|Median\|SigmaClippedMean }` → `{ jobId }` |
| GET | `/api/studio/masters/{jobId}/status` | Master-integration progress |
| POST | `/api/studio/calibrate` | Calibrate lights `{ lightIds, masterDarkId?, masterFlatId?, masterBiasId? }` (null = auto-match per light) → `{ jobId }` |
| GET | `/api/studio/calibrate/{jobId}/status` | Calibration progress with succeeded / failed counts |
| POST | `/api/studio/integrate` | Batch stack `{ frameIds, method }` (align + integrate) → `{ jobId }` |
| GET | `/api/studio/integrate/{jobId}/status` | Stack progress with combined / dropped / total exposure |
| POST | `/api/studio/frames/{id}/debayer` | Bilinear demosaic → luminance FITS in `{rig}/processed/{target}/` |
| POST | `/api/studio/frames/{id}/bgextract?samplesX=&samplesY=&polyDegree=` | Subtract polynomial gradient |
| POST | `/api/studio/frames/{id}/nr?radius=` | Gaussian noise reduction |
| POST | `/api/studio/frames/{id}/sharpen?amount=&radius=&threshold=` | Unsharp mask sharpening |

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
| POST | `/api/sequencer/document/json` | Accept raw JSON body, "load sequence from file" |
| POST | `/api/sequencer/start` | Validate + run the tree in the background |
| POST | `/api/sequencer/stop` | Cancel the run via the engine's CTS |
| POST | `/api/sequencer/validate` | Walk Validate() across the tree, return errors |
| GET | `/api/sequencer/types` | Palette listing, every known `(type, category, kind)` |
| GET | `/api/sequencer/templates` | List saved templates + their store dir |
| GET | `/api/sequencer/templates/{name}` | Load a named template |
| POST | `/api/sequencer/templates/{name}` | Save a `SequenceDocument` as a named template |
| DELETE | `/api/sequencer/templates/{name}` | Delete a template |

### Mosaic Planner

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/mosaic/plan` | Compute panels + time estimate from `MosaicRequest` (for the UI overlay preview) |
| POST | `/api/mosaic/to-sequence` | Build the plan + lower to a `SequenceDocument`; optionally load into the engine via `loadIntoEngine=true` |

### Plugins

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/plugins` | List loaded plugins with name / version / author / discriminators they contributed |

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
| POST | `/api/system/factory-reset` | Wipe all profiles / rigs / auth / settings back to first-run (keeps captured images) |

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
| `PHD2__ExecutablePath` | (auto-detected) | Override the path to phd2.exe / phd2 binary. By default the app walks the standard install paths per OS, only set this for non-standard installs |
| `PHD2__Host` / `PHD2__Port` | `localhost` / `4400` | PHD2 event server endpoint |
| `PHD2__InstanceNumber` | `1` | PHD2 `-i N` instance number |
| `PHD2__AutoStart` | `false` | Fallback for `PHD2AutoStart` profile flag. UI checkbox in Guider tab is the normal way to set this |
| `Sequencer__TemplateDir` | `sequencer-templates` | Folder where Advanced Sequencer templates are stored (one JSON file per template) |
| `Plugins__Enabled` | `true` | Set to false to skip the plugin scan entirely |
| `Plugins__Directory` | `plugins` | Folder scanned at startup for plugin `.dll` files |
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
| `Tls__ClientCertificateMode` | `request` | `none` / `request` / `require`, Kestrel client-cert behaviour (mTLS) |
| `Tls__HttpsPort` | `443` | HTTPS bind port when TLS is enabled |
| `Tls__RedirectHttpToHttps` | `false` | 308-redirect plain HTTP to HTTPS |
| `Tls__PfxPath` / `Tls__PfxPassword` |, | Static cert when `Tls:Mode=pfx` |
| `Tls__LetsEncrypt__Domains` |, | string[] of domains for ACME issuance |
| `Tls__LetsEncrypt__EmailAddress` |, | Contact email for Let's Encrypt |
| `Tls__LetsEncrypt__UseStaging` | `false` | Use Let's Encrypt staging API while testing |

