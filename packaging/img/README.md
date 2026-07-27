# Polaris x64 bare-metal image builder

Builds a flashable **raw `.img`** with Ubuntu (22.04 or 24.04) + the full
Polaris stack baked in, ready to `dd`/Rufus onto a mini PC's SSD or USB and
boot bare-metal on UEFI.

Unlike cloud images or `virt-builder` output, this uses the **stock Ubuntu
autoinstall (subiquity)** installer, so the ESP + GRUB are laid down exactly
like a normal USB install. That is what makes it boot reliably on real UEFI
hardware.

## What's in the image

Encoded in [`scripts/install-polaris-linux.sh`](../../scripts/install-polaris-linux.sh),
which is also the installer users run by hand on a fresh Ubuntu (so the
image and the documented install are the same recipe):

- INDI (`indi-full`, incl. 3rd party drivers) + PHD2, from their PPAs
  (`ppa:mutlaqja/ppa`, `ppa:pch/phd2`)
- `astrometry.net` + the `tycho2` index (small, ships its data in-package)
- ASTAP **GUI + CLI** + the **d80** star database
- default user `polaris` / `polaris`, passwordless sudo, console autologin
- SSH enabled, hostname `polaris-linux`
- all suspend/hibernate disabled
- the prebuilt `polaris_amd64.deb` from
  [GitHub Releases](https://github.com/DanWBR/NINA.Polaris/releases)
  (no local build needed)

### Host-side payload (reliability)

The big/critical artifacts (Polaris + ASTAP debs + the 1.25 GB d80 database)
are **pre-downloaded on the host** and handed to the guest as an iso labelled
`POLARIS`. the installer installs them from that local mount and only hits the
network as a fallback. This sidesteps QEMU's emulated NAT, which times out
badly against github/fastly and SourceForge under TCG emulation. The files are
cached in `./payload/` between runs. Disable with `SKIP_PAYLOAD=1` to let the
guest download everything itself.

## Build it (works in WSL2)

WSL2 exposes `/dev/kvm`, so QEMU is hardware-accelerated and you can build
**and** boot-test the image without leaving Windows.

```bash
sudo apt install qemu-system-x86 ovmf cloud-image-utils \
                 libarchive-tools genisoimage openssl wget

cd packaging/img
chmod +x build-img.sh ../../scripts/install-polaris-linux.sh
./build-img.sh
```

The installer runs unattended on the serial console, powers off when done, and
leaves `polaris-linux-x64.img` in this directory.

### Common overrides

```bash
# Ubuntu 22.04 instead of 24.04 (the newest point release is auto-discovered):
UBUNTU_RELEASE=22.04 ./build-img.sh

# Pin an exact Polaris release + bigger disk (room for extra index databases):
POLARIS_VERSION=0.89.6 DISK_SIZE=48G ./build-img.sh

# Already have an ISO downloaded:
ISO=~/Downloads/ubuntu-24.04.4-live-server-amd64.iso ./build-img.sh
```

### Distributing the image (don't use PiShrink)

The raw image is `DISK_SIZE` (default 20G) of mostly zeros, so just **compress
it** - the free space disappears:

```bash
7z a polaris.7z polaris-linux-x64.img      # or: zstd -T0 ...  /  xz -T0 ...
```

The root filesystem auto-grows to fill the real disk on first boot (a
self-disabling `polaris-growroot.service` runs `growpart` + `resize2fs`), so a
20G image flashed onto a big SSD still uses the whole drive.

> **Why not PiShrink?** It truncates this GPT/UEFI image too tightly to leave
> room for the 33-sector GPT backup table, producing a non-bootable image that
> drops to an initramfs prompt (`UUID=... does not exist`). Compressing a
> modest-sized raw image is simpler and actually boots.

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

> Root auto-grows to fill the target disk on first boot
> (`polaris-growroot.service`), so flashing a 20G image onto a 256G SSD uses
> the whole drive. No manual `growpart` needed.

## Notes / gotchas

- **`~/.local/share` must exist (Polaris startup):** .NET's
  `Environment.GetFolderPath(LocalApplicationData)` returns an *empty string*
  when `~/.local/share` doesn't exist, which makes Polaris resolve its TLS
  cert / profile / log paths relative to `/opt/polaris` and crash-loop. The
  `--system` polaris user gets no skel, so the installer creates the dir (and
  the deb postinst now does too). If you ever see Polaris failing with
  `DirectoryNotFoundException: .../NINA.Polaris/cert`, this is why.
- **Resilience:** the installer does not use `set -e`. A failed *optional*
  component (ASTAP, astrometry, a PPA) is collected, the apt state is repaired
  so it can't cascade, and a summary is printed at the end. Only a missing
  Polaris `.deb` fails the build. Re-run `sudo bash install-polaris-linux.sh` on the booted
  system to retry anything listed.
- **Why not `astrometry-data-2mass`?** That package downloads its data at dpkg
  *configure* time; an interrupted fetch leaves dpkg unrecoverable and breaks
  every later `apt` call. We use `tycho2` (data shipped in the package). Pull
  narrow-field 4200/4100 indexes for your FOV into `/usr/share/astrometry`
  post-boot if you need them.
- **ASTAP URLs** point at the current SourceForge assets
  (`astap_amd64.deb`, `astap_command-line_version_Linux_amd64.zip`,
  `d80_star_database.deb`). If SourceForge renames them, override via
  `ASTAP_GUI_URL` / `ASTAP_CLI_URL` / `ASTAP_D80_URL`.
- The installer is **standalone**, and that is now the supported route for a
  mini PC: install stock Ubuntu with its own installer (which partitions the
  SSD properly and fills it), then run one command to get the same setup
  without writing a 13 GB image to an internal disk:

      curl -fsSL https://raw.githubusercontent.com/DanWBR/NINA.Polaris/master/scripts/install-polaris-linux.sh | sudo bash

  `--addon` installs the software without creating the polaris user, autologin,
  hostname or sleep policy, for someone putting Polaris on a machine they
  already use. Running it by hand gives the same setup without building an
  image at all (it just downloads everything since there's no payload mount).
- No `/dev/kvm`? The build still works under TCG emulation, just slowly, and
  the host-side payload makes that path far more reliable.
