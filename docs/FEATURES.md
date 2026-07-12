# Polaris Astro Controller — Feature Overview

**Polaris Astro Controller** is a browser-controlled astrophotography platform that
runs on a Raspberry Pi, mini-PC, or any small Linux/Windows host on your
network. Point any device — laptop, tablet, or phone — at the host and you
get the full equipment, capture, and processing workflow with no desktop
application to install. Everything below runs from a single web UI.

> This page is a feature map organized by sidebar panel. Each panel links to
> its in-depth user-guide page where one exists.

---

## At a glance

- **Runs anywhere small** — Raspberry Pi 4/5, Orange Pi, x86 mini-PCs and
  sticks, or a Windows box. ARM64, ARMHF, and x86-64 are all supported.
- **Browser-native** — no app to install on the client; works on phones,
  tablets, and laptops over WiFi.
- **Hardware-agnostic** — INDI, ASCOM Alpaca, ASCOM Platform (Windows COM),
  and native vendor SDKs (ZWO, Svbony, Player One, ToupTek, Altair,
  Canon/Nikon/Sony DSLR) all in one app.
- **Acquire and process in one place** — from polar alignment through
  capture, live stacking, calibration, integration, AI cleanup, and a final
  non-destructive editor pass with JPEG/PNG/TIFF export.
- **Offline-first** — bundled deep-sky catalog, star catalog, and sky map
  work with no internet at the telescope.
- **Self-updating** on Pi/SBC `.deb` installs — a one-click in-app update
  from GitHub releases.

---

## HOME — Dashboard

The landing dashboard shown on connect.

- **Session summary** — active rig, connection status of each device, and
  current activity at a glance.
- **Quick navigation** — jump straight into the panel you need.
- **Host info** — model, OS, architecture, core count, and CPU of the
  machine running Polaris.

---

## RIGS — Equipment Management

Multi-rig equipment hub. Define several telescope setups ("rigs") and switch
between them in one click. → [rigs.md](user-guide/rigs.md)

- **Multi-rig profiles** — save each optical train (main scope + camera +
  mount + guider + accessories) as a named rig; the whole app retargets when
  you switch the active rig.
- **Per-role device cards** — Main Telescope, Camera, Mount, Focuser, Filter
  Wheel, Guidescope, Guide Camera, Guide Focuser, Rotator, Flat Panel, Dome,
  and Weather, each with connect/disconnect and live status.
- **Auxiliary camera + focuser** — a second imaging camera on the same mount
  with its own optics/exposure/gain, captured in parallel into a separate
  `aux/` tree; focusable from the FOCUS tab (manual or Auto V-curve).
- **Gain ↔ ISO aware** — astronomy cameras show analogue gain with an ISO
  explainer; DSLR/mirrorless bodies (gphoto on Linux) get a real ISO dropdown.
- **Connection backends in one place:**
  - **INDI** (TCP/XML) — full native client for INDI servers.
  - **ASCOM Alpaca** (HTTP) — auto-discovery + connect for Alpaca devices.
  - **ASCOM Platform (COM)** — Windows-only direct driver access.
    → [ascom-com.md](user-guide/ascom-com.md)
  - **Native vendor SDKs** — ZWO ASI, Svbony, Player One, ToupTek and Altair
    cameras driven directly. → [native-camera-sdk.md](user-guide/native-camera-sdk.md)
  - **DSLR** — Canon, Nikon, and Sony bodies on Windows; gphoto2 on Linux.
- **Per-rig optical data** — main + guider focal length (auto-updated from
  plate solves), filter offsets table, and per-camera quirk overrides (Bayer
  pattern, vertical flip) for cameras that report wrong metadata.
- **Robust INDI handling** — ack-based property writes, configurable
  per-device pre-connect delays, and idempotent connect logic.
- **Embedded INDI Drivers Manager** — the indi-web (indiwebmanager) UI
  inside the panel via iframe, so you can add/remove INDI drivers and
  start/stop the indiserver without SSH. → [indi-web.md](user-guide/indi-web.md)
