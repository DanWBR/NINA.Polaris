# NINA.Polaris — Architecture

This is the ASP.NET Core host. It's where ~90% of the business logic
lives. Everything else in the solution (NINA.INDI, NINA.Image.Portable,
the camera SDK wrappers, NINA.Relay.Server) is a library this project
consumes.

For the big picture (cross-project diagram, request flows), see the
[root ARCHITECTURE.md](../../ARCHITECTURE.md). This file zooms into
**how NINA.Polaris itself is organized**.

## Project layout

```
src/NINA.Polaris/
  Program.cs                 # composition root + middleware pipeline
  appsettings.json           # config (INDI host/port, image dir, PHD2, Relay)
  Endpoints/                 # REST surface — one file per resource group
  WebSocket/                 # /ws/status + /ws/image-stream handlers
  Services/                  # all business logic; mostly DI singletons
    Alpaca/                  # ASCOM Alpaca HTTP client + device wrappers
    External/                # Siril, GraXpert subprocess wrappers
    Planetary/               # SER writer, video recorder, lucky-imaging stacker
    PlateSolving/            # IPlateSolver implementations (ASTAP, PS3, AN, ...)
    Plugins/                 # MEF plugin loader
    Sequencer/               # Advanced sequencer entities (containers,
                             # instructions, conditions, triggers)
    Studio/                  # Frame library, master/calibration/integration jobs
  Resources/SirilScripts/    # bundled .ssf scripts copied to output
  sequencer-templates/       # JSON template files for the ADV palette
  wwwroot/                   # Alpine.js frontend (no build pipeline)
    index.html               # the whole UI tree
    js/app.js                # the Alpine component + all methods
    js/lib/                  # vendored third-party JS (Chart.js, Aladin, etc.)
    css/app.css              # styling
```

## Composition root (Program.cs)

`Program.cs` is the single source of truth for **what runs at startup**.
Roughly in this order:

1. `WebApplication.CreateBuilder(args)` — standard ASP.NET Core bootstrap
2. **Service registration**:
   - Foundation singletons (`ProfileService`, `EquipmentManager`,
     `SkyCatalogService`, ...)
   - Imaging chain (`ImageWriterService`, `ImageRelayService`)
   - PHD2 stack (`PHD2Client`, `PHD2ProcessManager`, `PHD2AutoStartService`,
     `PHD2ProfileSyncService`, `PHD2CalibrationOrchestrator`,
     `Phd2GuiSessionService`)
   - Plate solving (every `IPlateSolver` + the `PlateSolveService` dispatcher)
   - Long-running orchestrators (`AutoFocusService`, `MeridianFlipService`,
     `SequenceEngine`, `LiveStackingService`, `LiveStackTriggersService`,
     `SlewPreviewService`, `MosaicPlannerService`)
   - Planetary (`VideoRecordingService`, `PlanetaryStackerService`)
   - External (`SirilService`, `GraXpertService`)
   - File browser, frame library (SQLite-backed), studio job services
   - Hosted services (`MdnsService`, `HostMetricsService`,
     `RelayClient`, `PHD2AutoStartService`, ...)
3. **YARP reverse proxy** registration (for `/phd2-gui/*` → xpra)
4. Build the app + middleware pipeline (CORS → static files → WS upgrade
   → endpoints)
5. Map every endpoint group (`app.MapCameraEndpoints();
   app.MapTelescopeEndpoints(); ...`)
6. Map the two WebSocket paths (`/ws/status`, `/ws/image-stream`)
7. **Eager-resolve** the orchestrators so their constructors run + they
   subscribe to events before any HTTP request arrives. This is how
   `LiveStackTriggersService` gets wired to `LiveStackingService.Subscribe...`
   without an explicit call site.
8. `app.Run("http://0.0.0.0:5000")`

If a service needs to react to events from another service from the
moment the server starts (not just on first request), it **must** be
eager-resolved at the end of Program.cs.

## The Endpoints/ pattern

Every file in `Endpoints/` follows the same shape:

```csharp
public static class FooEndpoints {
    public static void MapFooEndpoints(this WebApplication app) {
        var g = app.MapGroup("/api/foo");

        g.MapGet("/status", (FooService f) => Results.Ok(new {
            connected = f.IsConnected,
            value = f.CurrentValue
        }));

        g.MapPost("/connect", async (FooService f) => {
            await f.ConnectAsync();
            return Results.NoContent();
        });
    }
}
```

Rules of thumb:

- One file per logical resource. `CameraEndpoints` owns `/api/camera/*`,
  `TelescopeEndpoints` owns `/api/telescope/*`, etc.
- The handler is a single expression or short method. **No business
  logic in the endpoint file** — push it into the service.
- DTOs are inline `record`s defined at the top of the file.
- Errors that map to HTTP shape (404, 409, 400 with message) use
  `Results.NotFound(...)` / `Results.Conflict(...)` etc. Domain
  exceptions bubble up and the global exception middleware turns them
  into 500 with `{ error: "..." }`.
- Long-running operations return 202 + a `{ jobId }` and the caller
  polls a `/jobs/{id}/status` endpoint **or** subscribes to
  `/ws/status` (where the job appears under its own sub-object).

## The Services/ pattern

Almost everything in `Services/` is a **singleton** registered in
`Program.cs`. Pattern:

```csharp
public class FooService {
    private readonly ILogger<FooService> _logger;
    private readonly IProfileService _profiles;
    private readonly EquipmentManager _equip;

    public FooService(ILogger<FooService> logger,
                      IProfileService profiles,
                      EquipmentManager equip) {
        _logger = logger;
        _profiles = profiles;
        _equip = equip;
    }

    // Public reactive state surface
    public FooStatus CurrentStatus { get; private set; } = new();
    public event Action<FooStatus>? StatusChanged;

    public async Task DoSomethingAsync(CancellationToken ct = default) {
        // implementation
        Update(new FooStatus { ... });
    }

    private void Update(FooStatus s) {
        CurrentStatus = s;
        StatusChanged?.Invoke(s);
    }
}
```

