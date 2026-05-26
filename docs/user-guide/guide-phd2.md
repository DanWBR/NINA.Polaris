# GUIDE tab (PHD2)

PHD2 is a first-class managed device in Polaris, we control 100% of
the runtime via JSON-RPC, and Linux users can interact with PHD2's
native GUI (Wizard, Brain dialog, Guiding Assistant) embedded inside
the Polaris tab via xpra.

The GUIDE tab has two sub-tabs:

- **Control**, JSON-RPC UI: profile switcher, exposure, dec mode,
  equipment connect, guiding controls, RMS chart, Smart Calibrate,
  algorithm presets. Works on every OS.
- **PHD2 GUI**, embedded xpra HTML5 client iframe (Linux only). See
  [docs/phd2-gui-embedding.md](../phd2-gui-embedding.md) for the
  Xorg-dummy setup.

## Control tab walkthrough

### Connection

- **PHD2 not installed**: a download banner shows up with a link to
  the official PHD2 page. Install + restart the server (or set
  `PHD2:ExecutablePath` in `appsettings.json` if your binary is in an
  unusual place).
- **Manually launch**: click **▶ Launch PHD2** when the executable is
  auto-detected. Polaris spawns PHD2 + waits for its event server on
  TCP/4400 (loopback only).
- **Auto-start on boot**: checkbox below, persists in the profile, so
  every Polaris startup launches PHD2 ~2s after server start. Backed
  by `PHD2AutoStartService`.

Once PHD2 is up, the **Connect** button wires Polaris's JSON-RPC client
to it. Live status appears in the status bar header (PHD2 = ON / OFF).

### Profile + equipment

- **Profile dropdown** lists PHD2's profiles (read from `get_profiles`)
- Switching profiles auto-disconnects equipment (PHD2 requires it)
- **▶ Connect equipment** tells PHD2 to wire up the gear configured in
  the active profile
- **Current equipment** display shows guide cam + mount + aux mount +
  AO so you know what PHD2 thinks it's using

### Exposure + dec mode

- **Exposure dropdown** is populated from PHD2's `get_exposure_durations`
- **Dec mode**: Auto / North / South / Off, passed to `set_dec_guide_mode`

### Guiding controls

Standard PHD2 commands:

- **▶ Guide**, start guiding with settle params (settle pixels, time,
  timeout)
- **▶ Loop**, exposure cycling without guiding (find star, focus)
- **⏸ Pause / ▶ Resume**, keeps the loop running but suspends pulse
  output
- **⏹ Stop**, stop everything
- **Dither**, manually trigger a dither
- **★ Auto-select star**, `find_star`
- **Clear calibration** + **Clear history**, maintenance

### Live RA/Dec chart

Chart.js plot of the last 60 GuideSteps (1Hz sample), auto-scaling
Y-axis. RMS RA / RMS Dec / RMS total / peak RA / peak Dec readouts
update each second.

### Settle parameters

- **Settle pixels** (default 1.5), guiding is "settled" when peak
  error stays below this for `Settle time` seconds
- **Settle time** (default 10s)
- **Settle timeout** (default 40s), give-up threshold

Applied to all Guide / Dither commands.

### Smart Calibrate

This is the killer feature for cold-start calibration. Instead of
opening PHD2's Calibration Wizard, Polaris computes everything for
you:

1. Click **🎯 Smart Calibrate**
2. Modal opens with options:
   - **Slew to equator** (optional), Polaris commands the main mount
     to LST + Dec 0° before calibrating (best calibration accuracy is
     near the celestial equator)
   - **Step size override**, leave blank to auto-compute from pixel
     scale + guide rate; manual override available
   - **Timeout**, default 240s
3. Polaris runs a 9-phase pipeline:
   `Preflight → PixelScale → ComputeStep → Slewing → ApplyStep → Calibrating → Validating → Ok/Fail`
4. Live progress chips in the status bar; result appears in the toast
5. On success, calibration is verified for orthogonality (XAngle ⊥
   YAngle within 20°) + non-zero rate

