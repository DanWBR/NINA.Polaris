# Native camera SDK backends (SVBony, ZWO) — high-fps planetary video

Polaris can talk to **SVBony** and **ZWO ASI** cameras through their native
USB SDKs, bypassing the INDI server entirely. This is the fast path for
high-frame-rate planetary video: the INDI route does a full per-exposure
round-trip per frame (often ~1 fps for a non-streaming driver), whereas the
native SDK streams continuously straight off USB.

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

RIGS → camera driver picker now lists **"SVBony (SDK, native)"** and
**"ZWO ASI (SDK, native)"** whenever the native library loads on the host.
Choose it, Discover, and connect like any other camera. All camera
operations (capture, gain, cooler, ROI, binning, and live video) go through
the SDK while it's the active driver.

## Platforms & packaging

- Native libraries ship **bundled in the package** per architecture:
  Linux **arm64** / **x64** (`.so`) and Windows **x64** (`.dll`). They are
  copied next to the executable in the published build; no separate download.
- On Linux the `.deb` installs udev rules
  (`/lib/udev/rules.d/99-polaris-{svbony,asi}.rules`) so the `polaris`
  service user can open the camera without root, and bumps `usbfs_memory_mb`
  to 200 so high-fps USB3 streaming has enough buffer. The postinst reloads
  udev automatically; replug the camera once after install.
- If the native lib is missing for your platform/arch the driver simply
  doesn't appear in the picker (the INDI driver remains available).

## Measuring the result

Run the Hardware Benchmark video probe (Settings → Hardware Benchmark →
"measure the connected camera", set a small **Video ROI** and tick
**Measure recording**) before/after switching from INDI to the SDK backend
to compare capture fps, transmit fps, record fps and dropped frames.

## Notes

- ToupTek cameras are not yet supported by a native backend (different SDK
  model); use INDI for those.
- Vendor SDK binaries under `camera_sdk/` are redistributed under their
  respective vendor licenses (see that folder's vendor readme files).
