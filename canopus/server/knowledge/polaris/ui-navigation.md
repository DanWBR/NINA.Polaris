# UI navigation: where do I go to do X?

A quick "task → panel → what to click" map of the N.I.N.A. Polaris web UI, for
when you know *what* you want but not *where* it lives. The left sidebar is the
main navigation rail; each button opens one panel. Some panels have a pill/tab
row at the top for sub-views.

## The panels (left sidebar, top to bottom)

| Sidebar button | Opens | Use it to… |
|---|---|---|
| **Home** | Dashboard | See session status at a glance; jump to common actions. |
| **Rigs** | Equipment | Pick/connect your camera, mount, focuser, filter wheel, guider; manage equipment profiles (rigs). |
| **Polar** | Polar alignment | Run TPPA or the rudimentary single-target polar alignment. |
| **Sky** | Sky Explorer | Browse the sky map, pick a target, and (via the pill row at the top) see **Tonight**'s best targets and the **Weather**. |
| **Focus** | Focus | Auto-focus (V-curve sweep) or focus manually with the HFR/Bahtinov aids. |
| **Guide** | Guider | Start/stop guiding (native or PHD2), calibrate, watch the guide graph. |
| **Preview** | Preview | Snap single test exposures (framing, focus check, test a target). Also plate-solve the current frame. |
| **Autorun** | Imaging schedule | Shoot a target unattended: set exposures/filters/count, then start. Also holds the **Flat Wizard**. |
| **Plan** | Multi-target planner | Plan a whole night across several targets with time windows; then run it. |
| **Live** | Live view / EAA | Live-stack frames as they land (ASIAIR-style), watch it build in near real time. |
| **Video** | Planetary/video | High-fps capture, recording, and lucky-imaging stacks (Moon/planets). |
| **Adv** | Advanced Sequencer | Build a tree-based sequence (containers, triggers, conditions) when Autorun/Plan isn't enough. |
| **Studio** | Files + stacking + editor | Browse saved files, grade/integrate subs, colour-calibrate, and post-process (editor, star removal, AI tools). |
| **Config** | Settings | Plate solver, network, authentication, AI/ONNX, colour-calibration data, appearance, and more. |
| **Help** | Tutorials | Step-by-step walkthroughs, first-night checklist, and troubleshooting. |

> For the assistant: these map to `show_panel` tabs: Home=`home`, Rigs=`equip`,
> Polar=`polar`, Sky=`sky` (Tonight=`tonight`, Weather=`weather`), Focus=`focus`,
> Guide=`guide`, Preview=`preview`, Autorun=`sequence`, Plan=`plan`, Live=`live`,
> Video=`video`, Adv=`seqadv`, Studio=`files`, Config=`settings`, Help=`help`.

## Task index

### Getting set up
- **Connect my gear** → **Rigs**: pick each device (camera/mount/focuser/filter
  wheel/guide camera), then Connect. Save the combination as a rig so it's one
  click next time.
- **Set my location / first-run** → the first-run setup prompts for it; later,
  **Config**.
- **Polar align** → **Polar**: choose TPPA or Rudimentary and follow the steps
  (it slews, captures, and solves to show your alignment error).

### Picking and framing a target
- **Find a target** → **Sky**: search or browse the map, click an object to
  select it; drag the FOV box to set rotation/framing.
- **What's good tonight** → **Sky → Tonight** (pill row at the top of Sky).
- **Check the weather/clouds** → **Sky → Weather**.
- **Go to and centre a target** → from **Sky**, use slew-and-centre (it slews,
  plate-solves, and re-centres).
- **Snap a quick test shot** → **Preview**: set exposure/gain and take one frame.
- **Plate-solve the current frame** → **Preview** (Plate solve button).

### Focus and guiding
- **Auto-focus** → **Focus**: run the autofocus sweep; it fits the V-curve and
  moves to best focus.
- **Focus by hand** → **Focus** (manual): watch HFR, or use the Bahtinov overlay.
- **Start guiding** → **Guide**: connect PHD2 (or the native guider), calibrate,
  then guide. The guide graph shows RMS.

### Capturing
- **Image one target unattended** → **Autorun**: set exposure, gain, filter, and
  frame count; press the shutter to start the schedule.
- **Image several targets across the night** → **Plan**: add targets with time
  windows and per-target exposure blocks, review, then start.
- **Live-stack (EAA)** → **Live**: start the loop and watch the stack grow;
  frames can be saved as you go.
- **Planetary / lucky imaging** → **Video**: capture at high fps, record SER,
  then stack the best frames.
- **Take flats** → **Autorun → Flat Wizard** (automated flats).
- **Complex, conditional sequences** → **Adv** (Advanced Sequencer).

### Processing (all under Studio)
- **Browse / open my files** → **Studio**: the file browser. Double-click an
  image (or the **View** button) to open the viewer.
- **Pick the best subs and stack them** → **Studio**: add lights, grade the
  frames, then integrate the keepers into a master.
- **Colour calibrate** → **Studio**: on a plate-solved RGB master, use
  **Color Cal (PCC)** or **SPCC**. (Needs WCS in the file: re-solve via
  **Studio → Solve** if matches are low. A white-balance summary chart appears
  when it finishes.)
- **Combine channels (LRGB / SHO)** → **Studio** (Combine).
- **Background extraction / denoise / deconvolution / upscale** → **Studio** AI
  tools (or the Editor's AI section).
- **Remove stars** → **Studio** (star removal), then blend back if wanted.
- **Edit (stretch, curves, crop, etc.)** → **Studio → Editor** (opens on an
  image).
- **Compare before/after** → the comparator that opens after a tool runs (use
  the **Linked / Independent stretch** toggle; for colour calibration keep it
  independent so the change shows).

### Config and help
- **Change the plate solver / downsample** → **Config → Plate solving**.
- **See colour-calibration data status (APASS / spectra / curves)** →
  **Config → Colour calibration data**.
- **Network / remote access / HTTPS / relay** → **Config**.
- **AI / ONNX model settings** → **Config**.
- **Tutorials and troubleshooting** → **Help**.

## If you're just lost
Start at **Home** for status, then follow the sidebar top-to-bottom, it's
ordered like a real session: **Rigs** (connect) → **Sky** (pick a target) →
**Focus** → **Guide** → **Autorun** or **Plan** (capture) → **Live** (watch) →
**Studio** (process). **Help** has a guided first-night checklist.
