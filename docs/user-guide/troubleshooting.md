# Troubleshooting

Common problems + diagnostic checklists. If your issue isn't here,
open an issue on the repo with: Polaris version, OS, INDI driver
version, what you were doing, what you expected, what happened.

## Server won't start

**`The application requires the .NET runtime 10.x`**, install:

```bash
# Linux
sudo apt install dotnet-runtime-10.0
# Or self-contained binary that bundles the runtime, see installation.md
```

**`Address already in use`**, port 5000 is taken. Either kill the
other process (`sudo lsof -i :5000`) or change Polaris's port via
`appsettings.json`:

```jsonc
{ "Kestrel": { "Endpoints": { "Http": { "Url": "http://0.0.0.0:5001" } } } }
```

**Hangs on first request**, verify `indiserver` is reachable. Polaris
doesn't require INDI on startup, but the first RIGS tab visit will
spin trying to enumerate.

## INDI not connecting

**Click Connect → spinner forever, no error**, INDI server isn't
running. Verify:

```bash
ps aux | grep indiserver       # process listed?
ss -tlnp | grep 7624           # listening on port 7624?
nc -v localhost 7624            # netcat connects?
```

Start it manually with the drivers you need:

```bash
indiserver -v indi_asi_ccd indi_eqmod_telescope
```

For permanent setup, use `indiwebmanager` (web UI for driver picking).

**Connects, but devices list is empty**, INDI server is up but no
drivers loaded. Add `-v` to indiserver + watch its output for driver
errors (often "device not present" because USB isn't plugged or
permissions denied).

**Permission denied on USB device**, your user isn't in `dialout` /
`plugdev`. Run:

```bash
sudo usermod -aG dialout,plugdev $USER
# Log out + back in
```

For udev rules per device (ZWO especially), check the vendor's installer.

## USB device crashes mid-operation (under-voltage)

