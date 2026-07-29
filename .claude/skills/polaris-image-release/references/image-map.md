# Image map

Partition offsets in **sectors** (512 bytes each), plus where each board keeps
its kernel command line. Verified 2026-07-29 by reading the shipped images.

Get the numbers yourself for an image not listed here:

```bash
fdisk -l <image>.img         # MBR / DOS labels
sgdisk -p <image>.img        # GPT
```

## Boards

| Board | Archive on Drive | Table | Root (sectors) | Boot partition | Mount boot at |
|---|---|---|---|---|---|
| x86-64 mini PC | `polaris-linux-x64-*.7z` | GPT | `1953792` | `2048` (953M ESP) | not needed |
| Raspberry Pi 4 | `rpi4-polaris-*.7z` | DOS | `1064960` | `16384` (512M FAT32) | `/boot/firmware` |
| Raspberry Pi 5 | `rpi5-polaris-*.7z` | DOS | `1064960` | `16384` (512M FAT32) | `/boot/firmware` |
| Orange Pi 4 Pro | `opi4pro-polaris-*.7z` | DOS | `65536` | none (boot inside root) | — |
| Orange Pi 5 Pro | `opi5pro-polaris-*.7z` | GPT | `32768` | none (boot inside root) | — |
| Radxa Dragon Q6A | `radxa-dragon-q6a-polaris-*.7z` | GPT | `2162688` | `65536` (1G EFI) | `/boot/efi` |

The Q6A also has a 16 MB partition at `32768` that carries firmware; leave it
alone.

## Where the kernel command line lives

The check that matters is the same everywhere: **the last `console=` has to be
a screen** (`tty0` or `tty1`), because the last one becomes `/dev/console`,
which is where the getty and the autologin attach. A command line with only a
serial console turns a real machine into a black screen the moment the kernel
takes over, with no error anywhere.

| Board | File | Value as shipped |
|---|---|---|
| x86-64 | `/etc/default/grub` → `/boot/grub/grub.cfg` | `console=ttyS0,115200n8 console=tty0` |
| RPi 4 / 5 | `/boot/firmware/cmdline.txt` | `console=serial0,115200 console=tty1` |
| Orange Pi 4 Pro | `/boot/boot.cmd` + `/boot/orangepiEnv.txt` | `console=both` → `console=ttyS0,115200 console=tty1` |
| Orange Pi 5 Pro | `/boot/boot.cmd` + `/boot/orangepiEnv.txt` | `console=both` → `console=ttyS2,1500000 console=tty1` |
| Radxa Q6A | `/boot/efi/loader/entries/RadxaOS-*.conf` | `console=ttyMSM0,115200n8 … console=tty1` |

The arm64 boards inherit this from their vendor images, which assume someone is
looking at a monitor. Only the x86 image was built by an installer running on a
serial console, which is why it was the one that shipped broken.

Orange Pi images read `console=both` from `orangepiEnv.txt` and `boot.cmd`
turns it into the pair above. Changing `boot.cmd` requires regenerating
`boot.scr` with `mkimage`; changing `orangepiEnv.txt` does not, so prefer the
env file when something needs adjusting.

## Reading a boot partition without unpacking the archive

The boot partition is the first one on every board that has one, so a prefix of
the raw image is enough:

```bash
7z x -so "/mnt/e/GDrive/Polaris/rpi4-polaris-v0.97.2.7z" | head -c 400M > /root/head.bin
L=$(losetup --find --show --offset $((16384*512)) --sizelimit $((1048576*512)) /root/head.bin)
mount -o ro $L /mnt/bootp && cat /mnt/bootp/cmdline.txt
```

This does not work for the Orange Pi images, where `/boot` lives inside the
root filesystem: ext4 refuses to mount from a truncated file because the block
bitmaps sit at the end of the filesystem, and `debugfs` fails for the same
reason. Those need a full extraction.
