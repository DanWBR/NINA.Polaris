# LIVE tab (live stacking + EAA)

Real-time integration of incoming frames into a single growing image.
For Electronically Assisted Astronomy (EAA), comet hunting, or just
watching your DSO target build up while you have a beer.

> **Where the math runs**: by default the server (Pi / mini-PC) does
> the stacking. On underpowered hosts (Pi 2/3) you can flip the
> Compute dropdown to client-side WASM offload so the browser owns
> the accumulator, see [client-side compute](client-side-compute.md).

## How it works

Polaris doesn't drive the capture from the LIVE tab, it **subscribes**
to frames arriving from anywhere (sequence engine, PREVIEW snap, camera
stream). When **Stack ON**, each incoming frame:

1. Has its stars detected
2. Is aligned (affine transform) against the reference (first frame's
   star pattern)
3. Accumulates into a running-mean buffer
4. Re-renders the stacked image to the canvas

Frame count + reference star count update each second.

## Controls (top row)

- **Capture / Loop / Stop**, same as PREVIEW; convenience to fire
  captures without leaving the tab
- **Stack ON / Stack**, toggle integration on/off
- **Reset** (visible when Stack ON), discards the running stack +
  reference. Next incoming frame becomes the new reference.
- **⛶ View**, OpenSeadragon viewer on the current stack

## Stats bar

- **Stars**, count in latest frame
- **HFR**, median HFR of latest frame (lower = sharper)
- **Mean / Median / StDev / Min / Max**, pixel stats of the stack
- **HFR + Star count history chart**, last N frames trend

## Auto re-focus / re-center

The big feature: long sessions degrade as focus drifts with temperature
and mount drift accumulates. Polaris fires AF + re-center automatically
on configurable triggers, without you intervening.

Click the **⚡ Auto re-focus / re-center** `<details>` panel to expand.

### Auto re-focus

Enable + pick any combination of triggers (first to cross fires):

- **Every N integrated frames**, pure frame counter
- **Every N minutes**, wall-clock elapsed
- **ΔT ≥ X°C**, sensor temperature drift since last AF
- **HFR ≥ Y% above last**, when current HFR degrades by Y% vs the
  HFR right after the last successful AF

Plus AF sweep config (steps, step size, exposure) reused from the
[FOCUS tab](focus.md#parameters).

When the trigger fires:

1. Captures pause naturally (the frame handler awaits the AF run, and
   whoever's pushing frames is awaiting that handler)
2. AF sweeps + finds best position
3. New HFR + temperature are baselined
4. Captures resume on their own

**▶ Now** button bypasses gates for manual fires.

### Auto re-center

Same OR-combine pattern. Three trigger types:

- **Every N frames**
- **Every N minutes**
- **Drift ≥ X arcsec**, runs a plate-solve **per frame** to compute
  drift from the reference; **expensive on RPi 4** (1-3s per solve).
  Default 0 = disabled.

When the trigger fires, Polaris re-slews + plate-solves until the
mount is centered to within **Tolerance arcsec** (default 30").

**Reference RA/Dec** is established by a one-shot plate solve on the
**first integrated frame** of the session, true astrometric position,
not the mount's potentially-biased report. Status line shows the
reference once it's solved; banner appears if the first-frame solve
failed (recenter disabled until next stack reset).

**▶ Now** button only enables once reference is solved.

### Mutex

Trigger handlers run sequentially inside the frame integration. AF +
re-center can't run concurrently (reentry guard skips trigger eval
for frames arriving during an executing trigger).

### Per-rig persistence

The full policy lives on `EquipmentProfile.LiveStackTriggers`. Switch
rigs + the new rig's policy auto-loads. Different setups (cold APO vs
SCT with active dew heater) have different thermal characteristics,
so per-rig makes sense.

## WebSocket payload

For automation / external dashboards, the live stack state is in the
1Hz `/ws/status`:

```jsonc
{
  "liveStack": {
    "isRunning": true,
    "frameCount": 42,
    "width": 4144, "height": 2822,
    "referenceStarCount": 87,
    "lastFrameHfr": 1.94,
    "lastFrameStarCount": 92,
    "triggers": {
      "isExecuting": false, "executingKind": null,
      "lastRefocusAt": "2026-05-22T22:48:13Z", "lastRefocusFrame": 30,
      "lastRefocusHfr": 1.87, "lastRefocusTempC": -10.2,
      "lastRecenterAt": null, "lastRecenterFrame": 0,
      "lastRecenterDriftArcsec": 0,
      "referenceRaHours": 23.234, "referenceDecDeg": 12.583,
      "referenceSolved": true,
      "lastError": null
    }
  }
}
```

## Common pitfalls

**Frames stop integrating mid-session**, alignment failed for a long
stretch (clouds, dew on the corrector). Polaris keeps trying each
frame; clear the dew + integration resumes automatically.

**Auto-recenter never triggers**, reference solve failed on the
first frame (no stars, blurry, plate-solver path wrong). Reset the
stack so the next first frame solves cleanly. Verify ASTAP is
installed + reachable.

**HFR trigger fires spuriously after passing meridian**, pier-side
flip changes the frame orientation; star detection sees "new" stars
and the median HFR jumps temporarily. Bump the HFR threshold or
combine it with "every N frames" so the spurious fire is bounded.

**Stack quality looks worse than individual frames**, alignment
keeps failing silently, only the first frame is actually in the
stack. Check the WebSocket payload: `frameCount` should match how many
frames you've captured. Persistent reference-star-count of 0 means
the first frame had no detectable stars, Reset + try again.

## See also

- [FOCUS tab](focus.md), the AF engine these triggers invoke
- [SKY tab → Slew & Center](sky-explorer.md), the recenter engine
- [Glossary → EAA / HFR / Dither / Plate solve](GLOSSARY.md#e)
