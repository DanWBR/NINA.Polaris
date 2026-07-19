# PLAN: Multi-Target Night Planner

The **PLAN** tab (sidebar, right after AUTORUN) is Polaris's ASIAIR-style
whole-night planner. Where AUTORUN runs one shooting schedule against whatever
the scope is currently pointed at, PLAN lets you queue **several targets** in
advance, each with its own frame list, and runs them in order with automatic
slew + plate-solve-center between targets, optional guiding, cooling,
auto-focus, and meridian-flip handling, then runs end-of-session actions when
the night is done.

Under the hood a plan "compiles" to an Advanced-Sequencer document and runs on
the same engine as the [ADV](adv-sequencer.md) tab, so only one of the two can
run at a time, PLAN refuses to start while a sequence is already running (and
vice-versa).

![](screenshots/plan-tab-overview.png)

---

## The plan library

Plans live in a **global library** in your user profile, so a saved plan is
runnable with any active rig.

- **Picker row**: choose a saved plan, or **New / Duplicate / Delete**.
- **Export / Import**: share a plan as a JSON file and load it back on
  another host.

---

## Plan settings

Open the plan-settings sheet (hexagon button) to configure the whole night:

| Setting | What it does |
|---|---|
| **Start** | Begin **now** or **at a clock time** (the runner waits, then starts). |
| **End** | Stop when **all frames are done**, at **astronomical dawn**, or at a **set time**. |
| **Auto guiding** | Start the active guider (native or PHD2) before the first target and stop it at the end. |
| **Auto cooling** | Cool the camera to a target temperature at the start and warm it at the end. |
| **Auto meridian flip** | Flip across the meridian automatically, pausing/resuming the active guider around the flip. |
| **Auto-focus** | Run auto-focus at the start and/or per target. |

### Meridian flip tuning

When auto meridian flip is on, the PLAN panel exposes the flip parameters
directly (no need to go to the AUTORUN/ADV trigger):

- **Minutes after meridian**: how long past the meridian to wait before
  flipping.
- **Recenter after flip**: plate-solve-center on the target again after the
  flip so framing is preserved.
- **Auto-focus after flip**: run an auto-focus once the flip completes.

The flip pauses and resumes **whichever guider backend is active**, not just
PHD2.

---

## Targets

Each plan holds an ordered list of targets. For every target:

- **Enable / disable** without removing it.
- **Name, RA/DEC, rotation**, and a **delay start (min)** to hold a target
  until it clears an obstruction.
- **Per-target frame list**: exposure × count per filter / gain / binning,
  edited like the AUTORUN frame rows. A "copy frames to all targets" action
  saves repetitive setup.
- **Per-target re-center / re-focus / dither every N frames**, keep framing,
  focus, and dithering maintained within a long target run.
- **Reorder / delete** within the list.

### Adding a target

- **Catalog search**, find a DSO by name/catalog from the bundled database.
- **Manual RA/DEC**: type coordinates directly.
- **Use current mount position**: capture wherever the scope points now.
- **Frame in the Sky map**, switch to [SKY](sky-explorer.md), frame the
  target with the FOV overlay + rotation, then "Add to plan" captures the
  center RA/DEC + rotation.

---

## End-of-session actions

When the plan finishes (or reaches its end condition), Polaris can:

- **Warm the camera + turn the cooler off**,
- **Park / go home** the mount,
- **Send the focuser to zero**,
- **Shut down the host** (gated behind an explicit confirmation, the device
  powers off completely and must be turned back on physically).

---

## Running a plan

- **View plan**, a whole-night timeline graph (elevation curves per target,
  with per-target delays marked) to sanity-check the order before you start.
- **Polaris Shutter** Start/Stop in the run bar, pinned to the bottom of the
  panel, with **current-target** and **total** progress bars and elevation
  shown during capture.
- The plan status (waiting-for-start / running / current target) is mirrored
  on the HOME dashboard and in the top status bar.

---

## Notes & current limits

- PLAN and ADV share one engine, start one and the other is blocked, with a
  clear message.
- **Single-frame targets** in this version; mosaic targets and
  rotator-driven auto-rotation are planned for a later pass (today
  `Rotation` is record-keeping unless a rotator move is added to the flow).

---

## Related

- [AUTORUN](autorun.md): single-target shooting schedule.
- [ADV](adv-sequencer.md): the underlying tree sequencer.
- [SKY Explorer](sky-explorer.md): target search + visual framing.
- [GUIDE (native)](guide-native.md) / [GUIDE (PHD2)](guide-phd2.md): guiding
  backends the plan can drive.
