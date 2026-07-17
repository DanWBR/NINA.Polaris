# Installation

Polaris ships as a **self-contained** build, so there is no separate .NET
runtime to install. It runs on the rig (a Raspberry Pi, another SBC, a mini
PC, or any Linux/Windows host) and is reached from any browser on the same
network, with no app to install on the viewing device.

From easiest to most manual:

| Path | Best for |
|---|---|
| **Ready-to-flash image** | Get going in minutes on a supported board |
| **.deb (Debian / Ubuntu)** | The recommended install on arm64 SBCs and x86-64 PCs |
| **Portable tar.gz** | Fedora, Arch, or any non-Debian Linux |
| **Windows .zip** | A Windows mini PC, desktop, or laptop |
| **Docker** | Containerized deployment (multi-arch) |
| **Build from source** | Developers and unsupported platforms |

---

## Ready-to-flash images (fastest)

Pre-built OS images with Polaris preinstalled and configured for a specific
board (Raspberry Pi, Orange Pi, x86-64 PC, and more). Flash to an SD card,
USB drive, or eMMC with Raspberry Pi Imager, balenaEtcher, or Rufus, then
boot.

On first boot Polaris starts automatically and is ready to use, no setup
needed. If the device cannot join a known WiFi network and it has WiFi, it
raises its own hotspot named **Polaris-Hotspot** (password `polaris1234`);
connect to that, then open the UI.

