# Flat Wizard (AUTORUN sub-tab)

Automates flat-field capture: for each selected filter, binary-search
the exposure until the median pixel value lands in a tolerance band
around your target ADU, then capture N flat frames at that exposure
and persist the trained exposure so the next session reuses it.

Lives inside AUTORUN as the **Flat Wizard** sub-tab. The tabstrip
"Sequence | Flat Wizard" at the top of the AUTORUN panel toggles
between the regular sequence editor and the wizard.

## Why bother with a wizard

Flats divide your lights to normalize vignetting + dust motes. Two
things matter:

1. **Median ADU in the right place**. Too dim → noise dominates the
   division. Too bright → wells clip + you get garbage. The classic
   sweet spot is **~30000 ADU for 16-bit cameras** (about 45% of
   full well).
2. **Exposure tied to filter**. R/G/B/L/Ha all transmit different
   amounts of light from your source, so each filter needs its own
   exposure.

Doing this by hand is tedious + error-prone. The wizard does the
search + capture + caching for you.

## Pre-flight

The pre-flight panel at the top of the sub-tab shows three rows:

- ✓ **Camera** must be connected (mandatory)
- ✓ **Filter wheel** must be connected with at least 1 filter
  (mandatory; the wizard's whole point is per-filter exposure
  tuning)
- ⚠ **Flat panel** is optional. When connected, the brightness
  slider in the sidebar applies before the wizard kicks off.
  When disconnected, the wizard works fine with sky flats
  (dusk / dawn) or a T-shirt over the front of the OTA.

If camera or filter wheel are missing, the link in the hint takes
you to the RIGS tab.

## Filter pick

Multi-select chips list the filters detected by the wheel. The
wizard captures in the order you tick them (left-to-right in the
chip list). **Select all** / **Clear** buttons help when you have
many filters.

Pick whichever subset matches your imaging plan, no need to capture
flats for filters you didn't shoot lights in.

## Settings

Per-rig defaults persist on `EquipmentProfile.FlatWizard` so
switching to a different rig (cold APO vs warm SCT) restores its
own values:

- **Target ADU**, default 30000. Drop to ~20000 for 14-bit sensors
  that clip earlier or for narrowband filters where you want more
  headroom.
- **Tolerance (%)**, default 5%. The search converges when the
  median lands in `[target * (1 - tol), target * (1 + tol)]`. Drop
  to 1-2% if your pipeline is sensitive to flat-field normalization.
- **Frames per filter**, default 20. Enough for a clean median
  master at most pixel scales. Bump to 40-50 for very low-light
  sky flats where each frame has more noise.
- **Min / Max exposure (s)**, defaults 0.1 / 30.0. Sets the
  binary-search bracket. Tighten to ~0.5 / 10 for bright panels,
  or widen for sky flats at deep twilight.
- **Binning**, default 1. Trained exposures cache per
  (filter, binning) tuple, so changing binning trains a separate
  slot. The wizard applies binning to the camera before each
  search/capture pass.
- **Max iterations**, default 10. Covers ~3 decades of dynamic
  range. Only bump if you've tightened tolerance to &lt; 2% and
  convergence stalls.

Every field debounce-saves (~400ms) into the active rig so editing
mid-session sticks even if you switch tabs without explicitly
saving.

## Panel brightness

Sidebar, shown only when a flat device is connected. Slider 0-100.
**0 means "don't touch the panel"** — useful when you're doing
sky / T-shirt flats and a panel happens to also be connected but
you don't want to use it.

When &gt; 0, the wizard `POST`s `/api/flatdevice/brightness` with
your chosen value before starting. The wizard never auto-adjusts
brightness during a run; if a filter needs a different exposure
range than the panel is delivering, the search will just take more
iterations to converge.

## Shutter (the round button)

The wizard uses the same Polaris shutter component as LIVE /
PREVIEW / AUTORUN. Gesture model:

- **Tap** when idle → start the wizard (same as long-press; the
  wizard runs to completion once kicked, no loop concept).
- **Tap** while running → abort. The current capture finishes, then
  the loop exits with `lastError = "Cancelled"`.

The ring around the button shows composite progress:
`(filtersDone + frames-in-current-filter) / totalFilters`. Empty
ring = idle, full ring = last filter just finished its last frame.

The shutter is disabled (cinza) when:

- No camera connected
- No filter wheel connected
- Zero filters picked

## Live progress

Below the shutter, while running:

```
Filter 2/3 · G
Searching attempt 4: 2.137s → 28473 ADU
```

Then after convergence:

```
Filter 2/3 · G
Capturing 8/20 at 2.137s
```