Behind the scenes: the pixel scale comes from PHD2's `get_pixel_scale`
(or fallback compute from rig's guider focal length); guide rate is
read from the mount (or 7.5"/s = 0.5× sidereal default); step ms =
`25px × pxScale / guideRate × 1000` clamped to `[250, 3000]`.

### Algorithm presets

Three curated bundles applied via `set_algo_param`:

- **Default**, PHD2's stock values. Balanced.
- **Reactive**, higher aggressiveness, lower hysteresis. Good for short
  focal lengths, good seeing, fast mounts. Risk: overshoot.
- **Smooth**, gentler corrections, higher hysteresis + min-move. For
  long focal lengths or windy/poor seeing.
- **Custom**, sentinel; auto-set when you edit any knob in the
  Advanced disclosure. Persists the override bag on the rig.

Click a preset pill to apply + persist. The Advanced `<details>`
section below lets you tune individual knobs (Hysteresis,
Aggressiveness, MinMove, FastSwitch per axis) and saves whichever you
touch as a Custom preset.

### Profile sync indicator

Top-right of the tabstrip. Reflects the live status of
`PHD2ProfileSyncService`:

- **✓ Profile synced**, rig name matches a PHD2 profile + it's the active one
- **⚠ Profile missing**, no PHD2 profile with this rig's name. Open
  the PHD2 GUI tab + create one via Wizard, then click ⟳ to retry.
- **⚠ Sync error**, PHD2 returned an error during the switch
- **↻ Syncing…**, currently flipping profile + applying preset

By default (`PHD2AutoSyncOnRigSwitch = true`), switching rigs in
Polaris triggers an automatic PHD2 profile switch + preset apply.

## PHD2 GUI tab (Linux only)

Embedded xpra HTML5 client showing PHD2's native window inside the
Polaris UI. Lets you do everything PHD2's GUI offers without VNC/SSH:

- Run the Profile Wizard
- Open the Brain dialog (fine-tune anything)
- Run the Guiding Assistant
- Manage dark libraries
- Adjust gamma + display options

States:

1. **Platform unsupported**, banner explains the limitation. Two
   variants:
   - **Non-Linux host** (Windows / macOS), open PHD2's native window
     on that machine directly.
   - **32-bit ARM** (Raspberry Pi 2 / 3 with 32-bit Raspberry Pi OS),
     xpra installs from apt but its session-start crashes; the dummy
     Xorg driver is unreliable on ARMv7. Upgrade to 64-bit Pi OS on
     a Pi 4 / 5, or run PHD2 on a separate machine and point Polaris
     at it.
2. **Linux without xpra**, install instructions with `sudo apt
   install xpra xserver-xorg-video-dummy`
3. **Linux + xpra installed but session not running**, "▶ Start PHD2
   GUI session" button (~5-10s spin-up)
4. **Session running**, iframe + small toolbar (Restart, Stop,
   fullscreen)

Auto-start at Polaris boot is opt-in via `Phd2Gui:AutoStart` in
`appsettings.json` (uses ~150MB RAM constantly).

## Common pitfalls

**Smart Calibrate fails with "PHD2 reports not calibrated after
Guiding state, unexpected"**, usually a guide star couldn't be
locked. Lengthen `find_star` exposure or pick a denser field manually.

**Profile switch hangs at "switching"**, PHD2 is busy disconnecting
equipment. Wait 30s; it will resolve. If stuck longer, click ⟳ Refresh
in the Control tab connection panel.

**xpra iframe shows "session not running" right after Start**, first
launch takes 5-10s for Xorg-dummy to come up. Wait, then refresh.
If it persists, check `/etc/xpra/conf.d/55_server_x11.conf` for the
Xorg-dummy switchover.

## See also

- [docs/phd2-gui-embedding.md](../phd2-gui-embedding.md), full xpra
  install procedure for Linux hosts
- [Glossary → PHD2 / Calibration / Dither](GLOSSARY.md#p)