Open it at `https://<hostname>.local:5000` (each image ships with its own
hostname, for example `polaris-pi.local`). Download links are on the
[website Download page](https://polaris-astro.app.br/install) and the
[GitHub releases](https://github.com/DanWBR/NINA.Polaris/releases).

---

## Debian / Ubuntu (.deb), recommended

A one-command install for Debian or Ubuntu, on arm64 SBCs (Raspberry Pi,
Orange Pi, Radxa) and on x86-64 PCs. The package creates the `polaris` user,
a systemd unit, the indi-web (indiwebmanager) venv, the apt dependencies
(INDI server and drivers, ASTAP, and so on), and a self-signed HTTPS cert.

**arm64 SBC:**
```bash
wget https://github.com/DanWBR/NINA.Polaris/releases/latest/download/polaris_arm64.deb
sudo apt install ./polaris_arm64.deb
```

**x86-64 PC:**
```bash
wget https://github.com/DanWBR/NINA.Polaris/releases/latest/download/polaris_amd64.deb
sudo apt install ./polaris_amd64.deb
```

It is running at `https://<hostname>.local:5000` in about 30 seconds. Manage
it with systemd:
```bash
sudo systemctl status polaris
sudo journalctl -u polaris -f
sudo systemctl restart polaris
```

**Self-update:** on a `.deb` install, a status-bar badge appears when a newer
GitHub release exists; one click downloads the matching `.deb`, installs it,
and reloads on the new version, with no SSH or sudo password needed. See
[self-update.md](self-update.md).

---

## Other Linux (portable tar.gz)

For Fedora, Arch, or any non-Debian distro, or when you prefer no systemd
integration. The build is self-contained, so there is no runtime to install.
Replace `linux-arm64` with `linux-x64` for Intel/AMD.

```bash
wget https://github.com/DanWBR/NINA.Polaris/releases/latest/download/polaris-linux-arm64.tar.gz
tar -xzf polaris-linux-arm64.tar.gz
cd polaris-linux-arm64
./NINA.Polaris
```

It runs in the foreground. Wire up your own systemd unit if you want it to
survive reboots, and an `indiserver` (`sudo apt install indi-bin indi-full`,
or your distro's equivalent) for equipment control; the embedded INDI Drivers
Manager needs [indiwebmanager](https://github.com/knro/indiwebmanager).

---

## Windows

Portable, no installer. The `win-x64` build covers Intel/AMD; a `win-arm64`
build is also published for Surface Pro X and some Copilot+ PCs.

```powershell
Invoke-WebRequest -Uri "https://github.com/DanWBR/NINA.Polaris/releases/latest/download/polaris-win-x64.zip" -OutFile polaris.zip
Expand-Archive polaris.zip
cd polaris-win-x64
.\NINA.Polaris.exe
```

Open `https://localhost:5000` and accept the self-signed cert once. Remember
to open the firewall (see below). On Windows, drive gear over **ASCOM / Alpaca**
(or INDI via WSL2), and you also get the native vendor **DSLR drivers**
(Canon, Nikon, Sony).

---

## Docker (multi-arch)

```bash
docker run -d --network host \
  -v $(pwd)/config:/config \
  -v $(pwd)/images:/images \
  ghcr.io/danwbr/nina-polaris:latest
```

A multi-arch image (arm64 + amd64). The compose file in the repo includes
`indiserver` in the same stack. The volumes mount `/config` (profiles) and
`/images` (FITS output). On Linux use `--network host` (INDI BLOB streaming
over loopback is much faster than a bridge), or port-forward TCP 5000 and
UDP 5353 in compose.

---

## Build from source

For developers or unsupported platforms. Needs the .NET 10 SDK and Git (with
submodules).

```bash
git clone https://github.com/DanWBR/NINA.Polaris.git
cd NINA.Polaris
./deploy/publish-linux-arm64.sh   # or publish-linux-x64.sh / publish-win-x64.ps1
./publish/linux-arm64/NINA.Polaris
```

Build and test target `net10.0`. The stellarium-web submodule ships pinned
`.js` / `.wasm`, so Emscripten is only needed if you bump the engine.

---

## Open the web UI

Polaris listens on **TCP 5000** (HTTPS, self-signed cert) and announces itself
over **mDNS** (UDP 5353), so `https://<hostname>.local:5000` resolves on the
LAN. You can also use the host's IP. Accept the self-signed certificate once;
HTTPS is what unlocks WebGPU and multi-thread WASM for the in-browser AI tools.

There is also an Android app (on the Download page) that scans the network and
lists every Polaris instance it finds, so you can connect with one tap.

### Firewall (manual installs)

The `.deb` opens these for you; on a portable or Windows install, allow them:

```powershell
# Windows (Admin PowerShell)
New-NetFirewallRule -DisplayName "N.I.N.A. Polaris" -Direction Inbound -Protocol TCP -LocalPort 5000 -Action Allow -Profile Private,Domain
New-NetFirewallRule -DisplayName "mDNS (Polaris)"   -Direction Inbound -Protocol UDP -LocalPort 5353 -Action Allow -Profile Private,Domain
```
```bash
# Linux
sudo ufw allow 5000/tcp
sudo ufw allow 5353/udp
```

---

## Updating an existing install

- **One-click in-app update** (recommended, `.deb` installs): click the
  status-bar update badge. See [self-update.md](self-update.md).
- **Manual over SSH:** SSH in (user `polaris`, password `polaris` on the
  ready-to-flash images), download the latest `.deb`, and install it over the
  running version. Your profiles and data are kept.
  ```bash
  ssh polaris@<hostname>.local
  wget https://github.com/DanWBR/NINA.Polaris/releases/latest/download/polaris_arm64.deb
  sudo dpkg -i polaris_arm64.deb
  ```
  Use `polaris_amd64.deb` on x86-64. Change the default `polaris` password
  after first login.

---

## DSLR / Mirrorless camera support

DSLRs have separate per-vendor docs:

- [Canon EDSDK](../dslr-windows-canon.md)
- [Nikon SDK](../dslr-windows-nikon.md)
- [Sony Camera Remote SDK](../dslr-windows-sony.md)
- [Linux gphoto](../dslr-linux.md) (any USB-tethered DSLR)

---

## Configuring image output location

Polaris saves captured FITS/XISF to a per-rig folder under `ImageOutputDir`.
The default is `$HOME/Pictures/Polaris` on Linux and
`%USERPROFILE%/Pictures/Polaris` on Windows. Change it from the FILES tab:
navigate to the folder you want and click "Set as Studio root".

The folder structure is:
```
{ImageOutputDir}/
  {RigName}/
    lights/{Target}/{Filter}/{ISO-timestamp}/
    calibration/dark/dark_{ExposureSec}s_{Gain}_{Temp}C.fits
    calibration/flat/{Filter}/flat_{Timestamp}.fits
    calibration/bias/bias_{Timestamp}.fits
    stacked/                         (saved live stacks)
    snaps/{Filter}_{Date}/           (from PREVIEW with Save on)
    planetary/{Target}/{ts}.ser      (from the VIDEO tab)
    siril/                           (Siril output)
    bge/                             (GraXpert BGE output)
```

---

## Next: first-night setup

Once Polaris is up and the UI responds at `https://<hostname>.local:5000`, see
[Getting started](https://polaris-astro.app.br/getting-started) for the full
walkthrough from no rig to first frames on the sensor.
