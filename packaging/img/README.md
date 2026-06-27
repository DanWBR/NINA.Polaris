# Polaris x64 bare-metal image builder

Builds a flashable **raw `.img`** with Ubuntu (22.04 or 24.04) + the full
Polaris stack baked in, ready to `dd`/Rufus onto a mini PC's SSD or USB and
boot bare-metal on UEFI.

Unlike cloud images or `virt-builder` output, this uses the **stock Ubuntu
autoinstall (subiquity)** installer, so the ESP + GRUB are laid down exactly
like a normal USB install. That is what makes it boot reliably on real UEFI
hardware.

## What's in the image

Encoded in [`provision.sh`](provision.sh):

- INDI (`indi-full`, incl. 3rd party drivers) + PHD2, from their PPAs
  (`ppa:mutlaqja/ppa`, `ppa:pch/phd2`)
- `astrometry.net` + index databases
- ASTAP **GUI + CLI** + the **d80** star database
- default user `polaris` / `polaris`, passwordless sudo, console autologin
- SSH enabled, hostname `polaris-linux`
- all suspend/hibernate disabled
- the prebuilt `polaris_amd64.deb` pulled straight from
  [GitHub Releases](https://github.com/DanWBR/NINA.Polaris/releases)
  (no local build needed)

## Build it (works in WSL2)

WSL2 exposes `/dev/kvm`, so QEMU is hardware-accelerated and you can build
**and** boot-test the image without leaving Windows.

```bash
sudo apt install qemu-system-x86 ovmf cloud-image-utils \
                 libarchive-tools whois openssl wget

cd packaging/img
chmod +x build-img.sh provision.sh
./build-img.sh
```

The installer runs unattended on the serial console, downloads everything,
powers off, and leaves `polaris-linux-x64.img` in this directory.

### Common overrides

```bash
# Ubuntu 22.04 instead of 24.04 (the newest point release is auto-discovered):
UBUNTU_RELEASE=22.04 ./build-img.sh

# Pin an exact Polaris release + bigger disk (room for astrometry indexes):
POLARIS_VERSION=1.0.0 DISK_SIZE=32G ./build-img.sh

# Already have an ISO downloaded:
ISO=~/Downloads/ubuntu-24.04.4-live-server-amd64.iso ./build-img.sh
```

By default the script scrapes `releases.ubuntu.com/<release>/` for the latest
live-server point release, so it won't 404 when Ubuntu rotates them. Pin an
exact ISO with `ISO_URL=...` if you want a fixed build.

All knobs: `ISO`, `ISO_URL`, `UBUNTU_RELEASE`, `OUTPUT`, `DISK_SIZE`,
`POLARIS_VERSION`, `POLARIS_USER`, `POLARIS_PASS`, `HOSTNAME_NAME`, `QEMU_MEM`,
`QEMU_CPUS`.

## Flash it

- **Windows:** [Rufus](https://rufus.ie/) or
  [balenaEtcher](https://etcher.balena.io/) -> select `polaris-linux-x64.img`.
- **Linux:** `sudo dd if=polaris-linux-x64.img of=/dev/sdX bs=4M status=progress conv=fsync`

First boot: `https://polaris-linux.local:5000`, or
`ssh polaris@polaris-linux.local` (password `polaris`).

> The raw image is fixed-size; after first boot grow the root partition to fill
> the disk with `sudo growpart /dev/sdaX N && sudo resize2fs /dev/sdaXN`, or
> just build with a larger `DISK_SIZE`.

## Notes / gotchas

- **ASTAP URLs** in `provision.sh` are best-effort. Confirm the current asset
  names on hnsky.org / SourceForge if a download 404s, and override via
  `ASTAP_GUI_URL` / `ASTAP_CLI_URL` / `ASTAP_D80_URL`.
- **astrometry indexes** are large. The script installs the wide-field Ubuntu
  packages; pull the narrow-field 4200/4100 series matching your FOV into
  `/usr/share/astrometry` if you need them (bump `DISK_SIZE` accordingly).
- `provision.sh` is **standalone**: you can also run it on a plain Ubuntu
  netinst (`sudo bash provision.sh`) to get the same setup without building an
  image at all.
- No `/dev/kvm`? The build still works under TCG emulation, just slowly.