**Symptoms:** focuser disconnects ("DISCONNECTED within 500ms of
move to X" in the LOG panel), filter wheel drops mid-change,
camera download fails halfway, mount communication errors only
under load. The pattern is "short / idle operations work fine,
longer / heavier operations crash the device". Often the device
even disappears from the INDI device list and has to be
re-discovered.

**Root cause:** the Raspberry Pi USB ports share a budget of
~1.2A total (Pi 4) across every connected device. When the
combined draw of camera + mount + focuser + filter wheel +
USB-to-serial adapters exceeds the budget under peak load (a
focuser running its motor while the camera is reading out a
frame), the 5V rail sags below ~4.65V. At that point the EAF /
EFW / ZWO camera firmware can't keep its USB session alive and
disconnects.

The author hit exactly this with a ZWO AM3 + ZWO EAF + ZWO EFW
+ ASI camera setup on a Pi 4: short focuser moves worked, long
moves and Reset & Home crashed the EAF every time. Plugging the
EAF (and the camera) into a powered USB hub instead of the Pi
direct fixed it on the first try.

**Confirm it's under-voltage:**

```bash
# 1. Pi's hardware voltage monitor. Any non-zero bit (especially
#    0x1, 0x10000, 0x40000, 0x50005) confirms under-voltage was
#    detected at some point since boot.
vcgencmd get_throttled
# 0x0           = OK ever since boot
# 0x50005       = under-voltage RIGHT NOW + throttled
# 0x50000       = throttled now + was under-voltage earlier
# 0x40000       = was throttled earlier (often follows UV)

# 2. Kernel log -- searches for the explicit warning the Pi
#    firmware prints when the 5V rail drops below threshold.
dmesg | grep -iE 'under-voltage|hwmon|voltage'
# "Under-voltage detected! (0x00050005)" = confirmed

# 3. If you have a USB power meter (e.g. AVHzY CT-3), measure
#    the actual current draw on the Pi's USB-C power input
#    during a long focuser move. Expect spikes well past 1.5A on
#    a loaded setup -- that's where 3A-rated PSUs save you.
```

**Fix (cheap → expensive):**

1. **Powered USB hub** (~$15-30) — best ratio of cost to fix.
   Plug your highest-current devices (EAF, cooled cameras, EFW
   if motor-driven) into the hub; leave lighter devices (USB-
   to-serial mount adapter, GPS dongle) on the Pi direct.
   Sabrent HB-UMP3, Anker 4-port powered, or any hub with its
   own 5V 2A+ wall wart.

2. **Official Raspberry Pi 5.1V 3A USB-C PSU.** The "1.2A USB
   budget" assumes the Pi's input PSU is delivering its rated
   5.1V — many generic phone chargers sag to 4.9V or less under
   load, which steals headroom from your devices. The official
   Pi PSU has tight regulation under load.

3. **Shorter / thicker USB cables.** Cable resistance drops
   real voltage at peak draw. Replace any 28 AWG / >1m cable
   with a 24 AWG / ≤1m one for the high-current devices. ZWO's
   bundled cables are usually fine; aftermarket extension
   cables are often the culprit.

4. **External power on the device.** Some EAF revisions have a
   12V aux power port that bypasses USB for the motor (USB
   carries data only). The ASI cooled cameras have a 12V DC
   barrel jack; if you were powering the cooler over USB-PD
   from the Pi (don't), switch to the barrel jack with a
   separate 12V supply.

**What software CAN'T do:** chunking long moves, settling waits,
retries — none of these fix under-voltage. They just spread the
peak draw out, which sometimes works for marginal cases and
fails when the budget is genuinely exceeded. The Polaris Reset
button on the focuser card cycles the INDI CONNECTION switch
which clears a wedged USB session (useful right after a crash to
get back to working state) but doesn't prevent the next crash.

## Plate solve fails

**`Astrometric solution failed`** in Slew & Center or Smart Calibrate
, diagnostic checklist:

1. **Are there stars in the frame?** Open the PREVIEW tab + take a
   manual exposure. If you see a uniform grey field, focus is too far
   off, manually focus until stars appear.
2. **Is the catalog hint accurate?** Plate solver uses pixel scale +
   FOV + (RA, Dec) hint to narrow the search. If your rig's focal
   length is wrong, hints are off. Check RIGS → Main Telescope
   → Focal length matches your actual setup.
3. **Is ASTAP installed + has its star database?** ASTAP needs the
   `h17`, `g17`, or `g05` star database, download from the ASTAP
   website + drop into ASTAP's data folder.
4. **Try blind solve**, Settings → Plate solver → set blind solver
   to "Astrometry.net online" + retry. Slow (~30s) but no hints needed.

## PHD2 calibration fails

**`Calibration aborted`**, the most common cause is the guide star
moved out of frame during the calibration sweep. Solutions:

1. Point closer to the celestial equator (Smart Calibrate has a "slew
   to equator" toggle that does this for you)
2. Increase `MaxStepCount` in PHD2 Brain (allow more steps per
   direction)
3. Verify your guide rate setting matches the mount's actual rate
4. Polaris's Smart Calibrate auto-computes step size based on pixel
   scale + guide rate, if you've manually overridden it to something
   too small, calibration won't reach a measurable angle

## Auto-focus moves to a wildly wrong position

V-curve parabola fit was poisoned by outlier samples (clouds, frame
without enough stars, satellite trail). Polaris validates "best within
±2 × StepSize × N/2 of starting position", outside that range
it logs a warning but still moves.

**Mitigations**:

- Bump `Min Stars` to 20+ to reject noisy samples
- Enable Backlash compensation if your focuser has hysteresis
- Pre-focus manually to within a step or two before running AF
- Increase `Steps` from 9 → 15 for a longer baseline

## Live stacking shows "frame count: 1" forever

Alignment failing on every frame after the first. Means the first
frame's reference stars can't be matched to subsequent frames.

**Causes**:

- Field rotation (you slewed during the stack), Reset + restart
- Focus drifted hard (stars became too bloated to detect), Reset +
  refocus
- First frame had unusual lighting (satellite, plane) creating false
  "stars", Reset (next first frame becomes the new reference)

Verify in `/ws/status` payload: `liveStack.referenceStarCount` should
be 50+ for a good reference. If it's 5, the first frame was bad.

## Sequence stops mid-run with no error

Check the **AUTORUN** tab, if `state = running` but `currentFrameInItem`
isn't incrementing, the camera is hung. Causes:

- USB cable popped (check INDI logs)
- Cooler ran a power-cycle reset (some cameras do this on overcurrent)
- Mount tracking off (filter wheel waiting on tracking-on state)

The sequence engine doesn't currently detect these silently, it's on
the roadmap. Workaround: Stop + Restart the sequence with the camera
reconnected.

## Meridian flip hangs

Most common cause: your mount needs an **explicit pier-side command**
to flip; just slewing back doesn't trigger the flip. EQMod-based
mounts (CGEM, AVX, EQ6-R) flip on slew; some Celestron mounts via
ASCOM don't.

Workaround: open INDI / ASCOM control panel for your mount + click
the "Flip" or "Force Pier Side: East/West" button manually mid-flip.

File an issue with your mount model, we'd like to support more.

## Embedded PHD2 GUI (xpra) shows blank iframe

Linux + xpra setup, but iframe stays blank. Diagnostic:

```bash
# Is xpra session running?
xpra list

# Can you connect locally?
curl http://localhost:14600/

# Logs from the xpra session
cat ~/.xpra/:100.log
```

Usually one of:

- Xorg-dummy config not switched (see `docs/phd2-gui-embedding.md`
  Step 2, `/etc/xpra/conf.d/55_server_x11.conf`)
- xpra password set, browser session storage doesn't have it, open
  the iframe URL in a new tab once + type the password (xpra
  remembers it in sessionStorage from there)

## STUDIO frame library is empty

**Rescan** button didn't pick up files. Causes:

- `ImageOutputDir` is wrong, check FILES tab Studio root indicator;
  re-set it from the actual folder containing `{rig}/lights/...`
- New files were saved while the rescan was running, re-click Rescan

## Live preview canvas is black

WebGL2 isn't initialized. Browser console (F12) shows shader compile
errors. Workaround: Settings → "Force JPEG mode" → server encodes
JPEGs instead. Slightly more CPU on the host but works in any browser.

## See also

- [FAQ](faq.md), quick-answer questions
- [GUIDE PHD2 troubleshooting](guide-phd2.md#common-pitfalls)
- [LIVE stacking troubleshooting](live-stacking.md#common-pitfalls)
- Per-feature pages all have a "Common pitfalls" section
