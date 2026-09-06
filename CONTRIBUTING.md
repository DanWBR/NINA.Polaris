# Contributing to Polaris Astro Controller

This file is for **developers** who want to fix a bug, add a feature,
or just understand how Polaris is put together well enough to read
the source. End-user docs live in [docs/user-guide/](docs/user-guide/).

## The name, and why the code says NINA

The product is **Polaris**, in full **Polaris Astro Controller**. That
is the name in the interface, the documentation, the website, the
release notes and anything else a user reads.

The source tree says `NINA.*` everywhere because Polaris is a fork of
[N.I.N.A.](https://nighttime-imaging.eu/) (MPL-2.0) and keeps upstream's
assembly and namespace names on purpose: it is what makes upstream code
readable side by side with ours and portable into the fork. So
`NINA.Polaris`, `NINA.INDI`, `NINA.Core.Portable` and their siblings are
correct and stay as they are. Do not rename them, and do not rename the
folders either.

The rule is simply which side of the line you are on:

- code, namespaces, assemblies, project files, repository paths -> `NINA.*`
- anything a user sees or reads -> Polaris

Polaris is **not** affiliated with or supported by the official N.I.N.A.
team. Support questions belong in this repository, never in theirs.

## TL;DR

```bash
git clone https://github.com/DanWBR/NINA.Polaris.git
cd NINA.Polaris
dotnet build src/NINA.Polaris/NINA.Polaris.csproj
dotnet test tests/NINA.Polaris.Test/NINA.Polaris.Test.csproj
dotnet run --project src/NINA.Polaris/NINA.Polaris.csproj
```

Server listens on `http://localhost:5000`. Edit + rebuild + refresh
the browser to see changes (static assets in `wwwroot/` reload without
rebuild).

## Stack

- **.NET 10** (latest STS)
- **ASP.NET Core minimal APIs** + WebSocket handlers
- **Alpine.js 3** for the frontend reactivity (no build pipeline)
- **NUnit** + plain xUnit-style tests in `tests/NINA.Polaris.Test`
- **SQLite** via `Microsoft.Data.Sqlite` for the STUDIO frame index
- **YARP** for the `/phd2-gui/` reverse proxy
- **SkiaSharp** for image encoding (JPEG/PNG/TIFF)
- **Microsoft.Extensions.Diagnostics.ResourceMonitoring** for host
  CPU/RAM gauges

## Project layout

```
src/
  NINA.Core.Portable/        # shared math / enums / utilities (no UI / IO)
  NINA.Image.Portable/       # FITS reader/writer, XISF writer, star
                             # detection, image stretch, BaseImageData
                             #, pure code, no host deps
  NINA.INDI/                 # INDI TCP/XML protocol + device wrappers
                             # (IndiCamera, IndiTelescope, ...)
  NINA.Camera.CanonEdsdk/    # Windows-only Canon EDSDK driver wrapper
  NINA.Camera.NikonSdk/      # ditto Nikon
  NINA.Camera.SonySdk/       # ditto Sony
  NINA.Mount.SynScanWifi/    # direct-WiFi SynScan driver
  NINA.Relay.Protocol/       # shared types for relay (tenant, audit)
  NINA.Relay.Server/         # standalone VPS-deployed relay server
  NINA.Polaris/             # the ASP.NET Core host, Services/,
                             # Endpoints/, WebSocket/, wwwroot/
tests/
  NINA.Polaris.Test/        # all the unit tests
docs/                        # user + dev docs
  user-guide/                # end-user (rendered to GitHub Pages later)
  *.md                       # per-feature install/setup docs
```

## How to add a new INDI device

Concrete walkthrough, add a hypothetical "weather safety monitor":

1. **Define the protocol surface** in `NINA.INDI/Devices/IndiSafety.cs`:
   ```csharp
   public class IndiSafety {
       private readonly IndiClient _client;
       public string DeviceName { get; }
       public bool IsSafe => _client.GetSwitch(DeviceName, "SAFETY_STATUS", "SAFE");
       public IndiSafety(IndiClient client, string name) { ... }
       public Task ConnectAsync(CancellationToken ct = default) => ...
   }
   ```
2. **Wire it into `EquipmentManager`** (`Services/EquipmentManager.cs`):
   ```csharp
   public IndiSafety? Safety { get; private set; }

   public bool SelectSafety(string deviceName) { ... }
   ```
3. **Add the endpoint** in `Endpoints/SafetyEndpoints.cs`:
   ```csharp
   public static void MapSafetyEndpoints(this WebApplication app) {
       var g = app.MapGroup("/api/safety");
       g.MapGet("/status", (EquipmentManager e) => Results.Ok(new {
           connected = e.Safety?.IsConnected == true,
           isSafe = e.Safety?.IsSafe ?? false
       }));
       ...
   }
   ```
4. **Register the endpoint** in `Program.cs`:
   ```csharp
   app.MapSafetyEndpoints();
   ```
5. **Add a card to the RIGS tab** in `wwwroot/index.html` following
   the existing pattern (icon header + dropdown + connect/disconnect
   buttons + status dot)
6. **Add JS state + methods** in `wwwroot/js/app.js` mirroring the
   existing equipment cards
7. **Add tests** in `tests/NINA.Polaris.Test/IndiSafetyTests.cs`

Reference: the `IndiWeather` device + `WeatherEndpoints` show the
full pattern.

## How to add a new sidebar tab

1. **HTML**: add `<button class="nav-btn">` to the sidebar (`index.html`
   ~line 100) + `<div x-show="tab === 'newtab'" class="tab-panel">`
   for the panel
2. **JS**: any state goes in the Alpine component object at the top
   of `app.js`. WebSocket payload absorption goes in the
   `handleStatusMessage` switch.
3. **CSS**: per-tab styling in `wwwroot/css/app.css`; mirror the
   pattern of existing tabs.
4. **Add a link** to the user-guide README ([docs/user-guide/README.md](docs/user-guide/README.md))
   so users can find it.

Reference: the VIDEO tab (commits VIDPL-5+8+10) is the most recent
complete add.

## How to add a sequencer instruction / trigger / condition

The Advanced Sequencer uses MEF-discovered exports in
`src/NINA.Polaris/Services/Sequencer/`.

1. Create a class implementing the appropriate base (`SequenceInstruction`,
   `SequenceTrigger`, `SequenceCondition`)
2. Annotate with `[Export]` + metadata attributes for category /
   display name
3. Add the type to `SequencerFactory` so it appears in the palette
4. JSON serialization via `SequenceJsonConverter` should pick it up
   from the type metadata automatically
5. Add a test

Reference: `Services/Sequencer/Triggers/AutoFocusOnTemperatureTrigger.cs`
for a complete trigger; `Services/Sequencer/Conditions/LoopUntilAltitudeCondition.cs`
for a complete condition.

## How to add a plate solver

Polaris uses the `IPlateSolver` strategy pattern.

1. Create a class implementing `IPlateSolver` in
   `Services/PlateSolving/YourSolver.cs`
2. `Task<PlateSolveResult> SolveAsync(string fitsPath,
   PlateSolveOptions options, CancellationToken ct)` is the only
   required method
3. Register in `Program.cs`:
   `builder.Services.AddSingleton<NINA.Polaris.Services.PlateSolving.YourSolver>();`
4. `PlateSolveService` (the dispatcher) auto-discovers + adds to the
   solver list
5. Settings → Plate solver → Primary / Blind dropdowns surface it

Reference: `Services/PlateSolving/AstapSolver.cs` and `AstrometryNetLocalSolver.cs`.

## Scope and design

Polaris is opinionated on purpose. That is what keeps forty-odd panels
looking like one program instead of forty. Two rules protect it:

- **Propose a feature before you build it.** Open a discussion or an
  issue saying what you want and why. A pull request is a perfectly good
  way to show an idea, but a large one that arrives unannounced is
  likely to get feedback on direction rather than on code, and that
  wastes your evening more than ours.
- **Match what is already there.** New interface work reuses the
  existing design system instead of introducing a second one. If the
  pattern you need does not exist yet, say so in the proposal and we
  will design it once, in one place.

Neither rule is about trust. They exist so that nobody builds something
good and then hears no.

### Interface rules

These are checked in review every time, so they are cheaper to follow
than to retrofit:

- Every new card, modal and panel gets both horizontal and vertical
  padding. Mirror the sibling sections rather than inventing spacing.
- Label above the field, field full width. Grids stay single column
  unless the content genuinely pairs.
- Every user-visible string is translated in the **same** change, in all
  five files under `src/NINA.Polaris/wwwroot/data/locales/`
  (`_source`, `pt-BR`, `es`, `fr`, `de`). The English text is the key.
  A half-translated dialog is worse than an untranslated one.
- No em dashes or en dashes in user-facing text, in the keys or in the
  translations. A comma, a colon or a full stop reads better and
  survives every font on every board.
- Do not put a `max-width` on dropdowns.
- When you touch `wwwroot/js/app.js`, run
  `node scripts/check-missing-methods.mjs`. A `this.something()` with no
  definition is silent until a user clicks the button.

## Coding conventions

- **C#**: stick to the existing style. Records for DTOs, classes for
  services, async/await everywhere. `?.` + null-conditional reads from
  service property surfaces (services may be null at startup before
  EquipmentManager hooks up).
- **Comments**: explain **why**, not what. Code reviewers will reject
  comments that restate the next line.
- **XML doc-comments**: required on public types + public methods of
  services. Short paragraph minimum, mention which other service
  consumes it.
- **Logging**: `ILogger<T>` injected, structured (`LogInformation("X
  finished {Frames} frames", count)`), never string.Format / concat
  the message.
- **Async**: every IO surface is async. Sync wrappers are anti-pattern.
- **JS**: no build pipeline. Alpine.js + vanilla. Keep methods small +
  composable on the Alpine component.
- **CSS**: BEM-ish naming (`.equip-card`, `.equip-card-header`).
  No SASS / Tailwind. Keep close to the markup.

## Commits

Format: `<scope>: <imperative summary>` followed by a blank line + a
short body explaining **why**. Example:

```
LSTR-3: PHD2ProfileSyncService orchestrator + DI registration

Singleton subscribes to LiveStackingService.SubscribeFrameIntegrated
in its constructor (eagerly resolved in Program.cs same way as
PHD2ProfileSyncService) and drives the auto-refocus + auto-recenter
state machine.
...
```

Scopes follow the plan file phase IDs (LSTR-3 / VIDPL-7 / PH2X-4 / etc).
For ad-hoc work that isn't planned, use `ui:` / `feat:` / `fix:` /
`docs:` / `chore:`.

End commits with the Co-Authored-By line if AI-assisted:

```
Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
```

## Branches + PRs

- `master` is the stable trunk. `preview` carries the 0.99.x preview
  releases. **Both branches are protected**: changes land through a pull
  request carrying an approving review from a code owner
  (see [`.github/CODEOWNERS`](.github/CODEOWNERS)). Force pushes and
  branch deletion are refused, and review threads have to be resolved
  before merge.
- Feature branches `feat/short-name` -> PR back to `master`.
- Bug fix branches `fix/short-name`.
- Squash + merge is the default, keeps the trunk tidy.
- A fix that belongs in both branches lands on `master` first and is
  ported to `preview` afterwards. Do not develop the same change twice.
- Releases are cut by pushing an annotated tag (`v0.98.x` from `master`,
  `v0.99.x-preview1` from `preview`); the workflow builds every target
  from the tag, so no version number lives in the tree.

## Tests

- All new services + non-trivial helpers get unit tests
- Pure functions (e.g. `CalibrationStepCalculator`, `FrameQualityAnalyzer`)
  get golden-value tests
- WebSocket / endpoint integration tests are not yet in place, only
  manual smoke testing on the dev box. Contributions welcome.
- Adding a block or a field to the `/ws/status` payload means pinning
  it in `tests/NINA.Polaris.Test/status-contract.txt` in the same
  commit, or the contract test fails for everyone else.
- `dotnet test tests/NINA.Polaris.Test/NINA.Polaris.Test.csproj` runs
  the full suite (~450 tests, ~5 seconds on RPi 5).

## License + 3rd-party

MPL 2.0 (same as upstream NINA). Adding a new NuGet package: include
the license in any new `docs/third-party-licenses.md` we maintain
(currently a stub, first contribution opportunity).

## Hooks / settings

If you're using Claude Code locally, project-specific permissions are
in `.claude/settings.json`. The fewer-permission-prompts skill can
generate them from your transcript.

## See also

- [README.md](README.md), high-level feature list
- [docs/user-guide/](docs/user-guide/), end-user perspective
- [ARCHITECTURE.md](ARCHITECTURE.md), system overview + service
  dependency diagram