- **Wedged-driver watchdog** — a single INDI driver that stops
  delivering frames (dropped BLOB) is restarted on its own, and every
  running driver has a one-click restart, without touching the rest of
  the server. → [indi-web.md](user-guide/indi-web.md)
- **Live INDI property tree** — browse and set any INDI property directly.

---

## POLAR — Polar Alignment

Get the mount's polar axis aligned before the night starts.
→ [polar-alignment-rudimentary.md](user-guide/polar-alignment-rudimentary.md)

- **TPPA (Three-Point Polar Alignment)** — plate-solve-driven: capture, slew,
  solve at three points, then a live error vector tells you which way to turn
  the altitude/azimuth bolts.
- **Refinement mode** — a continuous capture/solve loop with an on-screen
  error overlay that updates as you adjust, so you converge to near-zero.
- **Rudimentary mode** — single-target alignment helper for setups without
  a clear view of the celestial pole.

---

## SKY — Sky Explorer (Map · Tonight · Weather)

Plan what to shoot and where to point. → [sky-explorer.md](user-guide/sky-explorer.md)

- **Offline sky map** — interactive sky atlas with bundled star data; works
  with no internet at the scope.
- **Offline DSS sky imagery** — an optional real DSS colour photo background
  for the map, provisioned ahead of time (~30 MB to ~400 MB by tile order) so
  it works fully offline at the telescope, with an online CDS fallback.
- **Target search** — find deep-sky objects by name/catalog from the bundled
  DSO database, or enter manual RA/DEC.
- **DSO preview thumbnails** — a small offline DSS thumbnail on each search
  result and atlas card, so galaxies and nebulae are easy to tell apart.
- **FOV overlays + framing** — draw your camera's field of view on the map,
  rotate it, and drag to frame a target; the blue (mount) and red (target)
  rectangles track plate-solve results. A **pink** rectangle shows the aux
  camera's field when an Auxiliary Camera System is configured.
- **Aux FOV confirmation** — a plate solve from SKY fires a parallel solve on
  the aux camera (independent hardware), so the pink rectangle reflects the
  true rotation + scale the aux photo will come out with.
- **Slew & center** — send the mount to a target and plate-solve-center on it.
- **Mosaic planner** — lay out multi-panel mosaics over a target, with a live
  preview of the tile grid drawn on the sky map as you adjust columns, rows,
  and overlap.
- **Tonight** — altitude curves and the best objects to image for your
  location and date, with twilight/night windows; the ranking also surfaces
  large emission/dark nebulae that carry size but no stellar magnitude.
- **Weather** — current conditions and forecast for the observing site.
- **Stellarium sync** — drive the view from / to a Stellarium instance.

---

## FOCUS — Focusing & Field Analysis

Achieve and verify focus, and diagnose the optical train.
→ [focus.md](user-guide/focus.md)

- **Manual focus** — step the focuser in/out with a live preview canvas and
  real-time HFR (half-flux radius) + Laplacian-variance sharpness readout.
- **Auto-focus** — automated V-curve sweep with parabola fit; robust on
  heavy defocus, with an HFR-vs-position chart of the run. An **optical-train
  selector** runs the sweep on the main, auxiliary, or guide camera+focuser
  pair.
- **Aux / guide focusing** — Camera + Focuser source switches (Primary /
  Auxiliary / Guide) target a second OTA or a motorised guide scope; the
  manual jog (position, range, GoTo, abort) follows the selected motor.
- **Bahtinov mask helper** — overlay analysis of a Bahtinov diffraction
  pattern for precise manual focus.
- **Field analysis tools** (shared capture, multiple views):
  - **Tilt** — sensor-tilt map from corner-vs-center star sizes.
  - **Aberration Inspector** — a 3×3 mosaic of 1:1 corner/center crops to
    judge coma, astigmatism, and backfocus at a glance.
  - **Inspector** — full-field star metrics (eccentricity, FWHM) to spot
    collimation and spacing problems.
- **Sensor analysis** — characterize the camera sensor itself.
  → [sensor-analysis.md](user-guide/sensor-analysis.md)

---

## GUIDE — Autoguiding

