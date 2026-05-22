# FOCUS tab

Manual focuser control + automated V-curve focusing with live frame
preview.

## Manual stepper

The big stepper at the top of the tab:

```
[<<]  [<]  [12345]  [>]  [>>]
```

- `<` / `>` move 1 step in / out
- `<<` / `>>` move `Step Size` steps in one go
- The middle number is the current focuser position; click to type a
  direct value

**Step Size** slider sets the `<<` / `>>` jump distance (1 to 500
steps, persisted per rig).

Info panel below shows:
- **Temperature** — focuser-reported temp (°C) when supported. Drives
  the LIVE tab's auto-refocus ΔT trigger.
- **Status: MOVING** — flashes while the focuser is in motion. Inputs
  disable to prevent stacked commands.

## Auto-Focus (V-curve)

Click **▶ Start AF** to run a symmetric sweep around the current
position. Polaris:

1. Builds a list of N positions: `current ± (Steps/2) × StepSize`
2. (Optional) Applies backlash compensation by overshooting in one
   direction
3. At each position:
   - Moves the focuser + waits for settle
   - Captures an exposure (`Exposure (s)`)
   - Detects stars + computes median HFR (`Min Stars` floor — frames
     with fewer stars are dropped)
4. Fits a parabola through valid (position, HFR) samples
5. Moves to the parabola's vertex (best focus)
6. Reverts to start position on cancel / failure

### Parameters

- **Steps** (3-25, odd) — how many sample positions in the sweep.
  9 is a good default; 5 if you're already near focus + want speed;
  15 if you're far off.
- **Step Size** — same units as the manual stepper. Should be small
  enough that the V-curve has clear shape (not all samples at the
  bottom, not all at the top). Typical: 50-200 steps for SCT, 20-80
  for refractor.
- **Exposure (s)** — long enough to register stars in the field. 3s
  is fine for most DSO setups; planetary uses 50-500ms.
- **Min Stars** — minimum stars required for a valid HFR sample. 5
  is sane; bump to 20 for crowded fields where HFR is noisy.
- **Backlash** — overshoot in steps when reversing direction. 0 for
  belt-driven focusers; use your focuser's published backlash for
  geared ones.

### Live progress

While AF is running:

- **Progress bar** + sample counter `8 / 9`
- **Last HFR + star count** update each sample
- **V-curve chart** appears bottom of panel, plotting (position, HFR)
  with the fitted parabola overlaid + best-position marker
- **Live frame preview canvas** shows the actual frame Polaris just
  captured at each sample, with a HUD chip displaying
  `pos {N} · HFR {x.xx} · ★ {stars}`

The preview canvas pipes through the same `/ws/image-stream` channel
LIVE + PREVIEW use, so you see the focus visually converging as the
focuser steps.

### Abort

- **Abort AF** — cancels the sweep; restores the starting focuser
  position
- **Stop Focuser** — only enabled while the focuser is moving (not
  during AF); emergency stop for a runaway manual command

## Auto-focus triggers (advanced)

AF doesn't just run on demand — it can be **automatically triggered**
in two places:

1. **Sequence engine** (AUTORUN tab): trigger AF at every N frames /
   on temperature change / on HFR degradation / on filter change.
   Configure under the sequence's Triggers panel.
2. **Live stacking** (LIVE tab): same four trigger types, evaluated
   per integrated frame. Captures pause naturally while AF runs (see
   [LIVE auto re-focus](live-stacking.md)).

## Common pitfalls

**HFR comes back as 0** — no stars detected. Increase exposure, check
that you're actually pointed at the sky + not a flat-grey panel.

**V-curve has no clear minimum** — Step Size too small (all samples
clustered at bottom) or too large (all in noise). Try doubling /
halving Step Size and retry.

**AF moves to a wildly wrong position** — parabola fit was poisoned by
outliers. Polaris validates "best position within ±2 × StepSize × N/2
of start"; outside that it warns. Use Backlash > 0 if your focuser has
hysteresis; bump Min Stars to drop noisy samples.

**Focuser stops moving mid-sweep** — driver lost connection. Check
INDI logs; reconnect the focuser; restart AF.

## See also

- [LIVE auto re-focus triggers](live-stacking.md#auto-re-focus--re-center)
- [AUTORUN sequence triggers](autorun.md)
- [Glossary → HFR / V-curve](GLOSSARY.md#h)