Conventions:

- **Status snapshots are records** — immutable, replaced wholesale on
  change. The UI never mutates them.
- **`StatusChanged` event** fires once per update. `StatusStreamHandler`
  doesn't subscribe per service — it polls `.CurrentStatus` of each
  injected service every 1Hz and broadcasts the merged payload.
- **Async everything**. Sync helpers are an anti-pattern in this project.
- **`ILogger<T>` is structured** — `_logger.LogInformation("Captured
  {Count} frames in {Elapsed}ms", n, ms)`. Never `string.Format`.
- **CancellationToken default(default)** on every IO method, so callers
  can opt out by passing nothing.

### Long-running job services

Several services run jobs that take seconds-to-minutes:
`AutoFocusService`, `MeridianFlipService`, `SlewCenterService`,
`PHD2CalibrationOrchestrator`, `PlanetaryStackerService`, the Studio
batch services, etc.

They share a pattern:

1. `StartJob(...)` returns a `Job` record with a `JobId` (guid).
2. Job state lives in a `ConcurrentDictionary<string, Job>` on the service.
3. State machine phases are exposed as a `string` field on the job
   (e.g. `"preflight" | "running" | "ok" | "fail"`).
4. The current active job appears on the service's `CurrentJob`
   property and gets included in the `/ws/status` payload by
   `StatusStreamHandler`.
5. `AbortJob(jobId)` flips a CTS that the running task observes.

When you add a new long-running job, copy this shape — the UI already
knows how to render phase + progress for any job that follows it.

## The WebSocket/ handlers

Two endpoints, two very different cadences:

### `/ws/status` (StatusStreamHandler)

- Broadcasts the full app state ~1Hz
- Reads from many service `.CurrentStatus` properties + composes one
  JSON payload
- Payload shape is the contract with the frontend; adding a new service
  status means adding a new sub-object here
- All connected browsers get the same payload (no per-client filtering)
- Cheap (~1-5 KB per tick when idle, ~20-50 KB during heavy activity)

### `/ws/image-stream` (ImageStreamHandler)

- Triggered by `ImageRelayService.RelayImageAsync(IImageData)`
- Sends each frame as **either** JPEG bytes (default) **or** raw
  uint16 + LZ4-compressed bayer pattern (when browser sets
  `?mode=raw`)
- Used by LIVE / PREVIEW / VIDEO tabs for the canvas blit
- Adaptive bandwidth: if the WS send queue backs up, the next frame is
  dropped (UI sees a tick missing rather than the whole stream stalling)

## Frontend (wwwroot/)

No build pipeline. The HTML is the HTML, the JS is the JS.

- `index.html` — the entire DOM tree. Tabs are `<div x-show="tab ===
  'foo'">` panels.
- `js/app.js` — a single Alpine.js component object. State at the top,
  methods underneath. Methods are grouped loosely by feature (rigs,
  sky, preview, autorun, ...).
- `js/lib/` — vendored third-party libraries (Chart.js, Aladin Lite,
  OpenSeadragon, suncalc, sortable.js, ...). Each has an adjacent
  `LICENSE` file with attribution.
- `css/app.css` — single stylesheet, BEM-ish class naming
  (`.equip-card`, `.equip-card-header`, ...). No SASS / Tailwind.

The WebSocket payload is absorbed in `handleStatusMessage(msg)` — a
giant switch that dispatches each top-level key (`equipment`,
`guider`, `liveStack`, ...) to its slot in component state.

## Profile model

A "rig" = an `EquipmentProfile` record persisted in JSON at
`{AppData}/Polaris/profile.json`. Each rig carries every per-setup
choice (which camera, which mount, focal length, PHD2 algo preset,
live stack trigger config, ...). Profile switch fires
`EquipmentProfileActivated` and many services subscribe to reconfigure
themselves.

If you're adding a feature that needs per-rig config:

1. Add the field to `EquipmentProfile`
2. Add it to the `PUT /api/equipment/rigs/{id}` endpoint accepted body
3. Subscribe to `EquipmentProfileActivated` in your service constructor
   to reload when the user switches rigs
4. Default to a safe value for backward-compat (older `profile.json`
   files won't have the field)

## Cross-cutting concerns

- **Logging**: `ILogger<T>` injected, structured templates, default
  level `Information`. Set `Logging:LogLevel:Default=Debug` in
  `appsettings.json` for verbose.
- **Configuration**: `appsettings.json` + `appsettings.Development.json`
  + environment variables override. Sections: `Indi`, `PHD2`, `Relay`,
  `Kestrel`, `Logging`.
- **Cancellation**: every IO surface accepts a `CancellationToken`.
  The HTTP request's `RequestAborted` propagates into long-running
  endpoint handlers.
- **Threading**: services are singletons + thread-safe by construction.
  `ConcurrentDictionary` for collections of jobs; `Interlocked` /
  `lock` for primitives where needed; immutable record swap for state
  snapshots.

## See also

- [Root ARCHITECTURE.md](../../ARCHITECTURE.md) — system overview,
  cross-project Mermaid, request flows
- [CONTRIBUTING.md](../../CONTRIBUTING.md) — how-to patterns (new INDI
  device, sidebar tab, sequencer item, plate solver)
- Sibling per-project ARCHITECTURE.md files in `src/NINA.INDI/`,
  `src/NINA.Image.Portable/`, ...
