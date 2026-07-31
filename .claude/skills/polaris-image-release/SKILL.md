---
name: polaris-image-release
description: Build or refresh a flashable Polaris image (x86-64 mini PC, Raspberry Pi 4/5, Orange Pi 4 Pro/5 Pro, Radxa Dragon Q6A) with the latest released .deb, sanitize it, compress it with 7-Zip and stage it on Google Drive. Use this whenever the user asks to update, rebuild, regenerate or republish an image, a card, an SD image, a .img or a .7z for any board, says something like "gera uma imagem nova", "atualiza as imagens arm64", "sobe a imagem no drive", or asks to put a new Polaris version on a Pi / mini PC image. Also use it when diagnosing an image that does not boot or that shows a black screen after the EFI stub.
---

# Polaris image release

Turns a released `.deb` into a flashable image, without booting the artifact and
without shipping one machine's identity to everyone who flashes it.

The whole pipeline is: **get the deb → install it into the image → sanitize →
verify → compress → stage on Drive**. Each step has a check, because every
mistake this pipeline has made in the field was invisible at the time and
expensive later: an image that shipped a TLS keypair, a truncated GPT that
would not enumerate, a kernel console pinned to a serial port nobody had.

## Before starting

Ask for nothing you can check yourself. Confirm:

- **WSL2 with root** (`wsl -u root`) — loop devices and chroot need it. No sudo
  password is needed with `-u root`.
- **qemu-user-static + binfmt**, only for arm64 images. See "Registering binfmt".
- **NanaZip** (`NanaZipC.exe`) on the Windows side for compression.
- **`gh`** authenticated, for downloading the release artifact.
- The **Drive folder** that syncs to the user's Google Drive. Today that is
  `E:\GDrive\Polaris`; confirm rather than assume.

Disk: a raw image is 8-30 GB and you may need two copies at once (the image
plus a boot-test copy). Check free space on the WSL filesystem before starting.

## 1. Get the released .deb

Pushing to master tags a release and CI builds the packages, so the deb the
users install already exists — build one locally only if the release is
missing. Using the released artifact means the image carries the same bytes
users get from the update button.

```bash
gh run list --limit 3                      # is the release build still running?
gh release view vX.Y.Z --json assets -q '.assets[].name'
gh release download vX.Y.Z -p "polaris_amd64.deb" -D <scratch>   # or polaris_arm64.deb
```

If the run is still in progress, wait for it rather than building locally: a
local build has a different version stamp and defeats the point.

## 2. Get the raw image

- **x86-64**: `packaging/img/polaris-linux-x64.img` lives in the repo already.
- **arm64**: only the compressed archives are kept, on Drive. Extract one:

```bash
7z x -so "/mnt/e/GDrive/Polaris/<name>.7z" > /root/<name>.img
```

`7z -so` streams, so piping it through `head -c` extracts only a prefix — handy
for a quick look at a boot partition without unpacking 30 GB. It does not help
for the update itself, which needs the whole image: ext4 will not mount from a
truncated file, because the block bitmaps live at the end.

## 3. Install the deb into the image

`scripts/image-update.sh` does mount → chroot → `dpkg -i` → sanitize →
zero free space. Give it the image and the partition offsets in **sectors**
(see `references/image-map.md` for every board):

```bash
# x86-64: GPT, root is the second partition
bash scripts/image-update.sh /path/polaris-linux-x64.img 1953792 --deb <scratch>/polaris_amd64.deb

# Raspberry Pi: FAT32 boot mounted inside the root, so the postinst sees it
bash scripts/image-update.sh /root/rpi4.img 1064960 --deb <scratch>/polaris_arm64.deb \
     --boot-offset 16384 --boot-mount /boot/firmware
```

Why `dpkg -i` in a chroot instead of copying `/opt/polaris` over: the package
database stays honest. An image whose files say 0.97.2 while dpkg says 0.89.6
breaks the in-app updater's version comparison, and that is exactly what an
earlier file-copy update left behind.

The script installs a `policy-rc.d` returning 101 for the duration, so a
postinst cannot start services in a chroot that has no systemd.

## Registering binfmt (arm64 only)

An arm64 chroot on an x86 host runs through qemu-user. Register the interpreter
by feeding the packaged line to the kernel **verbatim**:

```bash
mount | grep -q binfmt_misc || mount -t binfmt_misc none /proc/sys/fs/binfmt_misc
grep '^:qemu-aarch64:' /usr/lib/binfmt.d/qemu-aarch64.conf \
  | while IFS= read -r l; do printf '%s' "$l" > /proc/sys/fs/binfmt_misc/register; done
```

Expand those `\xHH` escapes by hand and every `exec` on the distro starts
failing with ELOOP — the magic no longer matches ELF, so the kernel hands x86
binaries to qemu too. It cost a full WSL reinstall once. Let the kernel parse
them.

## 4. Verify before compressing

Compression takes minutes and the upload takes longer, so check first. Run
`scripts/image-verify.sh <img> <root-sectors>`; it reports:

- **version** — `dpkg-query -W polaris` must be the version you installed.
- **residue** — `machine-id` at 0 bytes, no `*/NINA.Polaris/cert` directory, no
  growroot marker, empty journal. A shipped machine-id makes every flashed
  device the same host to DHCP and mDNS; a shipped certificate means accepting
  it on one rig accepts it for all.