Keep stars round during long exposures. Two interchangeable backends.

- **Native guider** — PHD2's guiding math ported to C#, driving the rig's
  guide camera + mount directly with no external PHD2 process.
  → [guide-native.md](user-guide/guide-native.md)
  - Multi-star guiding, per-equipment calibration save/restore.
  - Dithering with ASIAIR-style settle readout (shows live settle error
    until it stays under tolerance).
  - Star-lost detection and a watchdog that keeps the UI responsive.
  - Predictive algorithm option (periodic-error + drift modeling).
  - ZFilter option — a low-pass algorithm ported from PHD2 that smooths noise
    and seeing while still chasing drift, with a per-rig exposure-factor tuning.
  - Meridian-flip aware (pauses/resumes around the flip).
- **PHD2 integration** — full control of an external PHD2.
  → [guide-phd2.md](user-guide/guide-phd2.md)
  - Connect, manage profiles + equipment, set exposure, launch/shutdown.
  - Smart calibration orchestration and guiding-algorithm presets.
  - Embedded PHD2 GUI in the browser via xpra (Linux) or noVNC.
- **Live guiding telemetry** — RA/Dec error graph, RMS, and dither/settle
  state surfaced in the top status bar.

---

## PREVIEW — Test Shots

Quick framing and check exposures. → [preview.md](user-guide/preview.md)

- **Snap single frames** with chosen exposure/gain/binning, optional save.
- **Continuous stream mode** for live framing.
- **Plate solve from preview** — solve the current frame and sync/center.
- **Full image tools** — stretch controls, statistics, star annotations,
  crosshair/grid/pixel readout (shared with the Studio viewer).

---

## AUTORUN — Shooting Schedule

A straightforward sequence runner against the current target.
→ [autorun.md](user-guide/autorun.md)

- **Frame list editor** — rows of exposure × count per filter/gain/binning.
- **Polaris Shutter** start/stop control with live progress.
- **End-of-session actions** — warm camera + cooler off, park/go-home, send
  the focuser to zero, and optional host shutdown.
- **Flat Wizard** sub-tab — automated flat-field acquisition with
  binary-search exposure per filter, a trained-exposure cache, and per-rig
  defaults. → [flat-wizard.md](user-guide/flat-wizard.md)

---

## PLAN — Multi-Target Night Planner

ASIAIR-style whole-night planning: queue several targets, each with its own
frame list, run in order with automatic slew + plate-solve-center.

- **Multiple targets per plan** — added from catalog search, manual RA/DEC,
  the current mount position, or framed visually in the Sky map.
- **Per-target frame lists** and first-light delay.
- **Scheduling** — start now or at a clock time; end when all frames are
  done, at astronomical dawn, or at a set time.
- **Plan-level automation** — auto guiding, auto cooling (with target temp),
  per-target or initial auto-focus, and auto meridian flip with exposed flip
  tuning (minutes-after, recenter, autofocus-on-flip).
- **End actions** — warm/cooler-off, park/go-home, focuser-to-zero, and a
  confirm-gated host shutdown.
- **Elevation chart** of each target across the night for sanity-checking
  the order.
- **Global plan library** — saved plans are runnable with any active rig.

---

## LIVE — Live Stacking (EAA)

Real-time electronically-assisted-astronomy stacking that builds an image as
frames arrive. → [live-stacking.md](user-guide/live-stacking.md)

- **Continuous integration** with live alignment (star matching) and a
  growing preview.
- **Per-frame pre-processing** — optional calibration (auto-matched master
  dark/flat/bias) and GraXpert background extraction applied before each
  frame is added.
- **OSC color stacking** — automatic for one-shot-color cameras.
- **Kappa-sigma pixel rejection** — optionally reject per-pixel outliers
  (cosmic rays, plane / satellite trails, dithered hot pixels) instead of
  folding them into the running mean; per-rig, best combined with dithering.
- **Auto re-focus / re-center triggers** — kick off an auto-focus or a
  re-center when HFR drifts or the target wanders.
- **Save the current stack** to FITS at any time (into a dedicated `stacked`
  folder).
