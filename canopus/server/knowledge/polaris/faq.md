# Frequently Asked Questions

The questions that come up a lot, with short answers and pointers
into the chapters that go deeper. For failure symptoms and
diagnostics, see the "Troubleshooting" chapter.

## Frequently Asked Questions / General

**Is Polaris a N.I.N.A. replacement?**
No. It is a community fork built specifically for headless operation
(Linux, Raspberry Pi, or a Windows mini-PC without a display). The
original N.I.N.A. at nighttime-imaging.eu is a full Windows desktop
application and remains the gold standard on that platform
[@berg_nina]; Polaris targets the remote-site and single-board-
computer use cases it does not (the "What is Polaris?" chapter).

**Does Polaris share code with N.I.N.A.?**
Some, but heavily forked. The Windows-only WPF UI layer is gone,
replaced by ASP.NET Core and Alpine.js. Core libraries (the FITS
reader, star detection, plate solving) retain N.I.N.A. heritage but
have diverged. The license remains MPL 2.0.

**Will my N.I.N.A. sequences work?**
There is no automatic import. The Advanced Sequencer's JSON format is
similar but not bit-compatible; rebuilding sequences in the **ADV**
tab is the right path, and it does not take long for typical
multi-target plans (the "The Advanced Sequencer" chapter).

## Frequently Asked Questions / Hardware

**What cameras work?**
Any camera with an INDI driver (most ZWO, QHY, Atik, ToupTek,
ToupSky, Altair, and gphoto-supported DSLRs, among others), Alpaca
cameras on Windows, and DSLR or mirrorless bodies through the vendor
SDKs (Canon EDSDK, Nikon Imaging SDK, Sony Camera Remote SDK 2). The
driver dropdown on the camera card in **RIGS** shows what Polaris
detected on your host (the "Rigs and Device Drivers" chapter, the "DSLR and Mirrorless Cameras" chapter).

**What mounts work?**
Via INDI: EQMod (CGEM, EQ6, AVX, HEQ5, Atlas), Celestron NexStar,
iOptron, Sky-Watcher, and LX200. Via Alpaca: anything with an ASCOM
driver. Via direct WiFi: SynScan, NexStar, and LX200 over TCP
(the "Mounts and WiFi Accessories" chapter).

**Raspberry Pi 4 or 5?**
A Pi 4 with 4 GB or more handles capture and live stacking fine. The
Pi 5 is faster for planetary processing and the xpra-embedded PHD2
GUI (about three times faster xpra rendering). A Pi 3 is too slow for
live stacking. The full sizing table is in the "Requirements Matrix" chapter.

**Can Polaris and the equipment be on different machines?**
Yes: INDI can run on a Pi at the telescope while Polaris runs
elsewhere; set `Indi:Host` in `appsettings.json` to the remote Pi's
address (the "API and Configuration" chapter). But this is fragile, because USB drivers want to
be physically next to the camera; the reference deployment is Polaris
and INDI together on the same computer at the telescope.

**I need the INDI control panel but my Pi OS no longer packages it.**
Use the built-in **INDI control panel** sub-tab in **RIGS**. It is a
full in-browser property browser (read and edit every device
property, auto-saved to the driver configuration), so the standalone
Qt application, which recent Pi OS and libindi 2.x releases no longer
ship, is not needed. Each property also has a **(?)** help icon:
hover for a plain-language description, click to write your own note
(the "Rigs and Device Drivers" chapter).

## Frequently Asked Questions / Workflow

**AUTORUN or ADV, which should I use?**
AUTORUN for "shoot 100 frames of M81 in L tonight". ADV for "M81 in
LRGB, with auto-flip, dither every 3 frames, and a safety abort on
cloud cover". See the "Simple Sequences and the Flat Wizard" chapter and the "The Advanced Sequencer" chapter.

**Can I edit a sequence while it runs?**
In AUTORUN, in a limited way: pause, edit, resume. In ADV the same,
with the caveat that some container types lock their children during
execution.

**Do I have to focus before every sequence?**
The first time each night, yes. After that, enable auto-focus
triggers (in ADV) or the LIVE tab's auto-refocus, and Polaris
refocuses automatically on temperature change, HFR degradation,
elapsed time, or frame count (the "Focusing" chapter, the "Live Stacking and EAA" chapter).

**What is the difference between LIVE auto-recenter and the ADV
"Center After Drift" trigger?**
LIVE's recenter is scoped to the live stack: it uses the first
frame's plate solve as its reference and can solve every frame if the
drift threshold is enabled. ADV's trigger fires within a sequence
between exposures and uses the target's intended (RA, Dec) as the
reference. They serve different workflows; both are equally valid
(the "Live Stacking and EAA" chapter, the "The Advanced Sequencer" chapter).

