# FAQ

Quick-answer questions. For longer diagnostics, see
[Troubleshooting](troubleshooting.md).

## General

**Q: Is Polaris a NINA replacement?**

A: No, it's a community fork specifically for **headless** operation
(Linux, RPi, Windows mini-PC without a display). The "real" NINA at
nighttime-imaging.eu is a full Windows desktop app and remains the
gold standard for that platform. Polaris is for remote-site / SBC
use cases NINA doesn't target.

**Q: Does Polaris share code with NINA?**

A: Some, but heavily forked. We've stripped the Windows-only WPF UI
layer + replaced with ASP.NET Core + Alpine.js. Core libraries (FITS
reader, star detection, Plate-solving) retain heritage from NINA but
have diverged. License remains MPL 2.0.

**Q: Will my NINA sequences work?**

A: No automatic import. The Advanced Sequencer JSON format is similar
but not bit-compatible. Rebuilding sequences in Polaris's ADV tab is
the right path; it doesn't take long for typical multi-target plans.

## Hardware

**Q: What cameras work?**

A: Any camera with an INDI driver (most ZWO, QHY, Atik, Touptek, ToupSky,
Altair, gphoto-supported DSLRs, etc.), Alpaca cameras on Windows, and
DSLRs via vendor SDKs (Canon EDSDK, Nikon Imaging SDK, Sony Camera
Remote SDK 2). See RIGS → Camera card → Driver dropdown for what
Polaris detected on your host.

**Q: What mounts work?**

A: INDI: EQMod (CGEM/EQ6/AVX/HEQ5/Atlas), Celestron NexStar, iOptron,
Sky-Watcher, LX200. Alpaca: anything with an ASCOM driver. Direct
WiFi: SynScan, NexStar, LX200 TCP.

**Q: Raspberry Pi 4 vs 5?**

A: Pi 4 (4GB+) handles capture + live stacking fine. Pi 5 is faster
for planetary processing + xpra-embedded PHD2 GUI (3x improvement in
xpra rendering). Pi 3 is too slow for live stacking.

**Q: Does Polaris work over WiFi to a remote rig?**

A: Yes, INDI can run on the Pi while Polaris itself runs on a
different machine. Just set `Indi:Host` in `appsettings.json` to the
remote Pi's IP. But this is fragile (USB drivers want to be physically
co-located with the camera). The reference deploy is Polaris + INDI
on the same SBC at the telescope.

## Workflow

**Q: AUTORUN vs ADV, which should I use?**

A: AUTORUN for "shoot 100 frames of M81 in L tonight". ADV for "M81
in LRGB, with auto-flip + dither every 3 + safety abort on cloud
cover". See [AUTORUN](autorun.md) vs [ADV](adv-sequencer.md).

**Q: Can I edit sequences while they're running?**

A: AUTORUN: limited, pause, edit, resume. ADV: same, with the caveat
that some Container types lock their children during execution.

**Q: Do I have to focus before every sequence?**

A: First time of the night, yes. After that, enable AF triggers (in
ADV) or the LIVE tab's auto-refocus and Polaris handles it
automatically based on temperature / HFR / time / frame count.

**Q: What's the difference between LIVE auto-recenter and the ADV
"Center After Drift" trigger?**

A: LIVE's recenter is scoped to the live stack, uses the first-frame
plate-solve as reference, fires per-frame solves if drift threshold
enabled. ADV's "Center After Drift" trigger fires within a sequence
between exposures, uses the AUTORUN target's intended (RA, Dec) as
reference. They serve different workflows; both are equally valid.

## Storage

**Q: How much disk for a typical session?**

A: DSO capture: 30-50 MB per FITS frame. 200 frames = 6-10 GB.
Planetary: 1.5-3 GB per 60-second SER. Calibration masters: trivial.
Plan for 50+ GB free for a full night.

**Q: Can I store to a network share / NAS?**

A: Yes, FILES tab → navigate to the mount point (e.g.
`/mnt/nas/astro/`) → Set as Studio root. INDI BLOB writes will be
slightly slower over network than local SSD; live stacking is
unaffected (in-memory accumulation).

**Q: Where are my profiles stored?**

A: `{AppData}/Polaris/profile.json` (Windows: `%LocalAppData%`, Linux:
`~/.config/Polaris/`). Back this up before major OS upgrades, losing
it loses your rig configurations.

## Software

**Q: Why .NET 10?**

A: Faster runtime + better cross-platform performance + first-class
async/await + native ARM64 publish. .NET 8 LTS would work too; we
track the latest STS for performance.

**Q: Why ASP.NET Core + Alpine.js instead of React / Vue?**

A: Server-side rendering of the HTML + minimal JS framework keeps the
download tiny (~150 KB before WebGL2 shaders), fast first-paint on
mobile, and no build pipeline needed for contributors. Alpine.js gives
us reactivity without webpack/Vite/Bun complexity. The tradeoff is
less suited for a complex SPA, but Polaris is a multi-tab dashboard
not a CRUD app.

**Q: How do I see live logs?**

A: Server stdout. Run from a terminal to see them. Set
`Logging:LogLevel:Default = "Debug"` in `appsettings.json` for verbose.

**Q: Can I write plugins?**

A: There's a plugin system (`Services/Plugins/PluginLoaderService.cs`)
that loads MEF-exported types from `plugins/` at startup. Spec is
loose right now; if you want to write one, open an issue + we'll
help bootstrap.

## Network

**Q: I'm at a dark site with no internet, does Polaris work?**

A: Yes. Sky catalog + stellarium-web-engine map + DSO database are all
offline (the bundled HiPS skydata covers stars to ≥ mag 12, DSO
surveys, constellations, and Milky Way tiles).
Plate solving via ASTAP is offline. PHD2 + INDI are local. The only
internet-dependent feature is the Tonight's Best image fetcher (pulls
NASA/Wikipedia thumbs), it just shows placeholders offline.

**Q: Can multiple browsers connect to the same Polaris?**

A: Yes. Each browser gets its own WebSocket streams. State is server-
side so all clients see the same view. Useful for "show astrophotography
buddy what we're seeing".

## Astrophotography questions

**Q: Polaris detects 0 stars on focused frames.**

A: Exposure is too short or sky is too bright (twilight). Bump to 5s+
and try at full astronomical darkness.

**Q: HFR jumps by 30% after meridian flip, that's not normal, is it?**

A: It can be. Pier-side change rotates the field; star detection sees
"new" stars and the median shifts temporarily. If it persists more
than 3-5 frames, something else (collimation, dew, flexure) is wrong.

**Q: Polaris's plate solve uses ASTAP but I have Astrometry.net set
up, can I use that?**

A: Settings → Plate solver → Primary solver dropdown → "Astrometry.net
local". You'll need `solve-field` in PATH + the matching index files.

**Q: How accurate is the dithering?**

A: PHD2's dither is sub-pixel accurate. Polaris just triggers it +
waits for settle. The dither_amount you configure is in guide-cam
pixels; main cam motion depends on the focal ratio of guide vs main.

## See also

- [Troubleshooting](troubleshooting.md), longer diagnostic walkthroughs
- [Glossary](GLOSSARY.md), term definitions
