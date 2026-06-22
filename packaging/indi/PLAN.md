# PLAN — Distributing patched INDI drivers as `.deb` + auto-update from Polaris

## Why this exists

The Pi 4 / Pi 5 / Orange Pi 5 Pro images ship the **whole INDI stack built from
source** (`indiserver` + every `indi-3rdparty` driver), because indilib has **no
apt repository for Debian / Raspberry Pi OS arm64** — only an Ubuntu PPA. Field
diagnosis on a Pi 4 image confirmed:

- `indiserver` and all `/usr/bin/indi_*` are **orphan files** (`dpkg -S` → "no
  path found") — i.e. `make install`, not tracked by dpkg.
- Prefix is `/usr` (the `/bin/...` hit is just the usrmerge symlink, not a real
  duplicate). **No `/usr/local` copies.**
- The INDI runtime *libraries* came from Debian apt: `libindi* 1.9.9+dfsg`.
- The ASI SDK is present and correctly versioned:
  `/usr/lib/aarch64-linux-gnu/libASICamera2.so.1.41`.

So when DanWBR patches a driver (e.g. a BAYERPAT / readout fix in `indi-asi`),
there is currently no way to push it to users — they would have to recompile.
This plan makes a patched driver shippable as a `.deb` that:

1. is built **from the existing `make` build** (no rewrite of how drivers are
   compiled),
2. cleanly **takes over the orphan files** on first install and is dpkg-tracked
   from then on,
3. is hosted on **DanWBR's own repo (SourceForge)** and applied **automatically**
   by Polaris, reusing the proven self-update machinery (`UpdateService` +
   on-demand systemd unit + passwordless polkit rule).

Non-goals: rebuilding the images, standing up a signed apt repository, packaging
all ~280 drivers. The unit of distribution is **one upstream package group**
(`indi-asi`, `indi-svbony`, `indi-playerone`, `indi-gphoto`, …), prototyped with
`indi-asi`.

## Key facts that shape the design

| Fact | Consequence |
|------|-------------|
| Drivers are orphan (`make install`) | First `.deb` install **overwrites the orphan files without a dpkg conflict** (dpkg only errors when a file is owned by *another* package). After that it is tracked → future updates are clean upgrades. |
| Prefix `/usr` already | Build the `.deb` with `-DCMAKE_INSTALL_PREFIX=/usr` → zero duplicates, no `/usr/local` shadowing. A defensive `preinst` `rm` of `/usr/local/bin/<bins>` is kept as belt-and-suspenders. |
| libindi `1.9.9` from apt | The `.deb` declares `Depends: libindidriver1, libindiclient1, libindialignmentdriver1`. The soname (`…driver1`) **pins the ABI**: if libindi ever bumps to soname 2, apt blocks the mismatched driver instead of letting it crash. |
| SDK `libASICamera2` is orphan too | The driver `.deb` must carry the SDK `.so` (the upstream `indi-asi` bundles it via the `libasi` sub-build). Verify with `dpkg -S /usr/lib/aarch64-linux-gnu/libASICamera2.so.1.41`. |
| cmake writes `install_manifest.txt` | **This is the file list for the `.deb`** — no hand-maintained allowlist. The packager reads it. |

## The build → `.deb` workflow (the part to get right)

You keep building drivers exactly as you do now. The only change is: **install to
a staging dir instead of `/`**, then run the packager. Concrete, for `indi-asi`:

```bash
# 0. (once) where your indi-3rdparty checkout lives
SRC=~/src/indi-3rdparty

# 1. Build the ASI SDK shim (provides libASICamera2 + headers) and the driver,
#    exactly like the upstream developer build — nothing new here.
cmake -B build/libasi   -DCMAKE_INSTALL_PREFIX=/usr "$SRC/libasi"
cmake --build build/libasi -j"$(nproc)"

cmake -B build/indi-asi -DCMAKE_INSTALL_PREFIX=/usr "$SRC/indi-asi"
cmake --build build/indi-asi -j"$(nproc)"

# 2. Install into a STAGING root (DESTDIR) — NOT the live system.
#    cmake records every installed path in <build>/install_manifest.txt.
STAGE="$PWD/stage-indi-asi"; rm -rf "$STAGE"
DESTDIR="$STAGE" cmake --install build/libasi
DESTDIR="$STAGE" cmake --install build/indi-asi

# 3. Turn the staged files into a .deb (reads the install_manifest.txt files).
packaging/indi/build-driver-deb.sh \
    --conf     packaging/indi/packages/indi-asi.conf \
    --version  2.1.0+danwbr1 \
    --source   "$STAGE" \
    --manifest build/libasi/install_manifest.txt \
    --manifest build/indi-asi/install_manifest.txt
# → ./indi-asi_2.1.0+danwbr1_arm64.deb  (+ prints size + sha256 for the manifest)
```

Why `DESTDIR` staging:
- It captures **exactly the files this build produced**, versioned, without
  touching the running system.
- `install_manifest.txt` then lists the staged paths, which the packager strips
  back to `/usr/...` and copies into the `.deb` tree.

Fallback if you already `make install`'d to the live system and want to package
*that*: pass `--source /` and point `--manifest` at the build's
`install_manifest.txt` (its paths are then the live `/usr/...`). The script
handles both by stripping the `--source` prefix.

### What `build-driver-deb.sh` does

1. Reads each `--manifest`, strips the `--source` (DESTDIR) prefix, copies every
   listed file into `pkgroot/usr/...` preserving perms (binaries 0755, `.so`
   0755, data 0644).
2. Generates `DEBIAN/control` from `templates/control.in` (Package / Version /
   Architecture / Depends / Description from the `.conf`).
3. Generates a **`preinst`** that `rm -f`'s the stale `/usr/local/bin/<bins>`
   copies of exactly the binaries in this package (defensive anti-shadow).
4. Generates a **`postinst`** that runs `ldconfig` (the bundled SDK `.so`).
5. `dpkg-deb --root-owner-group --build` → `<pkg>_<version>_<arch>.deb`.
6. Prints `size` + `sha256` so you can paste them into `manifest.json`.

### Per-package config (`packages/indi-asi.conf`)

Sourced bash. One file per driver group you maintain:

```bash
DEB_PACKAGE="indi-asi"
DEB_DEPENDS="libindidriver1, libindiclient1, libindialignmentdriver1, libusb-1.0-0"
DEB_DESC_SHORT="INDI drivers for ZWO ASI cameras (DanWBR build)"
DEB_DESC_LONG="ZWO ASI camera/focuser/filter-wheel/ST4 INDI drivers, rebuilt by
 the N.I.N.A. Polaris project with field fixes, bundling libASICamera2."
```

## Distribution: SourceForge + a manifest

`.deb` files are loose downloads on SourceForge (its mirror redirects are fine —
`HttpClient` follows them; this is **not** an apt repo, so the redirect that
breaks apt does not apply here). A single `manifest.json` is the index Polaris
reads:

```jsonc
{
  "schema": 1,
  "generated": "2026-06-22T00:00:00Z",
  "base": { "libindi": "1.9.9" },          // advisory: which libindi these were built against
  "packages": [
    {
      "package": "indi-asi",               // dpkg package name
      "version": "2.1.0+danwbr1",          // dpkg version (must be > installed to offer)
      "arch": "arm64",
      "url": "https://sourceforge.net/projects/<proj>/files/indi/indi-asi_2.1.0+danwbr1_arm64.deb/download",
      "size": 5123456,
      "sha256": "….",
      "summary": "Fix BAYERPAT race on ASI OSC",
      "minPolaris": "3.3.0"                 // optional gate
    }
  ]
}
```

Version rule: always carry a `+danwbrN` revision so your build sorts **above** any
existing version and `dpkg --compare-versions` offers it.

## Polaris side (mirrors the existing self-update)

### `ThirdPartyDriverUpdateService`
Clone of `Services/External/UpdateService.cs`, source = the manifest URL
(configurable; default DanWBR SourceForge):
- **Check**: fetch `manifest.json` (cached ~30 min). For each entry, read the
  installed version with `dpkg-query -W -f='${Version}' <package>` (empty = not
  installed → only offer if you decide to, default skip) and compare with
  `dpkg --compare-versions`. Emit the list of upgradable packages with summaries.
- **Apply**: download each `.deb` (bounded timeout, **SHA-256 verified**, then
  `dpkg-deb -f … Package` sanity = expected name), stage under
  `/home/polaris/.cache/`, then `systemctl start polaris-indi-update.service`.
- **Offline relay**: same as Polaris — the browser fetches the `.deb` over its
  own link and POSTs the bytes; verified by SHA-256 from the manifest.

### `polaris-indi-update.service` + `polaris-indi-update.sh`
Twin of `polaris-self-update.service`. The script:
```bash
export DEBIAN_FRONTEND=noninteractive
apt-get install -y /home/polaris/.cache/indi-*.deb   # apt resolves libindi deps
```
Driver-only update **does not restart `polaris.service`**, so it does not need its
own cgroup the way the Polaris self-update does — but keeping it a unit gives us
the passwordless polkit grant and a clean log.

### `50-polaris-indi-update.rules`
Twin of `50-polaris-update.rules`: authorize the `polaris` user to
`systemctl start polaris-indi-update.service` only.

### Safety gates (load-bearing)
- **Refuse while a session is active** (capturing / guiding / slewing / autorun /
  live stack). Swapping a driver binary is safe on Linux (open inode survives),
  but to *use* the new driver `indiserver` must restart — never mid-night.
- After a successful install, prompt the user to **reconnect equipment** (Polaris
  bounces the INDI connection / indiserver), instead of doing it silently.
- **Scope** strictly to packages named in the manifest. Never a blanket
  `apt upgrade`.
- `dpkg --configure -a` recovery path surfaced on a failed apt.

### UI
A "Drivers (DanWBR)" row in the System Updates hub (next to the Polaris updater):
current vs available per package, a changelog from `summary`, a one-tap
"Update drivers", progress streamed via the existing terminal socket / transfer
chips. Auto-check is opt-in (boot + daily) and only ever *notifies* — never
applies unattended.

## Phases

- **P0 — Packaging skeleton (this commit).** `build-driver-deb.sh` + templates +
  `indi-asi.conf` + `manifest.example.json` + this plan. No server changes.
- **P1 — Prove one driver end-to-end.** Build `indi-asi` on a Pi, produce the
  `.deb`, `sudo apt install ./indi-asi_*.deb`, confirm: orphan files now
  `dpkg -S`-owned, `indiserver` runs the new binary, capture works, a second
  install upgrades cleanly.
- **P2 — Manifest + SourceForge.** Publish the `.deb` + `manifest.json`; verify
  the URL + SHA-256 by hand (`curl` + `sha256sum`).
- **P3 — `ThirdPartyDriverUpdateService` + endpoints + unit + polkit rule** (no
  UI yet); idle-gate; tests mirroring `UpdateService` tests.
- **P4 — System Updates hub UI** row + opt-in auto-check + offline-relay path.
- **P5 — Generalize** to `indi-svbony`, `indi-playerone`, `indi-gphoto`, … (just
  more `.conf` files) + docs in `docs/user-guide/`.

## Files

New (P0): `packaging/indi/PLAN.md`, `packaging/indi/build-driver-deb.sh`,
`packaging/indi/templates/{control.in,preinst.in,postinst.in}`,
`packaging/indi/packages/indi-asi.conf`, `packaging/indi/manifest.example.json`.

Later: `src/NINA.Polaris/Services/External/ThirdPartyDriverUpdateService.cs`,
`src/NINA.Polaris/Endpoints/DriverUpdateEndpoints.cs`,
`packaging/deb/lib/systemd/system/polaris-indi-update.service`,
`packaging/deb/opt/polaris/bin/polaris-indi-update.sh`,
`packaging/deb/etc/polkit-1/rules.d/50-polaris-indi-update.rules`,
`build-deb.sh` chmod additions, UI in `wwwroot/`.

## Verification (P1)
```bash
sudo apt install ./indi-asi_2.1.0+danwbr1_arm64.deb
dpkg -S "$(which indi_asi_ccd)"          # now: indi-asi: /usr/bin/indi_asi_ccd
which -a indi_asi_ccd                     # only /usr/bin (+ usrmerge), no /usr/local
dpkg -l indi-asi                          # version 2.1.0+danwbr1
# reconnect ASI in Polaris, take a frame → BAYERPAT present, no checkerboard
sudo apt install ./indi-asi_2.1.0+danwbr2_arm64.deb   # clean upgrade, no conflicts
```
