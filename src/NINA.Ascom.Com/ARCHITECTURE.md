# NINA.Ascom.Com

ASCOM Platform COM-interop adapters exposed through Polaris's
backend-agnostic device interfaces (`ICamera`, `ITelescope`, future
`IFocuser` / `IFilterWheel`).

## Why this project exists

Before this project, Polaris on Windows could only reach ASCOM
hardware via Alpaca, either by routing through ASCOM Remote Server
or by using the Alpaca Omni Simulator. Both work but add a hop
(HTTP localhost → COM) and require a separate process to be running.

This adapter eliminates that hop by late-binding to the ASCOM
drivers directly through COM. No reference to the ASCOM Platform
assemblies; Polaris ships without any ASCOM bits and starts fine on
machines that have never installed the Platform.

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
- `AscomComActivation.cs`: single activation choke point. Refuses a
  32-bit-only driver in the 64-bit host with a clear message, logs a
  synchronously-flushed breadcrumb around `CreateInstance`, and turns a
  failing `Connected = true` into an HRESULT-tagged error
  (`ConnectFailed`). `RegisteredBitness` exposes the driver's registered
  in-proc bitness so a factory can decide whether to host it out-of-process.
- `AscomHostChannel.cs`: app-side transport for the out-of-process driver
  host (see below). Launches the host (x64 self-relaunch or the packaged
  x86 exe) and marshals ASCOM member access over newline-delimited JSON.
- `AscomComHostRunner.cs`: the driver-side of that host — the JSON protocol
  loop running the COM object on an STA pump. Shared by both host processes.
- `AscomComFilterWheelHosted.cs`: `IFilterWheel` adapter that drives the
  wheel through an `AscomHostChannel` instead of an in-process COM object.

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

## Out-of-process driver host (WINEXIT-2)

Polaris is 64-bit. A 32-bit-only in-proc ASCOM driver (e.g. a DIY
MilkyWheel filter wheel) cannot load in a 64-bit process, and a driver
that dies with a native corrupted-state exception a managed `try/catch`
can't see would take the whole host down. Both are solved by running the
driver in a separate process:

- The driver-side protocol loop is `AscomComHostRunner` (activate / get /
  set / call / setup / dispose over newline-delimited JSON on stdin/stdout),
  running the driver on an `AscomComStaDispatcher` (STA + message pump).
- There are two host processes, both running that same loop:
  - **x64** is the main Polaris exe re-launched with `--ascom-com-host`
    (`Program.cs` intercepts it before starting Kestrel). Zero extra
    packaging, and it covers 64-bit-registered drivers that still crash the
    host — a .NET AnyCPU MilkyWheel registers `inproc64=True` yet hard-exits
    the 64-bit app on activate.
  - **x86** is `NINA.Ascom.Host.exe`, a thin entry point that calls the same
    `AscomComHostRunner`, published self-contained for win-x86 and staged
    into `{output}/ascom-host/win-x86` by `NINA.Polaris.csproj`. It exists
    because a 32-bit-only in-proc driver cannot load in the 64-bit app.
- The app side is `AscomHostChannel`: it launches the right host (self for
  x64, packaged exe for x86), marshals member access, and turns a child
  crash (OS process exit) into a clean `AscomHostException` — the app
  survives.
- `EquipmentManager.CreateAscomFilterWheel` sends a 32-bit-only wheel to the
  x86 host and everything else to the x64 self-host, hosting out-of-process
  whenever a host can be launched. Scope today is the filter wheel; the same
  channel generalises to the other small-payload devices (focuser / mount /
  switch). ASCOM cameras stay in-process (their `ImageArray` needs a binary
  side-channel, not JSON).

## Threading model

Each in-process driver instance gets its own `AscomComStaDispatcher`.
Cost: ~1 MB stack + a kernel thread per connected device. A typical rig
(camera + mount + focuser + filter-wheel) uses 4 threads. An
out-of-process driver moves that STA into the child process instead.

## Licensing

Polaris stays MPL 2.0. The ASCOM Platform is freely redistributable
but Polaris does not ship any of its binaries; users install the
Platform separately from `https://ascom-standards.org/`.
