# Installation

Polaris ships as a single .NET 10 executable that needs an `indiserver`
running on the same machine (or reachable on the LAN). Three deploy
paths are supported:

## Raspberry Pi 4/5 (linux-arm64), recommended

The reference platform. Pi 4 is fine for most workloads; Pi 5 is faster
for live stacking + planetary processing.

### 1. Install prerequisites

```bash
sudo apt update
sudo apt install indi-bin indi-full        # INDI server + all drivers
sudo apt install dotnet-runtime-10.0       # .NET 10 runtime (or SDK if building from source)
sudo apt install astap                     # plate solver (recommended)
# Optional but useful:
sudo apt install phd2                      # autoguider, Polaris controls it
sudo apt install siril graxpert            # post-processing tools, Polaris invokes their CLIs
```

For the embedded PHD2 GUI feature, see
[docs/phd2-gui-embedding.md](../phd2-gui-embedding.md) (extra `xpra +
Xorg-dummy` setup).

### 2. Get Polaris

Three options:

**A. Pre-built release** (when available):
```bash
curl -L https://github.com/DanWBR/nina-polaris/releases/latest/download/polaris-linux-arm64.tar.gz | tar xz
cd polaris-linux-arm64
./NINA.Polaris
```

**B. Docker** (if `docker` is installed):
```bash
docker run -d --name polaris \
  --network host \
  -v /home/pi/astro:/data \
  -v $HOME/.config/polaris:/root/.config \
  ghcr.io/danwbr/nina-polaris:latest
```

The `--network host` is critical, INDI's BLOB-streaming over loopback
is much faster than a bridge.

**C. Build from source**:
```bash
git clone https://github.com/DanWBR/nina-polaris.git
cd nina-polaris
dotnet publish src/NINA.Polaris/NINA.Polaris.csproj \
  -c Release -r linux-arm64 --self-contained
./src/NINA.Polaris/bin/Release/net10.0/linux-arm64/publish/NINA.Polaris
```

### 3. Start INDI server with the drivers you need

```bash
# Example: ZWO camera + EQMod mount + ZWO EAF focuser
indiserver -v indi_asi_ccd indi_eqmod_telescope indi_asi_focuser
```

For permanent setup, install [indiwebmanager](https://github.com/knro/indiwebmanager)
so INDI starts at boot with a web UI to pick drivers.

### 4. Open the web UI

Polaris listens on port 5000 by default. From any device on the same
LAN:

```
http://<pi-ip>:5000
```

mDNS is wired automatically, if your network supports it (any modern
router), `http://nina.local:5000` works too.

## Linux x64 (mini-PC, NUC, NAS)

Same as RPi but use the `linux-x64` binary / Docker tag. Performance
is significantly better; live stacking can integrate per-second-cadence
frames without dropping.

## Windows mini-PC

Polaris runs on Windows but with a few caveats:

- **INDI is Linux-first**. On Windows, use the `indi_*` drivers via
  WSL2 (Polaris connects to `localhost:7624` regardless of where
  `indiserver` runs) OR use ASCOM via Alpaca instead (see below).
- **xpra-embedded PHD2 GUI is not supported on Windows**, you'll
  interact with PHD2's native window directly.

### ASCOM via Alpaca

If your gear has ASCOM drivers (typical for Windows-native
astrophotography), install:

1. [ASCOM Platform 6.6+](https://ascom-standards.org/Downloads/Index.htm)
2. [ASCOM Remote Server](https://github.com/ASCOMInitiative/ASCOMRemote/releases)

In Remote Server: pick your devices, click Start Server. Polaris's
RIGS tab → Driver picker → ASCOM/Alpaca → Discover → your devices
show up.

### Pre-built release

```powershell
# Download + extract polaris-win-x64.zip from releases
cd polaris-win-x64
.\NINA.Polaris.exe
```

By default the app appears at `http://localhost:5000` and other LAN
devices can hit `http://<windows-host-ip>:5000`.

## DSLR / Mirrorless camera support

DSLRs have separate per-vendor docs:

- [Canon EDSDK](../dslr-windows-canon.md)
- [Nikon SDK](../dslr-windows-nikon.md)
- [Sony Camera Remote SDK](../dslr-windows-sony.md)
- [Linux gphoto](../dslr-linux.md) (any USB-tethered DSLR)

## Configuring image output location

Polaris saves captured FITS/XISF to a per-rig folder under
`ImageOutputDir`. The default is `%USERPROFILE%/Pictures/Polaris` on
Windows and `$HOME/Pictures/Polaris` on Linux. Change it via the FILES
tab, navigate to the folder you want and click "Set as Studio root".

The folder structure is:
```
{ImageOutputDir}/
  {RigName}/
    lights/{Target}/{Filter}/{ISO-timestamp}/
    calibration/dark/dark_{ExposureSec}s_{Gain}_{Temp}C.fits
    calibration/flat/{Filter}/flat_{Timestamp}.fits
    calibration/bias/bias_{Timestamp}.fits
    snaps/{Filter}_{Date}/          ← from PREVIEW tab with Save toggle
    planetary/{Target}/{ts}.ser     ← from VIDEO tab
    siril/                          ← Siril output
    bge/                            ← GraXpert BGE output
```

## Next: first-night setup

Once Polaris is up and the UI responds at `http://<host>:5000`, go
through [First-night setup](first-night.md) for the full walkthrough
from no-rig to first frames on the sensor.
