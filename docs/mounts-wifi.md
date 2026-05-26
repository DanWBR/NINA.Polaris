# Wi-Fi and Bluetooth mounts

Polaris already drives the vast majority of Wi-Fi-capable telescope
mounts on the market through its INDI client, `indiserver` runs on
the same host (RPi / mini-PC) and the INDI mount driver speaks
Wi-Fi on Polaris's behalf. This doc maps which mounts already work
that way, and which direct (no-indiserver) drivers are in the
backlog.

## Mounts that already work today via INDI

Pick driver = **INDI** in the Equipment → Mount card, then connect.
No extra Polaris setup required beyond the INDI driver package.

| Brand | Bodies | INDI driver |
|---|---|---|
| Sky-Watcher | AZ-GTi, AZ-EQ5, EQ5-Pro, HEQ5 Pro, EQ6-R Pro, EQ8-R Pro, AZ-EQ6, GoTo Dobsonians, AllView | `indi_skywatcherAltAzMount` / `indi_eqmod_telescope` |
| Celestron | CGX, CGX-L, CGEM II, AVX, NexStar SE/Evolution, CPC, StarSense Explorer | `indi_celestron_aux` / `indi_celestron_gps` |
| iOptron | CEM26, CEM40 (+EC), CEM70 (+EC), CEM120, HEM27, HEM44, HEM47, GEM28, GEM45, SkyTracker, SkyGuider Pro | `indi_ioptron_v3` |
| Meade | LX200 / LX200GPS, LX600, LX850 (with Stella Wi-Fi adapter) | `indi_lx200gps` / `indi_lx200_OnStep` |
| Astro-Physics | 1100 GTO, 1600 GTO, Mach1, Mach2 | `indi_lx200_OnStep` (Keypad) / `indi_apgto` |
| 10Micron | GM-1000, GM-2000, GM-3000, GM-4000, AZ-2000 | `indi_lx200_10micron` |
| Vixen | SXP, SXP2, AXJ, SXD2 | `indi_lx200_classic` |
| Losmandy | G-11, G-11G, GM-8, GM-811G | `indi_lx200gps` |
| ZWO | AM3, AM5, AM5N | `indi_skywatcherAltAzMount` (compatible protocol) |
| StarAid | Revolution standalone autoguider, pairs with mount via the standalone STA-1 cable | `indi_celestron_aux` |
| Generic | Anything that speaks LX200 / NexStar / SynScan over TCP/IP | `indi_lx200generic` |

On Windows Polaris also accepts ASCOM-Alpaca mounts (the
manufacturer's official driver, Celestron PWI, SkyWatcher SynScan,
iOptron Commander, typically exposes ASCOM, which Alpaca bridges
to HTTP that Polaris consumes).

## Setup walkthrough (Sky-Watcher AZ-GTi example)

1. On the AZ-GTi: switch the small slider on the side to **Wi-Fi
   AP mode** (this is the factory default).
2. From the Polaris host, join the `SynScan_xxxx` network broadcast
   by the mount.
3. Install the INDI gphoto + EQMod or AltAz driver package on the
   host:

   ```bash
   sudo apt install indi-eqmod indi-skywatcherAltAzMount
   ```
4. Start indiserver pointing at the WiFi driver:

   ```bash
   indiserver -v indi_skywatcherAltAzMount
   ```
   …or use INDI Web Manager to start a profile that includes it.
5. In Polaris **Equipment → Mount card**, pick `INDI` in the driver
   dropdown, then choose `SkyWatcher Alt-Az WiFi` (or whatever the
   driver name reports) and click **Connect**.

Same recipe with `indi_celestron_aux` for Celestron WiFi accessories
(SkyPortal, StarSense Explorer), and `indi_ioptron_v3` for iOptron
mounts with built-in WiFi.

## Direct WiFi drivers (no indiserver), backlog

For users who'd rather skip the indiserver step (or who run Polaris
on a host without it), Polaris's `ITelescope` abstraction has slots
for direct drivers. None of these ship in the current build, they're
listed as "(not installed)" in the driver dropdown and this doc is
where the work tracks.

