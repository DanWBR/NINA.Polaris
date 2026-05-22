# N.I.N.A. Polaris — User Guide

Welcome. N.I.N.A. Polaris is a browser-controlled astrophotography
controller running on a Raspberry Pi, mini-PC, or any small Linux/Windows
host on your network. You point any device (laptop, tablet, phone) at
the host and get the full equipment + capture + processing workflow
without a desktop.

This guide is split into **flows** (do this to accomplish X) and
**references** (this is what every control does).

## New to Polaris? Start here

1. **[Installation](installation.md)** — get the server running on your
   Pi / mini-PC / Windows box. ~10 minutes.
2. **[First-night setup](first-night.md)** — connect your gear, run
   your first sequence. ~30 minutes. Walks through INDI/Alpaca,
   creating a rig, hooking PHD2, slew-and-center, and triggering a
   ten-frame test sequence on Vega or similar bright reference target.

## By feature

Each tab in the sidebar has its own page. Read the ones you need:

- **[RIGS](rigs.md)** — multi-rig equipment management. INDI/Alpaca/vendor
  drivers, per-role cards (Main Telescope, Camera, Mount, Focuser, Filter
  Wheel, Guidescope, Guide Camera, Rotator, Flat Panel, Dome, Weather).
- **[GUIDE (PHD2)](guide-phd2.md)** — full PHD2 integration. Connection
  + management + smart calibration + algorithm presets + embedded xpra
  GUI (Linux only).
- **[FOCUS](focus.md)** — manual stepper + V-curve auto-focus with live
  preview canvas.
- **[PREVIEW](preview.md)** — snap test shots with optional save +
  continuous stream mode.
- **[AUTORUN](autorun.md)** — simple sequence editor with end-actions.
- **[ADV](adv-sequencer.md)** — advanced tree-based sequencer (NINA-style).
- **[LIVE](live-stacking.md)** — real-time EAA stacking with auto
  re-focus / re-center triggers.
- **[VIDEO](video-planetary.md)** — planetary capture + lucky-imaging
  stack pipeline (SER format).
- **[SKY](sky-explorer.md)** — offline sky map, target search, mosaic
  planner, tonight's best altitudes.
- **[STUDIO](studio.md)** — post-processing: frame browser, master
  generation, calibration, batch stacking, debayer + BGE + NR + sharpen.
- **[FILES](files.md)** — server-side file explorer.
- **[Relay (remote access)](relay.md)** — TLS-tunneled access from
  outside your LAN.

## Reference

- **[Glossary](GLOSSARY.md)** — every astrophotography term used in this
  guide explained in two lines. Read in any order.
- **[Troubleshooting](troubleshooting.md)** — common problems + fixes.
- **[FAQ](faq.md)** — questions that come up a lot.

## A note on screenshots

Screenshots in this guide use placeholder paths like
`![](screenshots/rigs-tab-overview.png)`. They land as broken images
in the rendered Markdown — that's expected. They'll be filled in over
time as we capture good ones from real sessions. If you want to help,
the placeholder filenames are descriptive enough that you can match
them to your own captures and open a PR.

## A note on audience

This guide is written for people who have shot astrophotography
before but are new to Polaris. Concepts like HFR, dither, plate solve,
bias / dark / flat get a quick line in context and a deeper entry in
the [glossary](GLOSSARY.md) — newcomers should keep that tab open
while reading.

For complete beginners to astrophotography, supplement with the
[Photographing the Universe playlist](https://www.youtube.com/@AstroBackyard)
or any of the standard intro books. We focus on Polaris-specific
workflow here, not the underlying technique.
