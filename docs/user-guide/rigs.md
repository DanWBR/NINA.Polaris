# RIGS tab

The RIGS tab is your equipment cockpit. It centralizes:

1. **Driver-host connection**, INDI or Alpaca, always visible at top
2. **Per-role equipment cards**, Main Telescope, Camera, Mount,
   Focuser, Filter Wheel, Guidescope, Guide Camera, Guide Focuser,
   Aux Camera + Aux Focuser, plus collapsible Accessories (Rotator,
   Flat Panel, Dome, Weather)
3. **Multi-rig management**, switch between saved equipment bundles

## Connection strip (top of the tab)

When INDI/Alpaca is **not connected**, the strip expands to show:

- **INDI tab**: Host + Port inputs + Connect button (default
  `localhost:7624`)
- **ASCOM/Alpaca tab**: Discover button + manual host/port for NAT'd
  servers + per-server device list

When **connected**, the strip collapses to a green compact bar showing
`✓ INDI · localhost:7624 · N devices`, with ⟳ Refresh and Disconnect
buttons.

## Role cards

Each card has the same skeleton:

- **Header**: icon + role label + status dot (gray = no selection,
  amber = selected but not connected, green = connected)
- **Body**: device dropdown + Connect / Disconnect buttons + role-
  specific controls

### Main Telescope (metadata-only)

No hardware connection, purely optical specs. Drives FOV calculation
+ FITS `FOCALLEN` header + plate-solve hints.

Two ways to populate:

**A. Catalog pickers** (preferred):
- **Brand**: dropdown of curated OTAs (Askar, Celestron, Sky-Watcher,
  GSO, Meade, SVBony, Explore Scientific, Astro-Physics, ...)
- **Model**: filtered by brand, shows aperture + f-ratio
- **Accessory**: reducers / flatteners / Barlows / extenders compatible
  with the picked OTA, auto-applies the focal-length multiplier

**B. Manual entry**: leave Brand = "Manual entry" and fill the numeric
inputs (Focal length, Aperture, Factor) by hand.

Both paths populate the same persisted fields on the active rig.

### Camera

Driver dropdown shows INDI/Alpaca + (Windows) Canon EDSDK / Nikon /
Sony SDK if installed. Connect → temperature chart, cooler target
input, gain/binning quick controls. Cooler-power chart bottom-left
when a cooled sensor is active.

Sensor dimensions auto-detected from the driver, no manual entry
needed (this used to be a Settings field; we removed it).

**Gain vs ISO**: dedicated astronomy cameras expose an analogue
**gain** number; the field carries a **(?)** helper that explains gain
in ISO terms (higher gain = brighter + lower read noise, but less
dynamic range). **DSLR / mirrorless** bodies (INDI gphoto on Linux)
report **ISO** instead — when the driver publishes a CCD_ISO list,
Polaris shows an **ISO dropdown** in the capture controls in place of
the numeric gain box. See [DSLR on Linux](../dslr-linux.md).

### Mount

Driver dropdown: INDI Telescope, Alpaca Telescope, or one of the
direct-WiFi drivers Polaris ships (SynScan WiFi, NexStar WiFi, LX200
TCP). Connect → tracking toggle, park/unpark, RA/Dec readout, NSEW
directional pad.

### Focuser / Filter Wheel

Standard select + connect. Filter Wheel exposes filter swap controls;
filter labels come from the rig's `FilterOffsets` table (Manage rigs
modal).

### Guidescope (metadata-only)

Like Main Telescope but for the guide setup. Focal length + aperture
drive PHD2 pixel-scale sanity checks + the guiding resolution readout.

### Guide Camera (read-only)

Polaris doesn't manage this directly, PHD2 owns it. The card mirrors
PHD2's `get_current_equipment` so you can see at a glance what guide
cam PHD2 is using.

### Guide Focuser

