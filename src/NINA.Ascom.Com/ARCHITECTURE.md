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
- `AscomComFilterWheel.cs`: `IFilterWheel` adapter built on
  `ASCOM.Com.DriverAccess.FilterWheel`. Going through the Platform's
  DriverAccess (instead of raw `IDispatch` + a hand-rolled STA message pump)
  is what lets a .NET AnyCPU driver such as a DIY MilkyWheel load and connect
  in the 64-bit host — the raw path fast-failed those drivers (0xC0000409).
  DriverAccess manages the COM apartment itself, so this adapter needs no STA
  dispatcher; it calls on the thread pool via `Task.Run`, matching NINA.

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

## Why DriverAccess, not raw COM (WINEXIT-2 history)

An earlier attempt (#650) isolated the driver in a separate process to stop
a crashing driver from taking the host down. It worked as isolation, but the
child still activated the driver through the same raw `IDispatch` + STA-pump
path — so the real fix was never the process boundary, it was the activation
method. NINA loads the very same drivers in a 64-bit process with no
isolation, because it uses `ASCOM.Com.DriverAccess`. Adopting DriverAccess
made the driver simply work in-process, so the out-of-process host, its x86
child, and the `--ascom-com-host` self-relaunch were all removed. If a driver
ever crashes even through DriverAccess, re-introducing isolation would mean a
child that *also* uses DriverAccess — not the old raw-COM host.

The remaining raw-COM adapters (camera / focuser / telescope / switch) have
the same latent risk and should migrate to DriverAccess too; the filter wheel
is the first.

## Threading model

The raw-COM adapters each get their own `AscomComStaDispatcher` (~1 MB stack
+ a kernel thread per connected device). The DriverAccess filter wheel needs
none — DriverAccess owns the COM apartment, so its calls run on the thread
pool via `Task.Run`, as in NINA.

## Licensing

Polaris stays MPL 2.0. The ASCOM Platform is freely redistributable
but Polaris does not ship any of its binaries; users install the
Platform separately from `https://ascom-standards.org/`.
