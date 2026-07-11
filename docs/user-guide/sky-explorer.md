# SKY tab (Sky Explorer)

Offline sky map + target search + altitude planning + slew-and-center
orchestration.

## Sky map

[stellarium-web-engine](https://github.com/Stellarium/stellarium-web-engine)
running as a sandboxed WebGL2 sub-app (`/sky/`) inside an iframe.

The bundled `skydata/` (~4.6 MB, shipped in the repo + publish output)
covers, fully offline: Hipparcos/Tycho stars (the brighter naked-eye to
binocular range), the NGC/IC/Messier DSO catalog as labelled markers, IAU
**constellation lines + names** (on by default — the stick-figure overlay
you'd expect), the 88 western constellation **figure illustrations**
(toggleable artwork), a low-res Milky Way panorama, plus sun / moon /
planets / asteroids / comets.

**Real deep-sky imagery** (the "I can actually see the nebula/galaxy"
background, like ASIAIR) comes from the **DSS Color HiPS**. Two modes:

- **Online** (default if no local bundle): streamed on demand from CDS
  Strasbourg. Needs a connection.
- **Offline**: provision the DSS bundle once (see *Offline deep-sky
  imagery* below); the bridge auto-detects it and prefers it, so the rich
  sky works at the telescope with no network.

Drag to pan, mouse wheel / pinch to zoom. The view aims at whatever
the host UI tells it via postMessage (mount RA/Dec, search hit,
"Centre on selected target" buttons).

## Offline deep-sky imagery (DSS)

To make the sky show real imagery with **no internet at use time**,
download the DSS Color HiPS into the bundle once with the provisioning
script. Size scales ~4x per HEALPix order — pick the ceiling that fits
your SBC card:

| max order | tiles  | approx size | look                                  |
|---        |---:    |---:         |---                                    |
| 3         | ~1 020 | ~30 MB      | big objects recognisable, soft on zoom|
| 4         | ~4 100 | ~110 MB     | most DSOs recognisable (good value)   |
| 5         | ~16 400| ~400 MB     | detailed, ASIAIR-like                  |
| 6         | ~65 500| ~1.5 GB     | overkill for framing                  |

```bash
# Linux / macOS / Git-Bash  (args: MAX_ORDER [PARALLEL])
scripts/fetch-stellarium-dss.sh 4
```
```powershell
# Windows
pwsh scripts/fetch-stellarium-dss.ps1 -MaxOrder 4
```

The script is **resumable** (skips tiles already present, so re-run to
top up to a higher order) and writes to
`src/NINA.Polaris/wwwroot/sky/data/skydata/surveys/dss/`. That path is
tracked with **Git LFS** (see `.gitattributes`) so the binary tiles don't
bloat the working clone but still ship in `dotnet publish` / the
installer. Commit after fetching:

```bash
git add src/NINA.Polaris/wwwroot/sky/data/skydata/surveys/dss
git commit -m "skydata: bundle DSS Color HiPS (order 4)"
```

Attribution: DSS Color, STScI/NASA, HEALPixed by CDS Strasbourg.

> The repo ships order ≤3 as a baseline so the offline background works
> out of the box. Run the script to a higher order for ASIAIR-grade
> detail, or use **Settings → Sky imagery (offline DSS)** to download
> order 4 / 5 from the app without touching a script.

## DSO preview thumbnails

Search + Atlas result cards show a small **DSS2 cutout per object** (an
ASIAIR-style "photo per target") so you can tell a galaxy from a nebula
at a glance, fully offline. The repo bundles Messier + Caldwell (~206
images, ~3 MB) under `skydata/dso-thumbs/<SLUG>.jpg` (e.g. `M42.jpg`,
`C14.jpg`), tracked with Git LFS. The frontend derives the slug from each
result's catalog + id and hides the image when none is bundled.

Regenerate or widen coverage (e.g. add bright NGC/IC) with:

```bash
python scripts/build-dso-thumbs.py --catalogs M,C,NGC,IC --max-mag 11 --min-size 3
```

Resumable (skips existing). Source: DSS2 Color via the CDS hips2fits
service (STScI/NASA imagery). Distinct from the online `/api/sky/image`
NASA/Wikipedia lookup, which still works as a fallback when connected.

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

## Field of view overlays

Polaris draws the camera footprints on the map so you can frame before
slewing:

- **Blue rectangle** — the **mount** FOV (main camera), anchored where
  the scope is pointing. Sized from the active rig's focal length +
  the connected camera's sensor; rotated to the solved camera angle
  once a plate solve is available.
- **Red rectangle** — the **target** framing box. Screen-anchored
  (drag the map to compose) when idle; while imaging with a recent
  solve it snaps to the solved sky position so red converges on blue
  when you're framed correctly.
- **Pink rectangle** — the **aux camera** FOV, shown when an
  [Auxiliary Camera System](rigs.md#auxiliary-camera-system) is
  configured (aux focal length set + a sensor footprint). The sensor
  geometry is learned from the aux camera the first time it connects
  and remembered on the rig, so the rectangle keeps showing even
  before the aux is connected; for DSLRs it can also be filled in
  manually via the aux pixel/size fields. It starts out **concentric
  with the red target box** — main and aux ride the same mount, so
  they're assumed co-pointed — and moves to the aux camera's real sky
  position once an aux plate solve reports how far off the two
  actually are (see below).
- **Yellow rectangles** — the [mosaic](#mosaic-planner) panels.

### Confirming the aux camera framing

When you run a plate solve from SKY (**Solve & Sync**), Polaris fires a
**parallel solve on the aux camera** if it's connected — it captures
one aux frame (aux exposure / gain / binning) and solves it on its own
hardware, concurrently with the main solve. The pink rectangle then
snaps onto the aux's **real solved rotation + scale**, so you know for
certain the field and angle the aux photo will come out with instead of
assuming it matches the mount. A toast reports the solved aux rotation.

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

## Center on Sun / Moon / planet

Plate solving can't lock onto solar-system objects — the Sun/Moon wash the
frame out, and a planet shot (long focal length, millisecond exposures) has no
background stars to match. So Slew & Center fails on them. The **Center on
body** picker on the map handles them with a *solve-near-and-offset* strategy:

1. Pick **Moon / Sun / a planet** from the dropdown and click 🪐 **Center on
   body** (mount must be connected).
2. Polaris computes the object's apparent topocentric position from its built-in
   ephemeris (your profile location + clock).
3. It slews a few degrees off to a **nearby star field** and runs the normal
   plate-solve + sync there — correcting the mount's pointing model right next
   to the target, without ever solving the object itself.
4. It re-reads the ephemeris (the Moon moves ~0.5°/h) and does a precise GoTo
   onto the object. For the Moon/Sun it then switches the mount to **lunar /
   solar tracking** so the object stays centred.

The phase chip shows progress (Computing position → Solving nearby field →
Slewing to target → Centered), and the offset-field solve streams to the SKY
solver console like any other solve.

> **⚠ Sun:** selecting the Sun pops a confirmation — only proceed with a
> certified full-aperture solar filter fitted. An unfiltered scope on the Sun
> destroys the camera instantly and can cause permanent eye damage. No software
> can protect against this.

Needs a connected mount, camera, and a working plate solver (for the offset
field). If the nearby field won't solve, raise the offset or pick a clearer
patch of sky.

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
