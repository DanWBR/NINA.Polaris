# GUIDE tab (native guider)

Polaris ships a built-in autoguider (`NativeGuider`) ported from PHD2's core
guiding math — single-star centroid, calibration, Hysteresis (RA) + Resist-Switch
(Dec) algorithms, Dec backlash compensation, multi-star, and pier-side handling.
It drives the rig's **guide camera** + **mount** directly (ST4-style pulse
guiding over the mount's PulseGuide), so no external PHD2 process is needed.

To use the external PHD2 integration instead, see [GUIDE (PHD2)](guide-phd2.md).
Pick the backend per rig in **RIGS → Guider driver** (`native` vs `phd2`).

## Requirements

- A **guide camera** selected and connected on the RIGS Guide Camera card
  (any supported driver: INDI, Alpaca, a vendor SDK, or the built-in Simulator).
  It must differ from the imaging camera while that is connected.
- A **mount** connected that supports **pulse guiding** (GEM/EQ with PulseGuide).
- A guider focal length set on the rig (drives the reported pixel scale).

No connect button is needed: the native guider **auto-connects** as soon as the
guide camera is on and the GUIDE backend is `native`.

## Bottom control bar

| Control | Meaning |
|---|---|
| **Exp (s)** | Guide-camera exposure per frame, picked from a dropdown of presets (0.1, 0.2, 0.5, 1.0, 1.5, 2.0, 3.0, 5.0 s). Persists to the rig. |
| **Gain** | Guide-camera gain (native only). |
| **Bin 2x2** | Bin the guide camera 2×2 for a brighter, smaller frame. |
| **Settle px / s / Timeout** | Settle tolerance, minimum settled time, hard timeout used after calibration starts and after a dither. |
| **Loop** | Capture continuously without guiding (framing, focusing the guide scope). |
| **Start Guiding** | Calibrate if needed, auto-select a star, then guide. |
| **Auto-select Star** | Pick the brightest suitable star. |
| **Pause / Resume / Stop** | Pause keeps the lock; Stop ends the loop (and aborts an in-progress calibration). |
| **Dither / px / RA only** | Manual one-shot dither. (Automatic dithering is driven by the AUTORUN sequencer or the LIVE tab — see below.) |

## Calibration

Calibration measures how a pulse on each axis moves the star (rate in px/ms and
the camera-frame angle of each axis), so corrections can be converted to pulse
durations. **Start Guiding** runs it automatically when there is no valid
calibration; **Recalibrate** forces a fresh run.

**Calibration & correction** settings (per rig):

- **Calibration step (ms)** — pulse length per calibration step. Larger steps
  finish faster but overshoot on short focal lengths; smaller steps are gentler.
- **Max RA / Max DEC duration (ms)** — caps the per-axis correction pulse during
  guiding (ASIAIR-style), so a big error can't run the mount away.

During calibration the **crosshair stays pinned** at the start position while the
star sweeps; the moving star is shown by its marker circle. A banner shows the
phase, step, and distance.

### Review Calibration panel

**Calibration details** opens a PHD2/ASIAIR-style review: RA/Dec steps, camera
angle, orthogonality error, rates (px/s and arcsec/s), Dec backlash, binning,
pier side, and an RA (blue) / Dec (red) scatter plot. A **RESTORED** tag marks a
calibration loaded from disk rather than freshly measured.

### Persistence + restore (per equipment)

Calibrations are saved **per rig, keyed by the equipment signature** — guide
camera + driver, binning, guider focal length, and mount + driver. On connect,
Polaris restores the calibration matching the gear currently fitted.

This means you can swap equipment, recalibrate, then swap the original gear back
and its old calibration is reused automatically. If no saved calibration matches
the current equipment, none is applied (a stale calibration is never reused) and
you'll be prompted to recalibrate. **Clear** removes only the current
equipment's calibration; other saved ones stay.

> A calibration saved before this feature existed has no equipment key; it
> restores as a legacy single slot until you recalibrate once.

## Guiding parameters

- **RA algo / Dec algo** — Hysteresis / Lowpass / Lowpass2 / Resist-Switch /
  Identity (defaults: Hysteresis RA, Resist-Switch Dec, PHD2's defaults).
- **Dec backlash comp (auto-measured)** — applies the slack take-up measured
  during calibration on a Dec direction reversal. Disabled if calibration didn't
  measure a backlash.
- **Multi-star guiding** — tracks several stars and guides on their average for a
  steadier centroid.
- **On meridian flip** — *Mirror calibration* (reuse, flipping RA by 180° and
  optionally Dec), *Recalibrate*, or *Do nothing*. **Reverse Dec after flip**
  toggles the Dec-pulse reversal used by *Mirror*.

## Live view, Star Profile, graph

- **Guide frame** with the lock crosshair + star markers.
- **Star Profile** shows a zoomed image of the locked star plus its intensity
  cross-section and FWHM, ASIAIR-style.
- **History graph** plots RA (red) / Dec (blue) error in arcsec on a symmetric
  scale (the **y ±** buttons set full scale; the axis is labelled in arcsec).
  The vertical impulse bars are the per-frame correction pulses, drawn opposite
  the error (the direction the mount is pushing the star back).
- **Target** bullseye scatters recent RA/Dec error.

## Dithering

Automatic dithering (a small random nudge every N frames so the stacker rejects
hot pixels / walking noise) is driven by:

- the **AUTORUN** sequencer (dither-every-N-frames between exposures), and
- the **LIVE** tab live-stacking triggers (see
  [Live stacking → Auto dither](live-stacking.md#auto-dither)).

Both route through whichever guider backend is active, so dithering works the
same with the native guider and external PHD2. The guider must be actively
guiding; the dithered frame waits to settle before the next exposure/integration.

## Mount safety

If you press Start Guiding (or auto-guide from a restored calibration) without a
connected, pulse-guide-capable mount, guiding aborts with a clear alert instead
of "running" while every pulse is silently dropped. If the mount drops
mid-session you get a periodic "Mount not connected: guide pulses are being
dropped" alert.

## Testing without hardware

Select the **Simulator** guide camera + mount drivers (RIGS) with guider driver
`native` to exercise the full calibrate → guide → dither flow indoors. See
[Equipment simulator mode](simulator-mode.md).

## See also

- [GUIDE (PHD2)](guide-phd2.md) — external PHD2 backend
- [Live stacking → Auto dither](live-stacking.md#auto-dither)
- [Equipment simulator mode](simulator-mode.md)
- [Glossary → Calibration / Dither / Guiding](GLOSSARY.md)