- **Client-side compute offload** — on a slow server (Pi 2/3) the stacking
  math can run in your browser via WebAssembly instead of the host.
  → [client-side-compute.md](user-guide/client-side-compute.md)
- **Server-side capture loop** (experimental) — an opt-in mode that runs the
  capture loop on the server, so the stack keeps building even if the browser
  disconnects; the browser still handles WASM stacking compute.
- **Reference-frame suggestions** to improve alignment quality.

---

## VIDEO — Planetary & Lucky Imaging

High-frame-rate capture and stacking for planets, the Moon, and the Sun.
→ [video-planetary.md](user-guide/video-planetary.md)

- **SER capture** — high-fps video recording (tested to ~100 fps) with ROI
  subframing.
- **Frame-quality analysis** — Laplacian-variance scoring to rank frames.
- **Lucky-imaging stacker** — keep the best N% of frames and stack them.
- **Slew preview inset** — small live view while you center the planet.

---

## ADV — Advanced Sequencer

A full NINA-style, tree-based sequencer for complex automated nights.
→ [adv-sequencer.md](user-guide/adv-sequencer.md)

- **Tree of containers, instructions, conditions, and triggers** with
  drag-and-drop editing.
- **Instruction library** — camera (cool/warm/expose), mount (slew/park/find
  home), focuser, filter wheel, guider (start/stop/dither), flow control
  (wait-until-time), and more.
- **Loop conditions and event triggers** — meridian flip, auto-focus on
  temperature / HFR / time, and other operational triggers.
- **JSON serialization** — save, load, and share sequence documents.

---

## STUDIO — Post-Processing

Browse, calibrate, and integrate your captured frames on the host.
→ [studio.md](user-guide/studio.md)

- **Frame browser / library** — scan and organize captured frames.
- **Master generation** — build master darks, flats, and bias frames.
- **Calibration** — apply masters to light frames.
- **Batch stacking** — register and integrate a set of lights into a master,
  with progress reporting.
- **Drizzle integration** — optional 1x / 2x / 3x drizzle (Fruchter & Hook)
  for well-dithered, undersampled data, with an automatic recommendation
  computed from the frames' measured FWHM so you only upscale when it
  actually recovers resolution.
- **Channel combine** — merge per-filter masters into one image:
  - **RGB compose** (no luminance needed),
  - **LRGB** (Lab / ratio combine when you have a luminance master),
  - **Narrowband palettes** — one-click SHO / HSO / HOS / HOO from mono
    S / H / O masters, plus continuum subtraction (Ha−R, OIII−G) and
    free-form **PixelMath**,
  - **OSC dual-band → SHO** — pull Ha / SII and OIII out of Ha+OIII and
    SII+OIII one-shot-color masters and combine them, with automatic star
    alignment between the two frames.
  → [lrgb-mono-workflow.md](user-guide/lrgb-mono-workflow.md)
- **Color calibration** (Siril-style, plate-solve-driven):
  - **Background neutralization** (zero-config),
  - **Manual** (background patch + white-reference patch),
  - **PCC** — Photometric Color Calibration against the bundled APASS DR10
    star catalog for science-grade color.
  - **SPCC** — SpectroPhotometric Color Calibration: integrates each
    catalog star's spectrum through your sensor and filter response
    (bundled Pickles library + the imported **Siril SPCC database** of
    real measured curves for dozens of cameras and filters). Auto-selects
    the sensor and OSC/mono type from the frame's FITS header.
  - Both PCC and SPCC finish with a **white-balance summary** plot
    (measured vs expected channel ratios, robust fit).
  → [color-calibration.md](user-guide/color-calibration.md)
- **Quick processing** — debayer, background extraction, noise reduction,
  and sharpening passes.
- **Star colour repair** — fixes the one-sided colour fringe (a blue/magenta
  cast with a dark notch) some OSC cameras leave on bright stars, via sub-pixel
  channel alignment and radial colour/luminance symmetry repair. Neighbour-aware
  for crowded fields, with a before/after montage of the brightest stars.
