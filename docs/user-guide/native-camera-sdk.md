# Native camera SDK backends (SVBony, ZWO, PlayerOne, ToupTek, Altair) — high-fps planetary video

Polaris can talk to **SVBony**, **ZWO ASI**, **PlayerOne**, **ToupTek** and
**Altair** cameras through their native USB SDKs, bypassing the INDI server entirely.
This is the fast path for high-frame-rate planetary video: the INDI route
does a full per-exposure round-trip per frame (often ~1 fps for a
non-streaming driver), whereas the native SDK streams continuously straight
off USB.

## When to use it

- **Planetary / lunar video**, where you want the highest sustainable frame
  rate at a small ROI. Pick a small subframe (e.g. 640×480) for the highest
  fps.
- For deep-sky imaging the INDI driver is fine; the SDK backend is an
  *alternative* you select per rig, not a replacement.

> Frame rate is ultimately sensor-bound. A cooled deep-sky OSC like the
> SVBony **SV405CC** (Sony IMX294) tops out at tens of fps even over the
> native SDK; true 100 fps needs a small fast planetary sensor
> (e.g. ZWO ASI462/678).

## Selecting the backend

RIGS → camera driver picker lists **"SVBony (SDK, native)"**,
**"ZWO ASI (SDK, native)"**, **"PlayerOne (SDK, native)"**,
**"ToupTek (SDK, native)"** and **"Altair (SDK, native)"** whenever the matching native library loads on
the host. Choose it, Discover, and connect like any other camera. All camera
operations (capture, gain, cooler, ROI, and live video) go through the SDK
while it's the active driver.

## Platforms & packaging

- Native libraries ship **bundled in the package** per architecture:
  Linux **arm64** / **x64** (`.so`) and Windows **x64** (`.dll`). They are
  copied next to the executable in the published build; no separate download.
  (PlayerOne additionally ships arm32/x86 `.so` in the SDK, though Polaris
  packages arm64/x64.)
- On Linux the `.deb` installs udev rules
  (`/lib/udev/rules.d/99-polaris-{svbony,asi,playerone,touptek,altair}.rules`) so
  the `polaris` service user can open the camera without root, and bumps
  `usbfs_memory_mb` for high-fps USB3 streaming. The postinst reloads udev
  automatically; replug the camera once after install.
- If the native lib is missing for your platform/arch the driver simply
  doesn't appear in the picker (the INDI driver remains available).

## Measuring the result

Run the Hardware Benchmark video probe (Settings → Hardware Benchmark →
"measure the connected camera", set a small **Video ROI** and tick
**Measure recording**) before/after switching from INDI to the SDK backend
to compare capture fps, transmit fps, record fps and dropped frames.

## Maturity & on-hardware status

| Backend   | Native libs bundled            | Validated on hardware |
|-----------|--------------------------------|-----------------------|
| SVBony    | Linux arm64/x64, Windows x64   | SV405CC (Pi 5, USB3)  |
| ZWO ASI   | Linux arm64/x64, Windows x64   | not yet               |
| PlayerOne | Linux arm64/arm32/x64/x86, Win x64 | not yet           |
| ToupTek   | Linux arm64/x64, Windows x64   | not yet               |
| Altair    | Linux arm64(glibc)/x64, Windows x64 | not yet          |

The ZWO, PlayerOne, ToupTek and Altair backends are written to the vendor SDKs and
compile + pass managed smoke tests, but have **not** been exercised on real
cameras yet. Treat the first connect/capture/stream as a shakedown. If you
hit a bug, capture the Polaris log (`journalctl -u polaris.service -f` on the
Pi) around connect/capture and file it — the fragile spots below are the
first places to look.

## Known fragile spots (first-test checklist)

These are the parts most likely to need a fix once a real camera is plugged
in. Documented so whoever debugs the first session knows where to start.

- **ToupTek — raw format / bit depth.** The backend forces raw Bayer output
  (`OPTION_RAW=1`) at max bit depth (`OPTION_BITDEPTH=1`) and reads the actual
  depth + Bayer pattern back from `get_RawFormat` on connect. Some models only
  deliver 8-bit, ignore the bit-depth option, or report a different FourCC —
  if the image is mono-looking, half-height, or has wrong colors, this is the
  first thing to verify (log the FourCC + bitdepth from connect).
- **ToupTek — live ROI.** `put_Roi` is applied without stopping pull mode and
  the frame buffer re-sizes from `get_Size` per frame. On sensors that won't
  change ROI live, this may need a stop/restart-pull around `ApplyRoi`.
- **ToupTek — OEM rebadges.** Omegon / RisingCam etc. are ToupTek-
  based and enumerate under the same SDK, but only genuine ToupTek units are
  expected to work as-is; OEM PIDs may need adding to the udev rule.
- **Altair — dedicated backend.** Altair Astro cameras have their own
  vendor SDK drop (`camera_sdk/Altair`, `libaltaircam`) and a dedicated
  "Altair (SDK, native)" picker entry, separate from ToupTek. It is the same
  ToupTek-derived API surface (Altair is a ToupTek OEM), so the same ROI /
  raw-format / pull-mode notes apply. Vendor udev IDs `04b4`/`0547`/`16d0`.
- **PlayerOne — config-value union.** `POAConfigValue` (a C `union` of
  `long`/`double`/`POABool`) is marshalled via an explicit-layout struct
  overlapping `int`/`double` at offset 0. If gain, exposure or temperature
  come back with nonsensical values, this marshalling is the prime suspect.
- **ROI alignment (all SDK backends).** Sub-frame width/height are snapped to
  the vendor's required multiples (ZWO width%8/height%2; PlayerOne width%4/
  height%2; ToupTek even). A camera that rejects a subframe or returns a
  shifted image usually means the alignment/offset math needs tightening for
  that sensor.
- **USB udev IDs (Linux).** PlayerOne vendor id `a0a0` is confirmed from the
  SDK header; ToupTek uses `04b4`/`0547`. A camera that connects as root but
  not as the `polaris` service user means its vendor/product id isn't covered
  by `/lib/udev/rules.d/99-polaris-{playerone,touptek}.rules` — add it and
  reload udev.

## Notes

- The ToupTek backend uses the vendor's official cross-platform C# binding
  (`camera_sdk/ToupTek/dotnet/toupcam.cs`) in callback (pull) mode.
- Vendor SDK binaries under `camera_sdk/` are redistributed under their
  respective vendor licenses (see that folder's vendor readme files).
