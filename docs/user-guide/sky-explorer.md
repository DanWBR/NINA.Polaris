# SKY tab (Sky Explorer)

Offline sky map + target search + altitude planning + slew-and-center
orchestration.

## Sky map

[stellarium-web-engine](https://github.com/Stellarium/stellarium-web-engine)
running as a sandboxed WebGL2 sub-app (`/sky/`) inside an iframe. Renders
Gaia stars (down to mag 16), DSO surveys with image overlays, IAU
constellation art + names in multiple cultures, atmosphere/horizon, sun
+ moon + planets + asteroids, and HiPS Milky Way tiles. Fully offline
when the skydata bundle is present (bundled with publish by default;
~300 MB on disk).

Drag to pan, mouse wheel / pinch to zoom. The view aims at whatever
the host UI tells it via postMessage (mount RA/Dec, search hit,
"Centre on selected target" buttons).

> **Browser requirement.** WebGL2 is mandatory. On a host with no
> WebGL2 (e.g. running Polaris's local browser on a Raspberry Pi 2
> framebuffer), the SKY tab shows a graceful fallback banner,
> open Polaris from a desktop/laptop/tablet browser instead.

## Search

Top of tab: text input + Search button. Resolves names against the
built-in catalog (Messier, NGC, IC, common names). Matches show as
result cards with:

- **Name** + alternate designations
- **RA / Dec** (J2000)
- **Magnitude** + apparent size in arcmin
- **Object type** badge (Galaxy / Nebula / Cluster / ...)
- **Constellation**

Click a result → it overlays on the map, centred + highlighted.

## Filters

**Filters** button toggles a panel:

- **Constellation** dropdown
- **Object type** multi-select
- **Magnitude range** slider
- **Size range** slider
- **Min altitude tonight**, only show targets above N° during the
  upcoming dark window

## Tonight's altitude chart

Once a target is selected, the bottom of the SKY tab shows altitude
vs UTC time with:

- **Twilight bands** (astronomical / nautical / civil)
- **Moon altitude** overlay
- **Best window** highlight where target is highest

## Slew & Center

The big workflow button:

1. Click 🎯 **Slew & Center** on a selected target
2. Polaris commands the mount to the target's RA/Dec
3. Captures a plate-solve frame (5s exposure default)
4. Solves it (ASTAP primary, falls back to PlateSolve3 / Astrometry.net
   online / local)
5. Computes the offset from intended
6. Re-slews to correct, repeat up to 5 iterations until within tolerance
   (30 arcsec default)

Status banner shows phase live: "Slewing → Capturing → Solving →
Centering → ✓ Centered (12 arcsec error)".

## Mosaic planner

Click 🧩 **Plan mosaic** with a target selected:

- Grid N×M of panels overlaid on the map
- Settings: panels per axis, overlap %, total grid size
- cos(δ) correction so panels at high Dec don't stretch
- Estimated session time = panels × exposure × frames
- **Add to Sequence** generates the AUTORUN rows for all panels in
  serpentine slew order

## Stellarium sync

If you have Stellarium open with the Remote Control plugin:

1. Click **📥 Get from Stellarium**
2. Polaris fetches Stellarium's current selection via HTTP
3. Auto-populates the search box with the (RA, Dec, name)

## Slew preview (background feature)

While the mount is slewing AND nothing is capturing, an inset card
appears in the lower-right showing a live camera feed. Lets you watch
the field sweep past during goto.

Driven by `SlewPreviewService`, auto-on by default, polite to other
camera consumers (silently yields when sequence / AF / preview / video
recording grabs the camera).

## Common pitfalls

**Search returns nothing**, catalog isn't loaded. Refresh the page.

**Slew & Center fails repeatedly**, see
[Troubleshooting → Plate solve fails](troubleshooting.md#plate-solve-fails).

**Mosaic panels overlap wrong**, your rig's focal length / sensor
size is wrong. Re-pick the OTA from the catalog in the RIGS tab.

## See also

- [Tonight's Best](tonight.md), ranked best DSOs / Moon / planets
  for the current observing window
- [Glossary → Plate solve / FOV](GLOSSARY.md#a)
