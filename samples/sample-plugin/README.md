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
