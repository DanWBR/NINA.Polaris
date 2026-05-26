# RIGS tab

The RIGS tab is your equipment cockpit. It centralizes:

1. **Driver-host connection**, INDI or Alpaca, always visible at top
2. **Per-role equipment cards**, Main Telescope, Camera, Mount,
   Focuser, Filter Wheel, Guidescope, Guide Camera, plus collapsible
   Accessories (Rotator, Flat Panel, Dome, Weather)
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
- **PHD2** endpoint (host + port), profile id cache, algo preset, calibration step override, custom algo params
- **Filter offsets** table
- **Live-stack triggers** (refocus + recenter policy, see [LIVE](live-stacking.md))

## Telescope + accessory catalog

The dropdowns are driven by `wwwroot/data/telescopes.json` +
`wwwroot/data/optical-accessories.json`. Both are checked in to the
repo, to add a new OTA / reducer, edit the JSON, restart the server,
refresh the browser. Pull requests with additions for popular new
hardware are welcome.

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
