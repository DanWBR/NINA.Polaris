# NINA.Ascom.Com

ASCOM Platform COM-interop adapters exposed through Polaris's
backend-agnostic device interfaces (`ICamera`, `ITelescope`, future
`IFocuser` / `IFilterWheel`).

## Why this project exists

Before this project, Polaris on Windows could only reach ASCOM
hardware via Alpaca, either by routing through ASCOM Remote Server
or by using the Alpaca Omni Simulator. Both work but add a hop
(HTTP localhost → COM) and require a separate process to be running.

This adapter eliminates that hop by talking to the ASCOM drivers
directly. The filter wheel goes through the ASCOM Platform 7 library
(`ASCOM.Com.DriverAccess`, the same path NINA uses); the other devices
still late-bind through raw `IDispatch`. The `ASCOM.Com.Components`
package restores cross-platform and its COM calls are Windows-guarded,
so Polaris still builds for Linux and starts fine on machines that never
installed the Platform (the Windows COM paths simply stay dormant).

## Files

- `NINA.Ascom.Com.csproj`: `net10.0` target (not `net10.0-windows`
  so the managed assembly compiles on Linux/macOS CI; the COM call
  sites are guarded at the EquipmentManager entry point).
- `AscomComRegistry.cs`: registry walker. Enumerates installed
  drivers per device type by walking `HKLM/HKCU SOFTWARE\ASCOM\*
  Drivers` plus the WOW6432Node variant. Headless equivalent of
  the ASCOM Chooser dialog.
- `AscomComStaDispatcher.cs`: one STA worker thread per driver
  instance. Every COM property/method call is queued through here
  so ASCOM's apartment semantics are honoured and a slow operation
  on one device (telescope slew) can't block another (autofocus
  loop on the focuser).
- `AscomComCamera.cs`: `ICamera` adapter for ICameraV3 drivers.
  Supports connect/disconnect, sensor metadata, cooler, binning,
  gain (numeric range only), single-frame capture, abort, subframe.
- `AscomComActivation.cs`: single activation choke point for the raw-COM
  adapters (camera / focuser / telescope / switch). Refuses a 32-bit-only
  driver in the 64-bit host with a clear message, logs a synchronously-flushed
  breadcrumb around `CreateInstance`, and turns a failing `Connected = true`
  into an HRESULT-tagged error (`ConnectFailed`, reused by the DriverAccess
  filter wheel too).
- `AscomComFilterWheelHosted.cs` / `AscomHostChannel.cs` /
  `AscomComHostRunner.cs`: the ASCOM COM `IFilterWheel`, run out-of-process.
  See "Out-of-process filter wheel" below.

## Out of scope (for now)

- ASCOM Chooser dialog (we use registry walk + per-driver SetupDialog
  instead, because the Chooser brings nothing the user can't get from the
  RIGS driver dropdown).
- Rotator / Dome / FlatPanel / ObservingConditions / Switch adapters.
  Registry enumeration already covers them, but no concrete
  `IRotator`/`IDome` adapter classes yet; when the user wires up
  one of these, add a matching class in the same shape as the
  existing four.
- Focuser + FilterWheel UI driver picker. Backend (ASCOM-3) routes
  `?driver=ascom-com` correctly via /api/focuser /api/filterwheel,
  but the RIGS UI cards still default to the INDI device dropdown.
  Follow-up.

## Out-of-process filter wheel (WINEXIT-2)

Two things had to be true for a DIY WinForms/.NET driver (e.g. a MilkyWheel
filter wheel) to work; each was learned the hard way against a real driver:

1. **DriverAccess, not raw `IDispatch`.** Raw `Type.GetTypeFromProgID` +
   `Activator.CreateInstance` + a hand-rolled STA pump fast-fails (0xC0000409)
   at *construction*. The ASCOM Platform's `ASCOM.Com.DriverAccess` (the
   wrapper NINA uses) constructs the driver cleanly.
2. **A minimal process, not the loaded server.** Even through DriverAccess, the
   driver's `Connected = true` (opening a serial port via
   `ASCOM.Utilities.Serial`) fast-fails inside the loaded Kestrel process, yet
   throws a clean error — or connects — in a minimal child. Proven by running
   the exact same connect both ways.

So the wheel runs in a minimal child: the Polaris exe re-launched with
`--ascom-com-host` (`AscomComHostRunner`), hosting a DriverAccess FilterWheel on
an STA pump and serving it over stdin/stdout JSON. `AscomHostChannel` (parent)
marshals member access and turns a child crash into a clean
`AscomHostException`; `AscomComFilterWheelHosted` is the `IFilterWheel` over it.
Self-relaunch means zero extra packaging, and a crashing driver takes down only
its child — the API server surfaces a clean error with an Alpaca hint.

The remaining raw-COM adapters (camera / focuser / telescope / switch) still
activate in-process; native drivers (ZWO etc.) are fine there, but a WinForms
.NET one would hit the same wall and should move to this host too.

## Threading model

The in-process raw-COM adapters each get their own `AscomComStaDispatcher`
(~1 MB stack + a kernel thread per connected device). The filter wheel's STA
pump lives in its child process instead.

## Licensing

Polaris stays MPL 2.0. The ASCOM Platform is freely redistributable
but Polaris does not ship any of its binaries; users install the
Platform separately from `https://ascom-standards.org/`.
