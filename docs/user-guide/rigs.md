# RIGS tab

The RIGS tab is your equipment cockpit. It centralizes:

1. **Driver-host connection**, INDI or Alpaca, in a framed panel above
   the sub-tabstrip (visible on every sub-tab)
2. **Per-role equipment cards**, Main Telescope, Camera, Mount,
   Main Scope Focus Motor, Filter Wheel, Guiding System (Scope +
   Camera + Focus Motor), Auxiliary Camera System (Camera + Lens/Scope
   + Focus Motor), plus collapsible Accessories (Rotator, Flat Panel,
   Dome, Weather)
3. **Multi-rig management**, switch between saved equipment bundles

## Connection panel (top of the tab)

The driver-host connection lives in a framed panel **above the
Equipment / INDI Drivers / INDI Control Panel sub-tabstrip**, so it
stays visible on every sub-tab (all three need a live connection).

When INDI/Alpaca is **not connected**, the panel shows:

- **INDI tab**: Host + Port inputs + Connect button (default
  `localhost:7624`)
- **ASCOM/Alpaca tab**: Discover button + manual host/port for NAT'd
  servers + per-server device list

When **connected**, the panel collapses to a green compact bar showing
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
report **ISO** instead - when the driver publishes a CCD_ISO list,
Polaris shows an **ISO dropdown** in the capture controls in place of
the numeric gain box. See [DSLR on Linux](../dslr-linux.md).

### Mount

Driver dropdown: INDI Telescope, Alpaca Telescope, or one of the
direct-WiFi drivers Polaris ships (SynScan WiFi, NexStar WiFi, LX200
TCP). Connect → tracking toggle, park/unpark, RA/Dec readout, NSEW
directional pad.

### Main Scope Focus Motor / Filter Wheel

Standard select + connect (the focuser card is named after the optical
train it drives - the **Main Scope Focus Motor**). Filter Wheel exposes
filter swap controls; filter labels come from the rig's `FilterOffsets`
table (Manage rigs modal).

### Guiding System

One card for the whole guide setup, split into three labelled groups:

**Scope** (metadata-only) - like Main Telescope but for the guide
optics. Focal length + aperture drive PHD2 pixel-scale sanity checks +
the guiding resolution readout. Two ways to populate:

- **Catalog pickers** (preferred): **Brand** → **Model** dropdowns
  driven by `wwwroot/data/guidescopes.json` (curated common guide
  scopes: SVBony, ZWO, William Optics, Askar, Sky-Watcher, Orion,
  QHY, ...). The model list shows aperture + f-ratio; picking one
  auto-fills the guide focal length + aperture.
- **Manual entry**: leave Brand = "Manual entry" and type the numeric
  focal length + aperture by hand (for off-catalog scopes). Guide
  scopes take no accessory/reducer, so there is no accessory picker.

**Camera** - behaviour depends on the rig's **Guider driver**
(`native` vs `phd2`, set on this card):

- **Native guider** (default): Polaris manages the guide camera
  directly. Driver + device picker (INDI / Alpaca / vendor SDK /
  Simulator) and a connect toggle, just like the imaging camera. The
  built-in [native autoguider](guide-native.md) auto-connects and uses
  it for pulse guiding. The guide camera must differ from the imaging
  camera while that is connected.
- **PHD2**: an external PHD2 process owns the camera. The card then
  mirrors PHD2's `get_current_equipment` (read-only) so you can see at
  a glance which guide cam PHD2 is using.

**Focus Motor** - an optional motor on the **guide scope** (some setups
motorise it). Driver + device picker + connect toggle. Once connected
it can be jogged from the FOCUS tab via the **Focuser: Guide** source
switch, and auto-focused with the Auto V-curve **Optical train: Guide**
option (which uses the guide camera). See
[FOCUS → aux/guide focusing](focus.md#focusing-the-aux--guide-scope).

### Auxiliary Camera System

A **second imaging camera** riding the same mount through a different
lens/telescope, captured in parallel to make use of the same tracked
night. The card is split into **Camera + Lens/Scope** and **Focus
Motor** groups, and carries:

- **Driver + device** picker (INDI / vendor SDK / Alpaca, like the main
  camera), connect/disconnect, and live status.
- **Focal length / aperture / brand / model** for the aux optical train
  (its own values, used for the FITS `FOCALLEN` of aux frames).
- **Exposure / gain / binning** - the aux loop runs on its **own
  cadence**, independent of the main camera.
- **Enable aux capture** toggle - when on, the aux loop captures + saves
  frames automatically whenever a main session (LIVE or AUTORUN) is
  running. It pauses while the mount is busy (dither / settle / meridian
  flip / slew) so trailed frames aren't saved.
- **Aux Focuser** picker - an optional focuser for the aux train, for
  manual focusing (and Auto V-curve via **Optical train: Auxiliary**).

Aux frames are written to a **separate `aux/` subtree**
(`{rig}/aux/{target}/{filter}/{session}/`) so they never mix with the
main camera's `lights/`. The aux camera is also viewable in the FOCUS
tab via the **Camera: Auxiliary** source switch. Capture + save is the
aux camera's main job - no guiding, live stacking or sequencing through
it - but the **SKY** map does draw its real field of view (see below):
when connected it shows a **pink aux FOV rectangle**, and a SKY plate
solve fires a parallel aux solve so the rectangle reflects the true
rotation + scale the aux frame will come out with.

### Accessories (collapsible)

`<details>` block below the main grid. Auto-expands when at least one
accessory has a saved selection.

- **Rotator**: angle readout + slew, sync
- **Flat Panel**: light toggle + brightness slider (where supported)
- **Dome**: azimuth, shutter, park, slave-to-scope toggle
- **Weather**: read-only sensor display (cloud, humidity, dewpoint,
  wind, sky temp, MPSAS)
- **Power Box**: a switch / power-distribution hub (e.g. Pegasus Astro
  Ultimate Powerbox) over INDI, ASCOM-COM (ISwitchV2) or Alpaca. Pick the
  driver (INDI / ASCOM / Alpaca) then the device, Connect, and the card
  lists every channel: on/off buttons for the 12V outlets, a value box +
  Set for dew-heater / PWM channels, and read-only voltage / current /
  temperature sensors. Outlets and dew levels are also drivable from the
  Advanced Sequencer - see the **Power Box** instruction group in
  [adv-sequencer.md](adv-sequencer.md).

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
`wwwroot/data/optical-accessories.json` (Main Telescope card) and
`wwwroot/data/guidescopes.json` (Guidescope card). All three are
checked in to the repo, to add a new OTA / reducer / guide scope, edit
the JSON, restart the server, refresh the browser. Pull requests with
additions for popular new hardware are welcome.

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

- [GUIDE (native)](guide-native.md), the built-in autoguider
- [GUIDE (PHD2)](guide-phd2.md), external PHD2 autoguiding
- [Settings](#), observatory location, image output dir, theme
- [Glossary → Rig](GLOSSARY.md#r)