### SynScan Wi-Fi (UDP), Sky-Watcher

- **Protocol:** Sky-Watcher SynScan Wi-Fi UDP, port `11880`.
- **Bodies:** anything with a SynScan-compatible handset or the
  Wi-Fi adapter / built-in Wi-Fi (AZ-GTi, AllView, EQ8-R Pro,
  EQM-35 Pro, AZ-EQ6 GT with WiFi accessory, GoTo Dob).
- **Auth:** none, anyone on the WiFi network can drive the mount.
- **API shape:** ASCII command frames sent over UDP.
  `:e1` (get RA/Dec), `:Sr / :Sd` (set target), `:MS` (slew),
  `:Q` (abort), `:Mn / :Ms / :Me / :Mw` (jog), etc. Similar to the
  Meade LX200 serial protocol, just over UDP.
- **Reference doc:** Sky-Watcher Synscan Wi-Fi protocol PDF
  (published by Sky-Watcher on their developer site).
- **Effort:** ~1 day. Pure UDP `System.Net.Sockets.UdpClient`, no
  native deps. Cross-platform.

### NexStar Wi-Fi (TCP), Celestron

- **Protocol:** Celestron NexStar serial protocol wrapped in TCP,
  port `2000`.
- **Bodies:** any mount with the SkyPortal WiFi accessory or
  StarSense Explorer WiFi dongle (CGX, CGEM II, AVX, NexStar SE,
  NexStar Evolution).
- **Auth:** none.
- **API shape:** raw NexStar serial bytes, `e` (get RA/Dec
  precise), `s` (sync), `r` (slew to RA/Dec precise), `M` (cancel),
  `P` (move + axis + rate). One byte per command, fixed-length
  responses.
- **Effort:** ~1 day. Pure TCP, no native deps. Cross-platform.

### LX200 TCP, Meade / generic

- **Protocol:** LX200 ASCII over TCP (port varies, typically 4030
  on the Stella WiFi adapter; 4000 with most OnStep / SkySafari
  bridges).
- **Bodies:** Meade LX200 / LX600 / LX850 with the Meade Stella
  WiFi accessory, plus dozens of clones / OnStep builds / Pegasus
  NYX-101 / iOptron iEQ-series with the iOS-style hand controller.
- **Effort:** ~half a day after SynScan lands (the protocol is
  similar, same `:GR/:GD/:Sr/:Sd/:MS/:Q` command set).

### Alpaca

- **Protocol:** ASCOM-over-HTTP, the same standard the Alpaca
  camera + filter wheel paths already use.
- **Bodies:** any mount with an ASCOM driver, exposed over HTTP via
  ASCOM Remote (Windows) or a third-party Alpaca server.
- **Effort:** ~1 day, mostly mirroring the Alpaca Camera client
  pattern.

## Bluetooth, out of scope

The current mount market is essentially Wi-Fi-only. Bluetooth
support shipped on a handful of older bodies (Celestron NexStar SE
with the optional SkyAlign BT module, a few SynScan Hand-Controller
v3 firmwares) and the device picker on the BT side never standardised.
Modern bodies dropped BT entirely.

If a real user shows up needing BT, the path would be:
1. Pick a target body (likely a NexStar SE with BT module).
2. Use Tmds.MDns or 32feet.NET for BT discovery on Windows;
   BlueZ via `Linux.Bluetooth` on Linux. Not cross-platform out of
   the box.
3. Wrap the same NexStar serial protocol over the RFCOMM stream.

Not on the roadmap. Wi-Fi covers every mount worth tracking.

## How to pick

| Setup | Recommended driver |
|---|---|
| RPi / mini-PC with indiserver running | **INDI** (one driver list to maintain, every mount supported) |
| Windows with ASCOM installed | **Alpaca** (when it lands) |
| Linux / Mac / tablet, no indiserver, no ASCOM, just Polaris | **synscan-wifi** / **nexstar-wifi** / **lx200-tcp** (when they land), direct UDP/TCP to the mount |
| Anything else | INDI on the host, period |
