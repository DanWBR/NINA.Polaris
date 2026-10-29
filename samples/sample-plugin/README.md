# Sample N.I.N.A. Polaris plugin

A minimal example showing how a third-party assembly can contribute a
new sequencer instruction to the Advanced Sequencer.

## What it does

The sample registers one new instruction, `Beep`, that writes a row
into the log when executed. It's deliberately useless on its own; the
point is to show the plumbing.

## Building

```bash
cd samples/sample-plugin
dotnet build -c Release
cp bin/Release/net10.0/SamplePlugin.dll ../../publish/win-x64/plugins/
```

(Replace the publish path with wherever you run N.I.N.A. Polaris from.
You can also set `Plugins__Directory=/absolute/path/to/plugins` to
load from any location.)

## Anatomy

Two files:

- `SamplePlugin.csproj`, references `NINA.Polaris.dll` (the host you
  built from the main solution) and targets `net10.0`.
- `BeepPlugin.cs`:
  - `BeepInstruction` is a `SequenceInstruction` with a stable `Type`
    discriminator (`"SamplePlugin.Beep"`) and a `Message` field.
  - `BeepPlugin` implements `INinaPolarisPlugin`. In its `Register`
    method it calls
    `registry.RegisterSequencerEntity<BeepInstruction>("Plugins / Sample")`.

## What happens on startup

The `PluginLoaderService` (hosted service) scans
`Plugins:Directory` (default `./plugins`), loads every `.dll` into an
isolated `AssemblyLoadContext`, finds types implementing
`INinaPolarisPlugin`, instantiates them, and invokes `Register`.

Once that returns, the new entity is:

- Resolvable by the polymorphic JSON converter, sequences saved to
  disk that reference `"$type": "SamplePlugin.Beep"` will round-trip
  correctly.
- Visible in the Advanced Sequencer palette under the category you
  registered it with (`Plugins / Sample` in this example), drag it
  into any container, set the `Message` field, and run.
- Listed at `GET /api/plugins` along with the discriminators it owns.

Failures are isolated: if one plugin throws during load or `Register`,
it's logged and skipped, and the rest of the host keeps running.

## Contributing a plate solver

A plugin can also add a plate solver. Implement
`NINA.Polaris.Services.PlateSolving.IPlateSolver` and register an
instance from `Register`:

```csharp
public void Register(IPluginRegistry registry) {
    registry.RegisterPlateSolver(new MySolver());
}
```

The solver then:

- Appears in `GET /api/platesolve/solvers` and in the Solver list of
  Settings > Plate solving.
- Can be picked as the primary solver, and as the blind solver when
  `SupportsBlindSolve` is true.
- Runs through the same dispatcher as the built-in solvers, so the
  fallbacks, the progress log, the timeout and the Cancel button work
  the same way.

The contract:

- `Id` is what the profile stores, so keep it stable across versions.
  Built-in ids (`astap`, `asterion`, `custom` and the others) and an id
  another plugin already registered are refused with
  `InvalidOperationException`.
- `IsAvailable` is read every time the solver list is shown or a solve
  starts. Keep it cheap: a file-exists check, not a test run.
- `SolveAsync` gets a FITS path and returns a `PlateSolveResult` with RA
  (hours and degrees), Dec, pixel scale and rotation for the field
  centre. Fill the CD matrix and `CrPix1/2` when the solver reports them:
  they carry the mirror flip that the rotation alone cannot. Return
  `PlateSolveResult.Failed(...)` for a solver-level failure instead of
  throwing.
- Honour the cancellation token, and kill any process you start
  (`Process.Kill(entireProcessTree: true)`), so a cancelled solve leaves
  nothing running.

On a Polaris rig the plugin folder is `/opt/polaris/plugins`. An apt
upgrade only replaces the files the package owns, so a plugin there
survives updates. Restart Polaris after adding or removing one.

A solver that only needs a command line and writes a FITS WCS file does
not need a plugin: Settings > Plate solving > Custom solver runs it from
an executable path and an argument template.