An optional motor on the **guide scope** (some setups motorise it).
Driver + device picker + connect toggle, same shape as the main
focuser. Once connected it can be jogged from the FOCUS tab via the
**Focuser: Guide** source switch, and auto-focused with the Auto
V-curve **Optical train: Guide** option (which uses the guide camera).
See [FOCUS → aux/guide focusing](focus.md#focusing-the-aux--guide-scope).

### Aux Camera + Aux Focuser

A **second imaging camera** riding the same mount through a different
lens/telescope, captured in parallel to make use of the same tracked
night. The aux card carries:

- **Driver + device** picker (INDI / vendor SDK / Alpaca, like the main
  camera), connect/disconnect, and live status.
- **Focal length / aperture / brand / model** for the aux optical train
  (its own values, used for the FITS `FOCALLEN` of aux frames).
- **Exposure / gain / binning** — the aux loop runs on its **own
  cadence**, independent of the main camera.
- **Enable aux capture** toggle — when on, the aux loop captures + saves
  frames automatically whenever a main session (LIVE or AUTORUN) is
  running. It pauses while the mount is busy (dither / settle / meridian
  flip / slew) so trailed frames aren't saved.
- **Aux Focuser** picker — an optional focuser for the aux train, for
  manual focusing (and Auto V-curve via **Optical train: Auxiliary**).

Aux frames are written to a **separate `aux/` subtree**
(`{rig}/aux/{target}/{filter}/{session}/`) so they never mix with the
main camera's `lights/`. The aux camera is also viewable in the FOCUS
tab via the **Camera: Auxiliary** source switch. Capture + save is all
the aux camera does today — no guiding, plate solving, live stacking or
sequencing through it.

### Accessories (collapsible)

`<details>` block below the main grid. Auto-expands when at least one
accessory has a saved selection.

- **Rotator**: angle readout + slew, sync
- **Flat Panel**: light toggle + brightness slider (where supported)
- **Dome**: azimuth, shutter, park, slave-to-scope toggle
- **Weather**: read-only sensor display (cloud, humidity, dewpoint,
  wind, sky temp, MPSAS)

## Rig management

**Rig dropdown** (top of tab): switch active rig in one click. All
device selections + per-rig defaults reload automatically.

**💾 Save selections**: persists the current dropdown picks + cooler
target + focuser step into the active rig.

**Manage rigs…** opens a modal:

- Inline rename per rig
- Per-rig devices summary (📷 Camera · 🔭 Mount · 🔍 Focuser · ⚙ Filter Wheel)
- Per-rig optics summary (focal length, f-ratio, accessory)
- Per-rig **filter offsets** (collapsible), `{Filter → ΔSteps}` table
  used by the `MoveToFilterOffsetInstruction` in sequences
- Activate / Delete buttons per rig
- "New empty rig" + "Duplicate active" at the footer

The modal is intentionally slim, device pickers + optics live on the
RIGS-tab cards now, no longer duplicated here. Modal is for rig
lifecycle + filter offsets only.

## Per-rig persisted fields

Beyond the obvious device names, each rig stores:

- **Cooler target temperature** (°C)
- **Default gain / offset / binning**
- **Focuser step size + backlash**
- **Main scope** focal length + aperture + brand + model + accessory + factor + required back-focus
- **Guide scope** focal length + aperture + brand + model
- **Guide focuser** device + driver
- **Aux camera** device + driver, aux optics (focal length + aperture +
  brand + model), aux exposure / gain / binning, enable flag, and the
  **aux focuser** device + driver
- **PHD2** endpoint (host + port), profile id cache, algo preset, calibration step override, custom algo params
- **Filter offsets** table
- **Live-stack triggers** (refocus + recenter policy, see [LIVE](live-stacking.md))

## Telescope + accessory catalog

The dropdowns are driven by `wwwroot/data/telescopes.json` +
`wwwroot/data/optical-accessories.json`. Both are checked in to the
repo, to add a new OTA / reducer, edit the JSON, restart the server,
refresh the browser. Pull requests with additions for popular new
hardware are welcome.

## INDI control panel (property browser)

The **INDI control panel** sub-tab is a built-in replacement for the
old standalone `indi_control_panel` Qt app, which recent Raspberry Pi
OS / libindi 2.x releases no longer ship. It shows every property each
connected device exposes, grouped per device, and lets you read and
edit them right in the browser.

- Properties are grouped (Main, Options, Site, ...) and searchable.
  Number, switch, text and light types each get the right editor;
  read-only properties show greyed out.
- Edits are sent through the same path the rest of the app uses and
  then auto-saved to the driver's `~/.indi/*_config.xml`, so they
  come back on the next connect.
- **Refresh / Resync** at the top re-reads the property list (use
  Resync after loading or unloading a driver in the INDI Web
  manager).

### Property descriptions (the "?" help icon)

The INDI protocol does not include a description for each property,
only a short label. To make the cryptic names friendlier, every
property has a small **(?)** icon next to its name:

- **Hover** the (?) to read a plain-language English explanation as a
  tooltip. Around 80 common INDI standard properties (camera, mount,
  focuser, filter wheel, dome, rotator, weather, plus the general
  ones) ship with a built-in description.
- **Click** the (?) to open a small editor. It shows the built-in
  description and gives you a box to write your own note. Your note
  is saved and shown instead of the built-in text from then on.
- Notes are saved per property name, not per device, so a note you
  write on (for example) `CCD_TEMPERATURE` shows up for every camera
  and survives reconnects. Use **Clear note** to go back to the
  built-in description.

Built-in descriptions live in
`wwwroot/data/indi-property-help.json`; your own notes live in the
profile, so a [factory reset](#) clears them along with the rest of
your settings.

## Common pitfalls

**Cards show empty dropdowns even after INDI connects**, INDI hasn't
finished enumerating devices yet. Click ⟳ Refresh in the connection
strip, or wait 1-2 seconds.

**Camera connects but sensor dimensions are 0×0**, driver doesn't
populate `CCD_INFO` until first exposure. Take a 0.1s snap from the
PREVIEW tab and the dimensions populate.

**Switching rigs doesn't disconnect old devices**, by design.
Disconnect manually before swapping setups (otherwise INDI ends up
with multiple devices "connected" to the same hardware).

## See also

- [GUIDE (PHD2)](guide-phd2.md), adjacent tab for autoguiding
- [Settings](#), observatory location, image output dir, theme
- [Glossary → Rig](GLOSSARY.md#r)
