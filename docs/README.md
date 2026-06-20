# N.I.N.A. Polaris — Documentation

Everything is organized here. New users want the **User Guide**; integrators
want the **API & Configuration reference**; the rest are focused setup notes
and overviews.

## Start here

- **[User Guide](user-guide/README.md)** — the main manual. Install → first
  night → end-to-end workflow, plus a reference page for every sidebar tab.
- **[Feature overview](FEATURES.md)** — what each tab does, at a glance.

## Reference

- **[API & Configuration](api-reference.md)** — REST endpoints, WebSocket
  streams, `appsettings.json`, environment variables.
- **[Requirements matrix](../REQUIREMENTS.md)** — required + optional
  dependencies per platform (Windows / Linux ARM / Linux x64).
- **[Architecture](../ARCHITECTURE.md)** · **[Contributing](../CONTRIBUTING.md)**
- **[NINA-Manual.pdf](NINA-Manual.pdf)** — the upstream N.I.N.A. desktop
  manual, kept as a FITS-header / terminology reference.

## Setup guides (hardware & external tools)

- **[Raspberry Pi 4 / 5 setup](user-guide/raspberry-pi-setup.md)** — blank
  SD card to auto-start systemd unit.
- **DSLR / mirrorless cameras** —
  [Canon (Windows)](dslr-windows-canon.md) ·
  [Nikon (Windows)](dslr-windows-nikon.md) ·
  [Sony (Windows)](dslr-windows-sony.md) ·
  [Linux (gphoto/INDI)](dslr-linux.md)
- **[Mounts & WiFi accessories](mounts-wifi.md)** — direct-TCP mount drivers
  and WiFi-only accessory notes.
- **[GraXpert setup](graxpert-setup.md)** — AI background/denoise/decon models
  and the CLI fallback.
- **[Siril setup](siril-setup.md)** — optional external pre-processing /
  stacking integration.
- **PHD2 GUI embedding** — [Linux (xpra)](phd2-gui-embedding.md) ·
  [Windows (VNC)](phd2-gui-windows.md)

## User-guide pages by area

Equipment & rigs: [RIGS](user-guide/rigs.md) ·
[ASCOM (COM)](user-guide/ascom-com.md) ·
[Native camera SDKs](user-guide/native-camera-sdk.md) ·
[Simulator mode](user-guide/simulator-mode.md) ·
[INDI web manager](user-guide/indi-web.md)

Acquisition: [Preview](user-guide/preview.md) ·
[Focus](user-guide/focus.md) ·
[Guide (native)](user-guide/guide-native.md) ·
[Guide (PHD2)](user-guide/guide-phd2.md) ·
[Polar alignment](user-guide/polar-alignment-rudimentary.md) ·
[Sky Explorer](user-guide/sky-explorer.md)

Automation: [AUTORUN](user-guide/autorun.md) ·
[PLAN (night planner)](user-guide/plan.md) ·
[Advanced Sequencer](user-guide/adv-sequencer.md) ·
[Flat Wizard](user-guide/flat-wizard.md)

Imaging & EAA: [Live stacking](user-guide/live-stacking.md) ·
[Video / planetary](user-guide/video-planetary.md) ·
[Client-side compute](user-guide/client-side-compute.md) ·
[NPU acceleration](user-guide/npu-acceleration.md)

Processing: [Studio](user-guide/studio.md) ·
[Editor](user-guide/editor.md) ·
[Color calibration](user-guide/color-calibration.md) ·
[Star removal](user-guide/star-removal.md) ·
[Image Blend](user-guide/image-blend.md) ·
[ONNX inference](user-guide/onnx-inference.md) ·
[Files](user-guide/files.md) ·
[End-to-end workflow](user-guide/end-to-end-workflow.md) ·
[Mono / LRGB workflow](user-guide/lrgb-mono-workflow.md)

Access & ops: [Network mode](user-guide/network-mode.md) ·
[Relay](user-guide/relay.md) ·
[HTTPS setup](user-guide/https-setup.md) ·
[Authentication](user-guide/authentication.md) ·
[Remote terminal](user-guide/remote-terminal.md) ·
[Self-update](user-guide/self-update.md) ·
[Debug logging](user-guide/debug-logging.md) ·
[Benchmark](user-guide/benchmark.md) ·
[Sensor analysis](user-guide/sensor-analysis.md)

Help: [FAQ](user-guide/faq.md) ·
[Troubleshooting](user-guide/troubleshooting.md) ·
[Glossary](user-guide/GLOSSARY.md)

Developer notes: [RPi debug from VS](user-guide/rpi-debug-from-vs.md)
