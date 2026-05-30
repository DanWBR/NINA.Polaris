# NINA.Ascom.Com

ASCOM Platform COM-interop adapters exposed through Polaris's
backend-agnostic device interfaces (`ICamera`, `ITelescope`, future
`IFocuser` / `IFilterWheel`).

## Why this project exists

Before this project, Polaris on Windows could only reach ASCOM
hardware via Alpaca — either by routing through ASCOM Remote Server
or by using the Alpaca Omni Simulator. Both work but add a hop
(HTTP localhost → COM) and require a separate process to be running.

This adapter eliminates that hop by late-binding to the ASCOM
drivers directly through COM. No reference to the ASCOM Platform
assemblies; Polaris ships without any ASCOM bits and starts fine on
machines that have never installed the Platform.

## Files

- `NINA.Ascom.Com.csproj` — `net10.0` target (not `net10.0-windows`
  so the managed assembly compiles on Linux/macOS CI; the COM call
  sites are guarded at the EquipmentManager entry point).
- `AscomComRegistry.cs` — registry walker. Enumerates installed
  drivers per device type by walking `HKLM/HKCU SOFTWARE\ASCOM\*
  Drivers` plus the WOW6432Node variant. Headless equivalent of
  the ASCOM Chooser dialog.
- `AscomComStaDispatcher.cs` — one STA worker thread per driver
  instance. Every COM property/method call is queued through here
  so ASCOM's apartment semantics are honoured and a slow operation
  on one device (telescope slew) can't block another (autofocus
  loop on the focuser).
- `AscomComCamera.cs` — `ICamera` adapter for ICameraV3 drivers.
  Supports connect/disconnect, sensor metadata, cooler, binning,
  gain (numeric range only), single-frame capture, abort, subframe.

## Out of scope (for now)

- ASCOM Chooser dialog (we use registry walk + per-driver SetupDialog
  instead — the Chooser brings nothing the user can't get from the
  RIGS driver dropdown).
- Rotator / Dome / FlatPanel / ObservingConditions / Switch adapters.
  Registry enumeration already covers them, but no concrete
  `IRotator`/`IDome` adapter classes yet — when the user wires up
  one of these, add a matching class in the same shape as the
  existing four.
- Focuser + FilterWheel UI driver picker. Backend (ASCOM-3) routes
  `?driver=ascom-com` correctly via /api/focuser /api/filterwheel,
  but the RIGS UI cards still default to the INDI device dropdown.
  Follow-up.

## Threading model

Each driver instance gets its own `AscomComStaDispatcher`. Cost:
~1 MB stack + a kernel thread per connected device. A typical rig
(camera + mount + focuser + filter-wheel) uses 4 threads.

## Licensing

Polaris stays MPL 2.0. The ASCOM Platform is freely redistributable
but Polaris does not ship any of its binaries; users install the
Platform separately from `https://ascom-standards.org/`.