- **Crop tool** with a drag picker, plus an **Auto crop** button that
  suggests the largest fully-stacked rectangle to trim the ragged dither
  borders left by slightly-misaligned integrations — you review and adjust
  it before saving.

---

## EDITOR — Non-Destructive Image Editor

A Lightroom-style finishing editor for your master image.
→ [editor.md](user-guide/editor.md)

- **Slider-based adjustments** grouped into Light, Color, Effects, and
  Detail, with an auto-tune button for a sensible starting point.
- **Non-destructive** — edits are stored in a sidecar `.edit.json` next to
  the source file; the original is never overwritten.
- **AI section** — run GraXpert BGE / Denoise / Deconvolution from inside the
  editor (see AI inference below) with a before/after slider.
- **Star removal** — split a master into starless and stars-only images with an
  in-browser AI model (nox or starrem2k13, both MIT, or the opt-in StarNet++),
  launched from the FILES toolbar. Optional mask-guided halo reduction cleans the
  rings and halos around bright stars. Writes `_starless` and `_stars` FITS and
  opens a before/after comparator. → [star-removal.md](user-guide/star-removal.md)
- **Image Blend** — recombine two same-size images with an independent
  blackpoint/midtones/highlights stretch per layer, a blend mode (Screen / Add /
  Lighten), and opacity. The PixInsight ImageBlend-style finish for putting
  stretched stars back onto a separately-stretched nebula; live preview, full-res
  16-bit `_blend` FITS output. → [image-blend.md](user-guide/image-blend.md)
- **Export** to JPEG / PNG / TIFF with quality and resize options.
- **Auto-stretch** ported from GraXpert (15% background, 3-sigma) as the
  default display stretch.

---

## AI Inference (ONNX / GraXpert + Polaris models)

GraXpert's models plus Polaris's own AI models run on whatever hardware is
fastest, with no per-frame cloud calls. → [onnx-inference.md](user-guide/onnx-inference.md)

- **Background Extraction (BGE)**, **Denoise** (v2 + v3), and
  **Deconvolution** (stars / objects) models (GraXpert).
- **Polaris's own models** — a **Detail / Sharpen** enhancer, **Halo removal**
  (cleans the reflection halos around bright stars), and an **Upscaler**
  (2x / 4x super-resolution). All are NPU / quantization-friendly and run
  in the browser or on the SBC NPU.
- **Star-removal models** — nox and starrem2k13 (both MIT, bundled) plus the
  opt-in StarNet++ (NonCommercial). All are FP16-quantized so they run in the
  browser on phones and tablets via WebGPU, or on WASM.
  → [star-removal.md](user-guide/star-removal.md)
- **On-device model downloader** — pull ready-made `.onnx` models onto the device
  from the bundled SourceForge catalogue or a custom bucket, with SHA-256
  verification and no Docker or Python on the host (Settings → AI inference →
  Download models).
- **In-browser inference** — onnxruntime-web uses the client device's GPU
  (WebGPU) or WASM SIMD; the server just hosts the `.onnx` files.
  → [client-side-compute.md](user-guide/client-side-compute.md)
- **Server-side CLI** fallback (GraXpert) when you prefer the host to do it.
  → [graxpert-setup.md](graxpert-setup.md)
- **NPU acceleration** — on Rockchip RK3588 boards (RKNPU2, e.g. Orange Pi 5
  Pro) and Qualcomm Hexagon boards (QAIRT, e.g. Radxa Dragon Q6A), BGE and
  denoise run on the NPU about 5x faster than the CPU, freeing cores for live
  stacking; automatic, with a CPU fallback.
  → [npu-acceleration.md](user-guide/npu-acceleration.md)
- **Accelerator selector** — choose Auto / NPU / GPU / CPU for the GraXpert AI
  ops; on boards with more than one accelerator a Vulkan GPU lane (via ncnn)
  runs alongside the NPU.
- **OpenCL GPU backend** — image math can offload to the SBC GPU where
  available.
- **RGB FITS support** throughout the AI pipelines.

---

## FILES — File Explorer

Server-side file browser. → [files.md](user-guide/files.md)

