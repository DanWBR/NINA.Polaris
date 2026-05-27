# Polaris Debian packaging

Builds a single `polaris_VERSION_arch.deb` that turns a fresh
Raspberry Pi OS Lite (or any Debian-derived Linux) install into a
working Polaris server with one `apt install ./polaris.deb`. Replaces
the manual 13-section setup in
[docs/user-guide/raspberry-pi-setup.md](../docs/user-guide/raspberry-pi-setup.md)
for users who do not want to type commands.

## What the .deb does

| Step | What | When |
|---|---|---|
| Stage payload | Copies self-contained .NET binary to `/opt/polaris/` | dpkg unpack |
| Drop unit | Installs `polaris.service` to `/lib/systemd/system/` | dpkg unpack |
| Drop config | Installs default `appsettings.json` to `/opt/polaris/` (conffile) | dpkg unpack |
| Pull apt deps | Resolves `indi-bin`, `libfontconfig1`, etc | apt install |
| Create user | `adduser --system polaris` if missing | postinst |
| Create dirs | `~/files`, ensures `~/.config/NINA.Polaris/` exists | postinst |
| Install indi-web | `python3 -m venv /opt/polaris-indiweb-venv` + `pip install indiweb` | postinst |
| Enable + start | `systemctl daemon-reload; enable; start polaris.service` | postinst |
| Print URL | `http://<hostname>.local:5000` | postinst |

End user sees:

```bash
sudo apt install ./polaris_0.42.0_arm64.deb
# ... apt resolves dependencies, runs postinst ...
# Polaris running at http://polaris-pi.local:5000
```

## What the .deb does NOT do

- **Does not install .NET runtime separately.** The binary inside is
  self-contained (`dotnet publish --self-contained`), so the .deb is
  ~150 MB but works on any aarch64 Bookworm without `dotnet`
  installed.
- **Does not install GraXpert.** GraXpert is GPLv3 + ships no ARM
  binary, only a PyPI package whose AI models are large and licensed
  separately. Installed manually after Polaris is up; see the
  `raspberry-pi-setup.md` section 4.4. Polaris auto-detects whatever
  install style the user picks.
- **Does not install an ASTAP star catalog.** ASTAP is pulled as a
  recommend, but the V50/V100 star database is a separate ~290 MB
  download with several variants. Document as a follow-up:
  ```bash
  cd /tmp
  wget -O v50.deb https://downloads.sourceforge.net/project/astap-program/star_databases/v50_star_database.deb
  sudo dpkg -i v50.deb
  ```
- **Does not configure HTTPS.** Self-signed cert generation is a
  user-driven step (`polaris --setup-https`) because each device that
  connects has to trust the cert once. Defer to user.
- **Does not touch user data on remove.** `/home/polaris/files`,
  `/home/polaris/.config/NINA.Polaris/profiles`, and saved sessions
  survive `apt remove polaris` AND `apt purge polaris`.

## Build

Requires `dotnet` SDK 10.x with linux-arm64 / linux-x64 runtime
targets installed, plus `dpkg-deb` (any Debian / Ubuntu host or WSL).

```bash
# From repo root:
./packaging/build-deb.sh 0.42.0 arm64    # for Pi 4 / 5
./packaging/build-deb.sh 0.42.0 amd64    # for x86 mini-PCs

# Output: polaris_0.42.0_arm64.deb (or _amd64.deb)
```

On a Pi (or any Debian), validate before publishing:

```bash
dpkg-deb -I polaris_0.42.0_arm64.deb         # control metadata
dpkg-deb -c polaris_0.42.0_arm64.deb | head  # payload listing
lintian polaris_0.42.0_arm64.deb             # style checks
```

## Install on the target

```bash
sudo apt install ./polaris_0.42.0_arm64.deb
```

`apt install ./file.deb` is the modern equivalent of `dpkg -i` plus
dependency resolution, so it pulls in `indi-bin`, `phd2`, `astap`,
etc. automatically.

If you only have `dpkg`:

```bash
sudo dpkg -i polaris_0.42.0_arm64.deb
sudo apt --fix-broken install   # resolves any missing deps
```

## Upgrade

Drop a newer .deb on top:

```bash
sudo apt install ./polaris_0.43.0_arm64.deb
```

The postinst is idempotent: existing user, dirs, and indi-web venv
are detected and reused. `appsettings.json` is marked as a conffile,
so dpkg prompts before overwriting your edits:

```
Configuration file '/opt/polaris/appsettings.json'
 ==> File on system created by you or by a script.
 ==> File also in package provided by package maintainer.
   What would you like to do about it ?
    Y or I  : install the package maintainer's version
    N or O  : keep your currently-installed version
    D       : show the differences between the versions
```

Profile data and captures are never touched.

## Remove vs purge

```bash
sudo apt remove polaris       # uninstall binary + unit, keep user data + configs
sudo apt purge polaris        # also removes configs and indi-web venv
                              # (still keeps /home/polaris/files and profiles)
```

To wipe everything including captures (irreversible):

```bash
sudo apt purge polaris
sudo userdel -r polaris       # removes /home/polaris entirely
```

## Layout reference

```
/opt/polaris/                       Self-contained binary + assets
├── NINA.Polaris                    Executable
├── appsettings.json                Conffile (IndiWeb path, ports)
├── wwwroot/                        UI assets
└── ... (.NET native libs, etc)

/lib/systemd/system/polaris.service Unit file

/opt/polaris-indiweb-venv/          Python venv for indi-web (postinst)
└── bin/indi-web

/home/polaris/                      System user home
├── files/                          POLARIS_IMAGE_OUTPUT_DIR
└── .config/NINA.Polaris/profiles/  Profile JSONs (per-rig, per-user)

/usr/share/doc/polaris/             Bundled docs + license
```

## Architecture notes

- **Self-contained .NET**: 70-100 MB of the package is the .NET
  runtime. Trade-off accepted because installing .NET 10 on Bookworm
  apt is awkward (not in repos, only via Microsoft script) and we want
  `apt install` to "just work".
- **System user**: `polaris` is `--system`, no login shell unless
  manually changed. Runs only the service, no sudo, no normal user
  permissions.
- **indi-web in dedicated venv**: lives in `/opt/polaris-indiweb-venv/`
  rather than the user's home, so a user-side `pip install` cannot
  break it and it survives user account changes. Path is what
  `appsettings.json` points to by default.
- **HOME env in unit**: `Environment=HOME=/home/polaris` because Bottle
  (indi-web's framework) and several other Python libs use `appdirs`
  which needs HOME set even when running as a systemd service.

## Future work (out of scope for v1)

- **apt repository hosting**: would let users do
  `apt update && apt install polaris` for upgrades, no manual .deb
  download. Requires GPG-signed repo on GitHub Pages or similar.
- **SD card image**: see `image-build/README.md` for the pi-gen plan
  that would bake the .deb pre-installed into a bootable image.
- **Snap / Flatpak**: same idea, different ecosystems. Lower priority.
- **GraXpert wrapper .deb**: separate `polaris-graxpert` package that
  installs GraXpert via pip in a polaris-managed venv. Skipped here
  because GraXpert AI models are not redistributable.