When a filter completes, it goes into the **Done** list with the
final exposure or "did not converge" warning. The list accumulates
across the full run so you can see at a glance which filters made
it.

## Trained exposures table

Reads from `GET /api/flatwizard/trained` on tab-enter and refreshes
after each wizard run. Shows `filter / binning / exposure`,
e.g.:

```
L  · 1 · 1.847s
R  · 1 · 2.341s
G  · 1 · 1.923s
B  · 1 · 3.412s
```

The next time you run the wizard with the same filter + binning,
the trained exposure seeds the binary search and convergence
takes 1-2 iterations instead of 5-8.

The cache lives in `{AppData}/NINA.Polaris/trained-flats.json`,
shared across rigs (key includes binning, not rig).

## Output

Each captured flat goes through `ImageWriterService` with
`IMAGETYP=FLAT`, so frames land in your usual
`{ImageOutputDir}/{rig}/Flat/...` tree alongside lights / darks /
biases. The STUDIO library auto-indexes them.

## Workflows

### Indoor with a flat panel

1. Cap the scope or close the dome shutter — keep ambient light
   off the panel.
2. Tube the panel against the dewshield / front of the OTA.
3. Aba AUTORUN → Flat Wizard sub-tab.
4. Pick filters L + R + G + B.
5. Settings: TargetADU 30000, Tolerance 5%, Frames 20, Min 0.1,
   Max 5 (panels are bright; tight max keeps search fast).
6. Brightness 50 (start middle; if all filters need very long or
   very short exposures, adjust + re-run).
7. Tap shutter. ~5 minutes later (4 filters × ~30s search + 20
   frames each at sub-second exposure) you have all 4 masters.

### Outdoor with sky flats

1. Open the dome / point at zenith (away from the sun) before the
   sun sets enough that you'd shoot lights.
2. Aba AUTORUN → Flat Wizard sub-tab.
3. Pick filters L + R + G + B.
4. Brightness 0 (no panel needed even if one is connected).
5. Settings: Min 0.1, Max 30 (deep twilight means dim, so widen
   the bracket).
6. Tap shutter. Sky brightness changes fast at twilight, so plan
   to capture one filter set per twilight window. Trained exposure
   from the wizard becomes useless 5 minutes later because the sky
   is now 2× brighter or darker.

## Troubleshooting

**Search never converges**, the bracket may be too narrow or the
target ADU is unreachable. Widen Min/Max exposure. Check the
panel/sky actually reaches your target ADU with a manual capture
at the max exposure — if the median is still well below target,
the light source is too dim.

**"Did not converge" for one filter**, the binary search collapsed
without hitting tolerance. Often happens with narrowband filters
under broadband sky flats — they transmit so little that the
brightest exposure you'd accept still produces a median below
target. Capture that filter against a panel instead.

**Trained exposure gives wildly wrong ADU on the next run**, the
illumination changed between sessions (panel brightness mod,
different sky condition). Run the wizard again — convergence will
be fast (1-2 iterations) and a new value gets cached. The wizard
always trusts the live measurement over the cache.

**Wizard says "no camera connected"**, even though one is selected
in RIGS. Check the camera card's status dot. If amber or red,
click Connect on the card.

## API

- `GET /api/flatwizard/status` — `{state, progress, lastError}`
- `GET /api/flatwizard/trained` — `{filter_bin: seconds}` map
- `POST /api/flatwizard/start` with body
  `{filters[], framesPerFilter, targetAdu, tolerance, minExposure,
  maxExposure, binning, maxSearchIterations}` → 200 / 409 if
  already running
- `POST /api/flatwizard/abort` → cancels the current run

The same state is on the WebSocket `flatWizard` sub-object every
tick:

```jsonc
{
  "flatWizard": {
    "state": "running",
    "lastError": null,
    "progress": {
      "startedAt": "2026-05-29T11:23:45Z",
      "totalFilters": 4,
      "currentFilterIndex": 1,
      "currentFilter": "R",
      "phase": "capturing",
      "searchAttempt": 3,
      "currentExposure": 2.341,
      "lastMedian": 30142,
      "totalFramesPerFilter": 20,
      "framesCaptured": 7,
      "filterResults": [
        { "filter": "L", "converged": true,
          "finalExposure": 1.847, "framesCaptured": 20 }
      ]
    }
  }
}
```

## See also

- [AUTORUN](autorun.md), the sibling tab that captures lights
- [End-to-end workflow](end-to-end-workflow.md), how flats fit
  into calibration before stacking
- [STUDIO](studio.md), where masters get built from your captured
  flats
