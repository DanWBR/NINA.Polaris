# ADV tab (Advanced Sequencer)

NINA-style tree-based sequence engine for multi-target unattended runs
with conditions + triggers + branches. For simple flat lists, use
[AUTORUN](autorun.md) instead — ADV is for the cases where flat lists
fall short.

## When to use ADV vs AUTORUN

| Use case | Pick |
|---|---|
| 100 frames of M81 in L | AUTORUN |
| LRGB across 3 targets with end-of-night warm-down | AUTORUN |
| LRGB + Ha across 5 targets with conditional weather safety | ADV |
| Per-target rotator + filter offsets + dither cadence variations | ADV |
| Mosaic with branching capture per panel | ADV |
| Anything where you want "if temperature drops 2°C, run AF" | ADV |

## Tree model

Three node types make up the tree:

- **Containers** group children (Sequential, Parallel, DeepSkyObject,
  Templated)
- **Instructions** are atomic actions (Take Exposure, Slew, Center,
  Move Focuser, Dither, Park, ...)
- **Conditions** gate execution (Loop Until Time, Loop Until Altitude,
  Loop For N Exposures, ...)
- **Triggers** fire on events (Auto-focus on Temperature Change,
  Meridian Flip Trigger, Dither After N Exposures, ...)

Triggers attach to containers and apply to every instruction underneath
them — so an "Auto-focus on Temperature Change" attached to a Deep Sky
Object container will fire AF whenever the threshold crosses during any
instruction inside that target.

## Tri-pane editor

Left: **Palette** of available items (filterable). Drag onto the tree.

Center: **Tree view** with drag-reorder, expand/collapse, status
colouring during runs (green = done, blue = running, red = errored).

Right: **Properties panel** for the selected item. Numeric / text /
dropdown fields driven by the type's metadata.

Toolbar: Start / Pause / Resume / Stop / Save JSON / Load JSON / Import
Template / Export Template / New Sequence.

## Templates

A subtree can be saved as a template (e.g. "M81 LRGB nightly" with the
specific filter plan + dither cadence + AF triggers). Templates load
into the palette + drag-drop into any new sequence.

`{AppData}/Polaris/sequencer/templates/*.json`.

## Available items

(non-exhaustive; the palette reflects what's loaded)

**Mount**: Slew, Slew & Center, Park, Unpark, Set Tracking, Solve & Sync

**Camera**: Take Exposure, Take Many Exposures, Cool Camera, Warm Camera

**Focuser**: Auto Focus, Move Focuser, Move to Filter Offset

**Filter Wheel**: Switch Filter

**Guider (PHD2)**: Start Guiding, Stop Guiding, Dither, Auto-select Star

**Dome / Flat Panel / Rotator**: open/close, slew azimuth, set brightness, rotate to angle

**Flow control**: Wait For Time, Wait Until Above Horizon, Wait For
Altitude, Wait For Moon Below Horizon, Wait For Sun Below Horizon

**External**: Run External Script, Send HTTP Request, Send Email

## Conditions

- **Loop Until Time** — datetime cutoff
- **Loop Until Altitude** — target altitude drops below threshold
- **Loop For N Exposures** — frame counter
- **Loop For Duration** — wall-clock timer
- **Loop While Safe** — checks weather safety

## Triggers

- **Auto Focus on Temperature Change** — Δ°C threshold
- **Auto Focus on HFR Increase** — % above baseline
- **Auto Focus on Time Elapsed** — interval
- **Auto Focus on Filter Change** — fires after every filter swap
- **Meridian Flip Trigger** — flips the mount when target crosses
- **Dither After N Exposures** — dither cadence
- **Center After Drift** — plate solve + re-center periodically
- **Safety Trigger** — abort to safe state on weather event

## Migration from AUTORUN

Open an AUTORUN sequence + click "Convert to Advanced" — Polaris
generates an equivalent tree with each row mapped to a DeepSkyObject
container.

## Common pitfalls

**Sequence appears to do nothing** — a condition's gate is always
false. Inspect the tree for `Loop While Safe` with no weather device.

**Triggers fire infinitely** — your trigger condition matches a state
that the action doesn't reset. Example: Auto-focus on HFR Increase
without setting a baseline → fires every frame. Fix: combine with a
"every N frames" trigger as a floor.

**Lost the work** — JSON Save isn't automatic. Save manually before
running long sessions.

## See also

- [AUTORUN](autorun.md) — simpler alternative
- [Glossary → various](GLOSSARY.md)