- **kernel console** — the last `console=` must be a screen (`tty0` / `tty1`).
  The x86 image once shipped with only `console=ttyS0,115200n8`, left over from
  the autoinstall running on QEMU's serial port: on a real mini PC the display
  went black the instant the kernel took over from the EFI stub, and the only
  visible sign was that Ubuntu's recovery entry (which builds its own cmdline)
  still printed. If this check fails, fix `/etc/default/grub` **and** the
  generated `grub.cfg` — `update-grub` cannot run against a loop device backed
  by a file on `/mnt/c`, so patch the generated file and validate it with
  `chroot <mnt> grub-script-check /boot/grub/grub.cfg`.

## 5. Boot-test a copy (x86-64)

Never boot the file you are going to ship. Booting writes a machine-id, a TLS
keypair and the growroot marker into it — which is how those got shipped in the
first place. Copy it, boot the copy:

```bash
bash scripts/image-boot-test.sh /root/boottest.img
```

The script boots QEMU with OVMF (real UEFI, so it exercises GPT → ESP → GRUB),
forwards SSH, and waits for the guest's **SSH banner**. That distinction
matters: QEMU's `hostfwd` opens the host-side port the moment QEMU starts, so
"port 2222 is open" is true while the guest is still in the firmware. Only a
`SSH-2.0-...` banner or a real HTTP response proves the guest answered.

Then confirm from inside:

```bash
sshpass -p polaris ssh -p 2222 -o StrictHostKeyChecking=no polaris@127.0.0.1 \
  'dpkg-query -W polaris; systemctl is-active polaris.service; cat /proc/cmdline'
```

A first-boot image should answer `{"error":"auth required","authConfigured":false}`
on `https://…:5000/api/system/status` — that is the sanitization proving itself:
no profile, no password, no certificate.

There is no equivalent for arm64: emulating those boards is unreliable enough
that a pass would not mean much. Say so plainly instead of implying coverage
you do not have, and let the user flash a card.

## 6. Compress and stage

Compress on the **Windows** side. WSL's 7z on a /mnt/c file is several times
slower for the same archive.

```powershell
NanaZipC.exe a -t7z -mx=5 -mmt=on <out>.7z <img>
NanaZipC.exe t <out>.7z          # never skip: a 0-byte archive once overwrote a good one
Copy-Item <out>.7z E:\GDrive\Polaris\<name>-vX.Y.Z.7z
```

After copying, compare `(Get-Item src).Length` with the destination. The Drive
folder syncs in the background and a partial copy looks like a file.

Name the archive for the version it contains: `polaris-linux-x64-v0.97.2.7z`,
`rpi4-polaris-v0.97.2.7z`. The name is what the user sees in the download.

### Delete the .img as soon as its .7z is verified

Do this per board, inside the loop, not at the end of the batch:

```powershell
NanaZipC.exe t <out>.7z          # must pass first
Remove-Item <img>                # only then
```

The seven uncompressed images are ~120 GB and the archives that replace them
are ~36 GB. Holding all seven until the batch finished cost 120 GB of the
operator's system disk, and the images are reproducible from the .7z by
extraction, so keeping both is paying for the same bytes twice.

Order matters and is not negotiable: the archive has to TEST clean before the
source goes. A 0-byte archive has overwritten a good one here before, and with
the .img already deleted that would have been the only copy.

Note the second half of the bill, which is invisible: the loop mount, the
chroot and the copy all happen inside WSL, and its ext4.vhdx GROWS but never
shrinks on its own. After a batch, `df -h /` inside WSL can show tens of GB
free while the vhdx still holds all of it on the Windows side. Compacting it
needs an elevated shell, so hand the user the command rather than trying:

```powershell
wsl --shutdown; Optimize-VHD -Path '<...>\LocalState\ext4.vhdx' -Mode Full
```

`wsl --manage <distro> --set-sparse true` is the non-elevated alternative and
is currently refused by WSL as unsafe. Do not force it with `--allow-unsafe`.

## 7. Hand off

Creating the share link needs the Drive UI, which is not reachable from here.
Tell the user the file is staged and ask them to share it and send the link.

Then verify the link before anything points at it:

```bash
curl -sL "https://drive.google.com/uc?export=download&id=<ID>" \
  | grep -oE '<a href="/open\?id=[^"]*">[^<]*</a> \([^)]*\)'
```

That prints the real filename and size Drive will serve. A published page that
promises the new version while the link still serves the old one is the same
class of failure as the black screen: silent, and only the user discovers it.
When it matters, hash both sides (`Get-FileHash -Algorithm SHA256`) and confirm
the staged file is byte-identical to what you tested.

## Publishing on the website (optional follow-up)

The download cards live in `website/content/install/install.json` plus the four
locale copies (`install.pt-BR.json`, `.es`, `.fr`, `.de`). Each board is one
entry in `images.items`; the x86-64 card is the first. Update `url`, `polaris`
(`"0.97.2 (2026-07-29)"`) and `size` in **all five** files, then commit and
push — the site deploys from master.

Only edit the card for the image you actually rebuilt. The others still point
at their own versions.