- Browse, download, and manage captured frames and outputs on the host.
- Upload images into Studio/Editor as an entry point for processing.

---

## SETTINGS

Global configuration, host management, and connectivity.

- **Profiles & location** — observer location (with a first-run setup
  modal), units, and app-wide preferences.
- **Appearance** — theme and font picker.
- **Network mode (Hotspot ↔ Station)** — ASIAIR-style WiFi panel. The Pi
  ships as a hotspot on first boot so you can reach it without a monitor;
  flip it onto your home WiFi with a 30-second try-and-revert safety net.
  → [network-mode.md](user-guide/network-mode.md)
- **Remote access (Relay)** — TLS-tunneled access to your rig from outside
  the LAN, with optional DuckDNS and Let's Encrypt. → [relay.md](user-guide/relay.md)
- **HTTPS setup** — one-click self-signed certificate (port 5001) so client
  devices get WebGPU + multi-thread WASM for in-browser AI.
  → [https-setup.md](user-guide/https-setup.md)
- **Authentication** — login + first-run wizard protecting the UI and API.
  → [authentication.md](user-guide/authentication.md)
- **Remote terminal** — embedded SSH terminal (xterm.js) to manage a
  headless host from the browser, with a one-click launcher for the native
  raspi-config / armbian-config tool. → [remote-terminal.md](user-guide/remote-terminal.md)
- **Clock sync** — push the browser's UTC to a Pi without an RTC.
- **Equipment simulator** — built-in fake telescope/camera/focuser/filter
  wheel that renders real stars at the simulated position, so plate solve,
  auto-focus, and live stacking all work without hardware.
  → [simulator-mode.md](user-guide/simulator-mode.md)
- **Benchmark** — synthetic workloads to compare hardware (CPU, GPU, and a
  GraXpert denoise pass on the board's NPU) with a saved results history.
  → [benchmark.md](user-guide/benchmark.md)
- **Software self-update** — on Pi/SBC `.deb` installs, a status-bar badge
  appears when a newer GitHub release exists; one click downloads the
  matching `.deb`, installs it, and reloads on the new version. No password
  needed (authorized via a scoped PolicyKit rule).
- **Version rollback** — Settings → Power → *Roll back version* lists recent
  releases and reinstalls any of them (downgrade included), reusing the same
  install path; works online or offline (relay through your phone).

---

## HELP

In-app guidance. → [README.md](user-guide/README.md)

- **Step-by-step tutorials** — capture-to-export, first-night checklist, and
  specific workflows (LRGB mono, planetary, PCC).
- **Troubleshooting accordion** and links to the full user guide.
- **Report a problem** — opens a pre-filled GitHub issue/discussion with
  client + server diagnostics attached.

---

## Always-on UI

Features present across every panel.

- **Top status bar** — connection badges (INDI / Alpaca / PHD2), camera
  temperature, live-stack frame count, client-device battery, clock, guider
  dither/settle state, a global sequence-abort button, the update badge, and
  the debug-log badge.
- **Activity / transfer chips** — live network throughput, in-flight
  uploads/downloads, image-frame and exposure progress.
- **Debug log** — every server log, HTTP request, toast, and browser
  exception collected in one panel, filterable and exportable as JSONL for
  bug reports. → [debug-logging.md](user-guide/debug-logging.md)
- **Multi-instance tabs** — manage several Polaris hosts from one browser.
- **Dismissable health chips** — e.g. Raspberry Pi undervoltage warnings.

---

## Platform & Deployment

- **One-command `.deb` install** — for Debian/Ubuntu on arm64 SBCs (Raspberry
  Pi, Orange Pi, Radxa) and on x86-64 PCs; the package handles every
  dependency, the systemd unit, indi-web, and user setup.
  → [raspberry-pi-setup.md](user-guide/raspberry-pi-setup.md)
- **Windows and Linux** hosts both supported. → [installation.md](user-guide/installation.md)
- **Docker image + compose** for containerized deployment.
- **Plate-solving** via ASTAP and other solver backends.
- **Plugin system** for extending the app.

---

*For the full, control-by-control reference, see the
[User Guide](user-guide/README.md).*
