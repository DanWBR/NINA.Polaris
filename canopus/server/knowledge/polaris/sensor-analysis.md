# Sensor analysis

The **Sensor analysis** tool (Equipment → camera card → *Sensor analysis*)
characterises your camera the same way SharpCap's Sensor Analysis does:
it measures the **conversion gain (e/ADU)**, **read noise (e)**, **full
well (e)** and **dynamic range (stops)** at each gain setting, so you can
pick the best gain for your imaging.

## How it works

It uses the **photon transfer curve (mean-variance)** method:

- A short **bias pair** (two frames at the minimum exposure) gives the
  read noise directly: subtracting two frames cancels the fixed pattern,
  so the standard deviation of the difference / √2 is the read noise.
- A **sweep of exposures** on a uniform light builds a set of flat levels.
  For shot-noise-limited flats the variance rises linearly with signal,
  and the slope is `1/gain`, so **gain (e/ADU) = 1 / slope**.
- **Full well (e)** = saturation × gain; **dynamic range (stops)** =
  log2(full well / read noise).
- It repeats this across the gain range to plot the curves.

It's justification-aware (a 12-bit sensor delivered left-justified in a
16-bit container steps by 16; that step is detected and divided out).

## Running it

1. Point the camera at a **uniform, constant light source** - a flat
   panel, an evenly lit wall, or twilight sky. Avoid stars or structure.
2. Equipment tab → your camera card → **📈 Sensor analysis**.
3. Adjust the range if needed (min/max gain, gain steps, max exposure,
   exposure steps) - the defaults are fine for a first run.
4. Click **Run**. It captures many frames per gain, so it can take a few
   minutes; a progress bar shows where it is, and you can Cancel.
5. When it finishes you get the green **gain** and red **read-noise**
   curves vs gain, a per-gain table, and a summary line with the measured
   bit depth, the quantization step, how far the sensor stayed linear,
   and the **unity gain** (where 1 e = 1 ADU).

It runs against the **camera simulator** too, so you can try the workflow
without hardware (the numbers then reflect whatever the simulator
produces, not a real sensor).

## Reading the results

- **e/ADU (gain)** - electrons per ADU. Falls as you raise the gain.
- **Read noise (e)** - lower is better; the big drop at low-to-mid gain is
  where many cameras have their "unity / HCG" sweet spot.
- **Full well (e)** - how many electrons a pixel holds before saturating;
  drops as gain rises.
- **Dynamic range (stops)** - full well ÷ read noise; highest at low gain.
- **Unity gain** - the gain where the gain is 1.0 e/ADU; a common starting
  point for deep-sky imaging.

## Tips

- Keep the light **constant** during the whole run; a changing source
  breaks the mean-variance relationship and rows come back marked invalid.
- If many rows are invalid, the light was too dim (variance never rose) or
  too bright (frames saturated before the sweep finished) - adjust the
  brightness or the max exposure and re-run.
- Stop any live stacking / video stream first; the tool refuses to run
  while they're active.