## Frequently Asked Questions / Storage

**How much disk does a typical session take?**
Deep-sky capture: 30-50 MB per FITS frame, so 200 frames is 6-10 GB.
Planetary: 1.5-3 GB per 60-second SER video. Calibration masters are
trivial. Plan for 50+ GB free for a full night (the "Managing Your Data" chapter).

**Can I store to a network share or NAS?**
Yes: in the **FILES** tab, navigate to the mount point (for example
`/mnt/nas/astro/`) and click **Set as Studio root**. Writing frames
is slightly slower over the network than to a local SSD; live
stacking is unaffected because it accumulates in memory (the "Managing Your Data" chapter).

**Where are my profiles stored?**
In `profile.json` under the per-user application data folder:
`%LocalAppData%\Polaris\` on Windows, `~/.config/polaris/` on Linux.
Back it up before major OS upgrades; losing it loses your rig
configurations (the "Administration and Diagnostics" chapter).

**How do I wipe all my settings, say before sharing an SD card?**
In **SETTINGS**, use **Reset everything to factory defaults**. It
clears every profile and rig, the app password and login sessions,
the observer location, and the browser's UI preferences, then reloads
into first-run state. Captured images and FITS files are kept. It
cannot be undone, so back up the profile first if you might want it
later (the "Administration and Diagnostics" chapter).

## Frequently Asked Questions / Software

**Why .NET 10?**
A faster runtime, better cross-platform performance, first-class
async support, and native ARM64 publishing. .NET 8 LTS would work
too; the project tracks the latest short-term-support release for
performance (the "Architecture" chapter).

**Why ASP.NET Core and Alpine.js instead of React or Vue?**
Server-side rendering of the HTML plus a minimal JavaScript framework
keeps the download tiny (about 150 KB before the WebGL2 shaders),
first paint fast on mobile, and the contributor experience free of
any build pipeline. Alpine.js provides reactivity without the
complexity of a bundler. The trade-off is less suitability for a
complex single-page app, but Polaris is a multi-tab dashboard, not a
CRUD app (the "Architecture" chapter).

**How do I see live logs?**
The server logs to standard output; run it from a terminal to watch.
Set `Logging:LogLevel:Default` to `"Debug"` in `appsettings.json` for
verbose output. The LOG panel in the UI collects server logs, HTTP
traffic, and browser errors into one exportable stream
(the "Administration and Diagnostics" chapter).

**Can I write plugins?**
There is a plugin system (`Services/Plugins/PluginLoaderService.cs`)
that loads MEF-exported types (MEF is .NET's built-in extension
loader) from the `plugins/` folder at startup. The specification is
loose right now; if you want to write one, open an issue and the
project will help you bootstrap (the "Building and Contributing" chapter).

## Frequently Asked Questions / Network

**I am at a dark site with no internet. Does Polaris work?**
Yes. The sky catalog, the map engine, and the DSO database are all
offline (the bundled sky tiles cover stars to at least magnitude 12,
deep-sky surveys, constellations, and the Milky Way). Plate solving
via ASTAP is offline, and PHD2 and INDI are local. The only
internet-dependent feature is the Tonight's Best image fetcher, which
pulls NASA and Wikipedia thumbnails and simply shows placeholders
offline (the "Sky Explorer and Session Planning" chapter, the "Requirements Matrix" chapter).

**Can multiple browsers connect to the same Polaris?**
Yes. Each browser gets its own WebSocket streams, and state lives on
the server, so all clients see the same view. Useful for showing an
astrophotography buddy what the rig is seeing
(the "What is Polaris?" chapter).

## Frequently Asked Questions / On the images themselves

**Polaris detects 0 stars on focused frames.**
The exposure is too short or the sky is too bright (twilight). Bump
the exposure to 5 s or more and try again at full astronomical
darkness.

**HFR jumps by 30% after a meridian flip. That is not normal, is
it?**
It can be. The pier-side change rotates the field; star detection
sees "new" stars and the median shifts temporarily. If it persists
more than 3-5 frames, something else (collimation, dew, flexure) is
wrong.

**Plate solving uses ASTAP, but I have Astrometry.net set up. Can I
use it?**
Yes: in **SETTINGS**, under **Plate solver**, set the primary solver
to **Astrometry.net local**. You need `solve-field` on the PATH and
the matching index files (the "Requirements Matrix" chapter).

**How accurate is the dithering?**
PHD2's dither is sub-pixel accurate; Polaris just triggers it and
waits for settling. The dither amount you configure is in guide-
camera pixels; how far the main camera moves depends on the ratio of
guide to main focal lengths (the "Autoguiding" chapter).
