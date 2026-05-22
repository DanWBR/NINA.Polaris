# SKY tab (Sky Explorer)

Offline sky map + target search + altitude planning + slew-and-center
orchestration.

## Sky map

d3-celestial-powered, fully offline (Hipparcos catalog through magnitude
6, Stellarium constellation lines, IAU named-star database, DSO catalog,
Milky Way contours). Two modes:

- **Local sky (default)** — projection from your observer location at
  current UTC. Horizon mask + 30-second ticker that re-centres on
  zenith. Shows what's actually visible to you right now.
- **Equatorial chart** — full-sphere RA/Dec grid. Use for planning
  below-horizon targets.

Toggle modes via the **🌍/⭐** button in the toolbar.

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
- **Min altitude tonight** — only show targets above N° during the
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

Driven by `SlewPreviewService` — auto-on by default, polite to other
camera consumers (silently yields when sequence / AF / preview / video
recording grabs the camera).

## Common pitfalls

**Search returns nothing** — catalog isn't loaded. Refresh the page.

**Slew & Center fails repeatedly** — see
[Troubleshooting → Plate solve fails](troubleshooting.md#plate-solve-fails).

**Mosaic panels overlap wrong** — your rig's focal length / sensor
size is wrong. Re-pick the OTA from the catalog in the RIGS tab.

## See also

- [Tonight's Best](tonight.md) — ranked best DSOs / Moon / planets
  for the current observing window
- [Glossary → Plate solve / FOV](GLOSSARY.md#a)
