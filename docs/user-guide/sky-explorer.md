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

## Catalogs bundled

Search + Atlas filter + Tonight's Best all draw from a SQLite +
R*tree-indexed bundle at `wwwroot/catalogs/dso/dso.db` (~2.6 MB,
~14.5k objects). Sources, with attribution:

| Catalog        | Entries | Source                                                   | License        |
|---             |---:     |---                                                       |---             |
| **NGC**        | ~7570   | [OpenNGC](https://github.com/mattiaverga/OpenNGC)        | CC BY-SA 4.0   |
| **IC**         | ~5000   | OpenNGC (same file)                                      | CC BY-SA 4.0   |
| **M** (Messier)| 107     | OpenNGC cross-reference (M-tagged duplicates)            | CC BY-SA 4.0   |
| **C** (Caldwell)| 104    | Embedded Caldwell↔NGC/IC mapping in the build script     | Public domain  |
| **Arp**        | 592     | CDS Vizier `VII/192A/arplist` (Arp 1966)                 | Public domain  |
| **Sh2**        | 313     | CDS Vizier `VII/20/catalog` (Sharpless 1959)             | Public domain  |
| **HCG**        | 100     | CDS Vizier `VII/213/groups` (Hickson 1982/89)            | Public domain  |
| **AGC**        | 767     | CDS Vizier `VII/110A/table3` (Abell-Corwin-Olowin 1989)  | Public domain  |

The AGC entry is magnitude-trimmed at m10 < 17 to keep the brightest
~30% of the 2712-cluster catalog — fainter clusters require deep
imaging beyond typical amateur reach.

To rebuild the bundle from the original sources, run:

```
python scripts/build-dso-catalog.py
```

Output overwrites `src/NINA.Polaris/wwwroot/catalogs/dso/dso.db`.
The script needs only Python 3.8+ stdlib (`urllib` + `sqlite3`);
no external dependencies. Cached downloads live in
`scripts/.dso-cache/` for fast re-runs.

When `dso.db` is missing (dev clone without the bundle), the SKY
tab silently falls back to a small ~150-object hardcoded list
(Messier complete + handful of popular NGC), so the app still works
but search hits like "NGC 7331" / "Arp 273" / "Sh2-279" come up
empty.

Full attribution + per-source license notes ship at
`wwwroot/catalogs/dso/LICENSE.txt`.

## Search

Top of tab: text input + Search button. Resolves names against the
bundled catalog (NGC / IC / M / C / Arp / Sh2 / HCG / AGC, plus
common names like "Andromeda"). Matches show as result cards with:

- **Name** + alternate designations
- **RA / Dec** (J2000)
- **Magnitude** + apparent size in arcmin
- **Object type** badge (Galaxy / Nebula / Cluster / ...)
- **Constellation**

Click a result → it overlays on the map, centred + highlighted.

## Filters

**Filters** button toggles a panel:

- **Catalog** dropdown — narrow to a single source (NGC / IC / M / C
  / Arp / Sh2 / HCG / AGC). Hidden when the expanded DB isn't loaded.
- **Object type** dropdown (Galaxy / Globular Cluster / HII Region /
  Peculiar Galaxy / Planetary Nebula / Supernova Remnant / ...). The
  list of types comes live from whatever's in the catalog.
- **Constellation** 3-letter IAU abbrev free-text ("Cyg", "Ori",
  "And", ...). Hidden when the expanded DB isn't loaded.
- **Magnitude range** Min/Max inputs
- **Dec range** Min/Max inputs in degrees (useful for filtering by
  hemisphere — set MinDec=0 to keep only northern targets, MaxDec=0
  for southern)

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
