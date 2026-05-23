# Embedded PHD2 GUI (Linux only)

Polaris embeds **PHD2's native GUI** inside the GUIDE tab so you can do
everything remotely — including the parts PHD2's JSON-RPC API doesn't
expose: profile creation via the Wizard, the Brain dialog, Guiding
Assistant, dark library management, equipment picker, custom algorithm
selection.

This is **Linux-only** on the server side, and **64-bit Linux only**.
Polaris achieves the embed by running PHD2 inside an [xpra](https://xpra.org)
session with an Xorg-dummy virtual display, then reverse-proxying xpra's
HTML5 client through `/phd2-gui/*`. On Windows/macOS, the GUIDE tab still
gives you the full JSON-RPC controls (profile switching, exposure, smart
calibrate, algorithm presets) — for the rare cases needing PHD2's GUI,
open PHD2 directly on that machine.

### Not supported on 32-bit ARM (Raspberry Pi 2 / 3 with 32-bit Pi OS)

xpra installs from apt on Raspberry Pi OS 32-bit (ARMv7), but session-start
crashes — the dummy Xorg driver is unreliable on 32-bit ARM and several
Python/GTK dependencies misbehave. Polaris detects this at startup and
disables the "PHD2 GUI" panel with a clear message instead of letting you
hit a confusing process-died log.

If you're on a Pi 2 or Pi 3, your options are:

- Upgrade to **64-bit Raspberry Pi OS** on a Pi 4 / Pi 5 (recommended —
  xpra works fine there).
- Run PHD2 on a separate Windows/Linux machine on the LAN and point
  Polaris at it via the **PHD2 host/port** setting in the GUIDE tab.
- Use X11 forwarding / VNC to reach PHD2's native window directly.

The full JSON-RPC control surface in the GUIDE tab still works on 32-bit
ARM — only the embedded GUI window is unavailable.

## Install (Raspberry Pi, Debian, Ubuntu)

Procedure adapted from the [PHD2 maintainer recommendation](https://github.com/OpenPHDGuiding/phd2/issues/683#issuecomment-3707310067).

### 1. Install xpra + Xorg-dummy

```bash
sudo apt update
sudo apt install \
    xpra \
    xserver-xorg-video-dummy \
    xserver-xorg-input-libinput \
    xserver-xorg-input-all \
    xserver-xorg-core
```

Polaris probes `xpra --version` on startup and lights up the GUIDE tab's
"PHD2 GUI" panel automatically when xpra is detected.

### 2. Configure xpra to use Xorg-dummy (not Xvfb)

PHD2 is a wxWidgets app, not GTK. It runs reliably under Xorg-dummy but
has glitches under the Xvfb default. Edit
`/etc/xpra/conf.d/55_server_x11.conf`:

```bash
sudo nano /etc/xpra/conf.d/55_server_x11.conf
```

Find the existing `xvfb = ...` block (the last lines of the file) and
**comment out** the Xvfb stanza, then **uncomment** the Xorg-dummy stanza:

```conf
# xvfb = Xvfb -screen 0 8192x4096x24 +extension GLX \
#     +extension RANDR +extension RENDER +extension Composite \
#     -extension DOUBLE-BUFFER -nolisten tcp -noreset \
#     -auth $XAUTHORITY

xvfb = /usr/lib/xorg/Xorg -novtswitch \
    -logfile ${XPRA_SESSION_DIR}/Xorg.log \
    -configdir ${XPRA_SESSION_DIR}/xorg.conf.d/$PID \
    -config ${XORG_CONFIG_PREFIX}/etc/xpra/xorg.conf \
    +extension GLX +extension RANDR +extension RENDER \
    +extension Composite -extension DOUBLE-BUFFER \
    -nolisten tcp -noreset -auth $XAUTHORITY
```

### 3. Verify PHD2 is reachable

```bash
which phd2     # should print /usr/bin/phd2 or similar
phd2 --version # should print 2.6.xx or newer
```

If PHD2 isn't installed yet, follow the
[PHD2 Linux build guide](https://github.com/OpenPHDGuiding/phd2/wiki/BuildingPHD2OnLinux)
or `sudo apt install phd2`.

### 4. Restart Polaris and open GUIDE → "PHD2 GUI" tab

1. Restart the Polaris server.
2. In the web UI, click **GUIDE** in the sidebar.
3. Click the **PHD2 GUI** tab.
4. Click **▶ Start PHD2 GUI session** — Polaris spawns xpra + PHD2.
5. After ~5–10 seconds the PHD2 native UI renders inside the tab.

From there you can run the Profile Wizard, Brain dialog, Guiding
Assistant, etc. as if you were sitting at the Pi's monitor — except
there's no monitor.

## Lifecycle

By default the xpra session starts **on demand** — first time you open
the PHD2 GUI tab. To pre-start at Polaris boot (so the iframe loads
instantly), set in `appsettings.json`:

```jsonc
{
  "Phd2Gui": {
    "AutoStart": true,
    "DisplayNumber": 100,
    "BindPort": 14600
  }
}
```

- `DisplayNumber`: X display number passed to xpra (default `:100`)
- `BindPort`: TCP port xpra listens on, localhost-only (default 14600).
  Polaris reverse-proxies `/phd2-gui/*` to this port. Never expose it
  to the network directly.

## How it works

1. **xpra session**: Polaris's `Phd2GuiSessionService` spawns
   `xpra start :100 --start=phd2 --html=on --bind-tcp=127.0.0.1:14600`.
2. **Reverse proxy**: ASP.NET Core forwards `/phd2-gui/*` → `ws://127.0.0.1:14600/*`
   using YARP's `IHttpForwarder` (HTTP + WebSocket upgrade).
3. **Same-origin iframe**: the GUIDE tab embeds `<iframe src="/phd2-gui/">`.
   Because the iframe path is under the Polaris origin, `sessionStorage`
   works (xpra-html5 needs it) and your Polaris auth (Relay tokens or
   LAN) covers PHD2 GUI access too.
4. **Health probe**: every 15 seconds Polaris TCP-pings 14600 and
   surfaces session state via `/ws/status` → `guider.guiSession.*`.

## Troubleshooting

### "xpra start exited 1: cannot open display"

Xorg-dummy isn't configured. Re-verify step 2 above (`55_server_x11.conf`).

### Iframe loads but is blank / shows xpra connect dialog

xpra's HTML5 client expects no password by default. If you've added one
via xpra's auth options, the iframe can't supply it automatically. Either
remove the password (Polaris's outer auth is sufficient since the port
is localhost-only) or open `/phd2-gui/` in a new tab to type the password
once — xpra stores it in sessionStorage from there.

### PHD2 crashes inside the session

The xpra session stays alive even when PHD2 crashes — just nothing's
rendered. Click **↻ Restart** in the toolbar above the iframe to relaunch
PHD2 inside the same session. (Future improvement: detect + auto-relaunch.)

### High CPU / bandwidth on Raspberry Pi

xpra's default refresh rate handles PHD2 fine on Pi 5. On Pi 4 with a
heavily-loaded session you can reduce frame rate by editing
`/etc/xpra/conf.d/16_client.conf` — add `framerate=10`.

The PHD2 GUI tab is meant for **setup and tuning**, not continuous
monitoring during sequences. Use the **Control** tab (JSON-RPC stats +
guide-step chart) for live ops — it has negligible bandwidth.

### Windows / macOS

The PHD2 GUI tab shows a banner explaining the Linux-only requirement.
Use the **Control** tab (which works on all OSes) for everything Polaris
can drive via JSON-RPC: profile switching, exposure, dec mode, guiding,
dithering, **Smart Calibrate** (auto-computes step size, optionally
slews to the equator, monitors calibration to completion), and the
**algorithm presets** (Default / Reactive / Smooth + Advanced disclosure
with every algorithm knob).

For the GUI-only operations (Profile Wizard, Brain, GA), open PHD2's
own window directly on the Windows/macOS machine.
