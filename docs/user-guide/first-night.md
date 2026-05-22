# First-night setup

Goal: from "I just opened Polaris in my browser" to "first ten frames
of a target are landing on disk". ~30 minutes if everything cooperates.

This walkthrough assumes you have [Polaris installed](installation.md)
and at the welcome screen.

## Pre-flight: pick a target

Pick something easy your first time:

- **Vega, Altair, Arcturus** — bright single stars. Great for focus +
  guiding tests.
- **M31, M42, Pleiades** — bright famous targets. Visible to the
  naked eye, easy to verify pointing.
- **Anything ≥30° altitude** — avoid the horizon.

You don't need to type RA/Dec — Polaris has a built-in catalog. We'll
search by name later.

## Step 1 — Set your location

Polaris uses your location for sky calculations (altitude, twilight,
meridian flips). On first run you should see a location modal — fill
it in with one of:

- **Address search** — types like "Brasília, Brazil" or your city +
  country, hits ↵, picks the matching result
- **Browser geolocation** — clicks "Use my location" (HTTPS only)
- **Manual lat/lng** — types decimal degrees

If you skipped the modal, go to **Settings** in the sidebar → Observatory
location.

> **Why this matters**: with location wrong, the Sky tab + meridian
> flip + tonight's-best altitudes are all garbage. ⤴ See
> [Glossary → LST](GLOSSARY.md#l) for the why.

## Step 2 — Create a rig

Click **RIGS** in the sidebar.

You start with a rig called "Default". Either rename it to something
meaningful or create a new one:

1. Top of the RIGS tab: rig dropdown + "Manage rigs…" button.
2. Click **Manage rigs…** → type a name like "Backyard SCT 8\"" in
   the "New rig name" input → **+ New empty rig** → close the modal.
3. The new rig is now active (selected in the dropdown).

> **Rigs explained**: a rig is a saved bundle of equipment selections
> + per-setup defaults. Switch rigs in one click when you move from
> backyard SCT to travel APO. See [RIGS tab guide](rigs.md) for the
> full feature surface.

## Step 3 — Configure the Main Telescope card

Now you're back on the RIGS tab. The first card is **Main Telescope**.

Pick **Brand** from the dropdown (Askar / Celestron / Sky-Watcher /
GSO / ...) → pick **Model**. Aperture + focal length + f-ratio + back-focus
fill in automatically.

If you have a reducer / flattener, pick **Accessory** too — the
effective focal length recalculates.

> **What if your scope isn't in the catalog?** Leave Brand = "Manual
> entry" and type focal length + aperture by hand. The catalog is just
> a convenience — Polaris doesn't require it.

## Step 4 — Connect INDI (Linux) or Alpaca (Windows)

The cards below Main Telescope (Camera, Mount, Focuser, Filter Wheel,
etc.) show empty dropdowns until you connect to a driver host.

Top of the RIGS tab there's a **connection strip**. If `indiserver` is
running on the same machine:

1. Tab **INDI** (the default)
2. Host `localhost`, Port `7624` — defaults are fine
3. Click **Connect INDI**

You should see the connection strip turn green with "✓ INDI · localhost:7624
· N devices". Each role card's dropdown now lists the matching INDI
devices.

For Windows + Alpaca, switch the tab to **ASCOM/Alpaca** + click
**Discover** to auto-find Remote Servers on your LAN.

> **Troubleshooting**: see [Troubleshooting → "INDI not connecting"](troubleshooting.md#indi-not-connecting).

## Step 5 — Pick devices in each card

For each card you actually have hardware for:

1. Pick the device from the dropdown ("ZWO CCD ASI2600MC Pro", etc.)
2. Click **Connect** — the status dot goes green
3. (Camera) Set the cooler target temperature if you have one
4. Cards you don't use stay "Select device" — Polaris ignores them

Click **💾 Save selections** at the top to persist the picks in the
active rig. You won't need to redo this next session — switching rigs
restores everything.

## Step 6 — (Optional) Hook PHD2

If you have a guide scope + guide camera:

1. **GUIDE** tab in the sidebar
2. Default tab **Control** is open
3. If PHD2 isn't installed yet, the banner gives a download link
4. Click **Launch PHD2** if you have it auto-detected, or **Connect**
   if PHD2 is already running on `localhost:4400`
5. In PHD2 itself (or via the **PHD2 GUI** tab on Linux): pick the
   profile that matches your guide camera + mount
6. Click **▶ Connect equipment** in Polaris → PHD2 wires up its own
   gear
7. **▶ Loop** to start exposure cycling → **find star** → **▶ Smart
   Calibrate** if calibration isn't done yet

> **First-time calibration**: point the mount somewhere within 30° of
> the celestial equator (Dec near 0°) for best results. Polaris's
> Smart Calibrate computes the step size from pixel scale + guide rate
> for you.

## Step 7 — Slew to your target

1. **SKY** tab in the sidebar
2. Search bar at top → type "Vega" (or your target name) → ↵
3. Result card appears → click it → preview shows it on the sky map
4. Click **🎯 Slew & Center** → Polaris commands the mount, captures a
   plate solve frame, computes the offset, and re-slews until centered
   to within 30 arcsec
5. The whole flow takes 30 seconds to 2 minutes depending on how far
   off the mount's pointing was

> **What if Slew & Center fails?** The plate solve probably can't find
> a match. Check that:
> - Focus is close enough that stars actually exist in the frame
> - Exposure is long enough for stars to register (try 5-10s for the
>   solve)
> - The catalog hints (focal length, pixel scale) on the active rig
>   are accurate

## Step 8 — Quick focus check

Before committing to a sequence:

1. **FOCUS** tab → set **Step Size** to ~20 (your focuser, your
   numbers)
2. Click **▶ Take snap** in the PREVIEW tab → see what the field looks
   like
3. Back to FOCUS → manually step in / out a few times watching the
   HFR value in the stats bar; when it bottoms out you're at focus
4. (Better) Click **▶ Start AF** in the auto-focus panel → V-curve
   sweep runs, parabola fit, focuser moves to the vertex. Watch the
   live frame preview canvas as it samples each position.

## Step 9 — Run a test sequence

Now the actual capture:

1. **AUTORUN** tab
2. **+ Add target** → pre-fills with your current SKY tab target
3. Set:
   - Filter (or leave blank if mono filter wheel)
   - Exposure time (start with 30s for a bright target)
   - Count (10 to start)
   - Optional: Bin, Gain
4. Click **▶ Start sequence** → captures begin
5. Files land in `{ImageOutputDir}/{RigName}/lights/{Target}/{Filter}/{Date}/`

Watch the LIVE tab to see the latest frame + running statistics. Toggle
the **Stack ON** button to enable live integration — frames pile up
in a single growing image.

## Step 10 — Stop + look at your work

When the sequence completes (or you click ⏹ Stop):

1. **STUDIO** tab in the sidebar → rescan → all your captures appear
   in the frame browser
2. Click any frame → see the full-resolution viewer with stats
3. Continue with master generation + calibration + stack — see
   [Studio guide](studio.md) for the post-processing flow

## What just happened

In 30 minutes you covered the full Polaris workflow:

- **Location → Rig → Equipment** (RIGS tab) — one-time setup, persistent
- **PHD2 hook-up** (GUIDE tab) — once per equipment combo, then auto-
  resumes
- **Slew & Center** (SKY tab) — every new target
- **Auto-focus** (FOCUS tab) — once per session + auto-triggered during
  long stacks (see [LIVE auto re-focus](live-stacking.md))
- **Sequence run** (AUTORUN tab) — the unattended capture engine
- **Live preview + Studio** — visual feedback + offline processing

From here, dig into each feature page as your needs grow — auto-focus
during sequences, meridian flips, dithering, plate-solve recenter
triggers, planetary lucky imaging, the embedded PHD2 GUI, the relay
server for remote access.

Welcome aboard. ✨
