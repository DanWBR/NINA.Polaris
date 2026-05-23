# Siril setup for Polaris

[Siril](https://siril.org) is Polaris's preferred preprocessing +
stacking engine. When installed, the **STUDIO** tab shows a
**⚡ Stack with Siril** button that runs your chosen `.ssf`
script against the frames you've selected.

## Install

### Linux

```bash
# Debian / Ubuntu
sudo apt install siril

# Fedora / RHEL
sudo dnf install siril

# Arch
sudo pacman -S siril
```

Or grab a Flatpak from [flathub](https://flathub.org/apps/org.siril.Siril)
if the distro package is too old (Polaris uses CLI features added
in 1.0).

### Windows

Download the installer from <https://siril.org/download/>. The
standard install drops `siril-cli.exe` at
`C:\Program Files\Siril\bin\siril-cli.exe` — Polaris auto-detects
that path.

### macOS

```bash
brew install --cask siril
```

Or download the `.dmg` from the official site and drag into
`/Applications`. Polaris looks for the CLI inside the `.app`
bundle automatically.

## Verify the detection

1. Open the Polaris UI, go to **Settings → External tools**.
2. The **Siril** row should show **✓ Detected v1.x.x** with the
   binary path.
3. If it shows **✗ Not detected**, click **Re-detect**. Still
   nothing? Paste the absolute path to `siril-cli` (`.exe` on
   Windows) into the **Path override** field — Polaris uses that
   over auto-detection.

## Scripts

Polaris ships 9 curated `.ssf` scripts covering the common
preprocessing matrix:

| Camera | Calibration | Script |
|---|---|---|
| OSC (one-shot color / DSLR) | bias + dark + flat | `OSC_Preprocessing.ssf` |
| OSC | bias + flat (no darks) | `OSC_Preprocessing_WithoutDark.ssf` |
| OSC | bias + dark (no flats) | `OSC_Preprocessing_WithoutFlat.ssf` |
| OSC | lights only | `OSC_Preprocessing_WithoutDBF.ssf` |
| Mono | bias + dark + flat | `Mono_Preprocessing.ssf` |
| Mono | bias + flat (no darks) | `Mono_Preprocessing_WithoutDark.ssf` |
| Mono | bias + dark (no flats) | `Mono_Preprocessing_WithoutFlat.ssf` |
| Mono | lights only | `Mono_Preprocessing_WithoutDBF.ssf` |
| OSC dual-narrowband | — | `OSC_Extract_HaOIII.ssf` |

These get extracted from the Polaris assembly to
`%LOCALAPPDATA%/NINA.Polaris/siril/scripts-bundled/` (Windows) or
`~/.local/share/NINA.Polaris/siril/scripts-bundled/` (Linux/macOS)
on first use. They're idempotent — Polaris re-extracts on upgrade.

Your personal scripts in the standard Siril location
(`%APPDATA%/siril/scripts` on Windows, `~/.siril/scripts` on
Linux/macOS) also appear in the STUDIO dropdown, marked
`(your script)`. If a name collides, **your copy wins** — useful
for tweaking the bundled recipes.

You can also point Polaris at an **extra** scripts dir under
Settings → External tools → Siril → "Extra scripts folder".

## Running a stack

1. In STUDIO, select the light frames you want to stack.
2. Click **⚡ Stack with Siril**.
3. Pick a script from the dropdown.
4. Enter a target name (used in the output path).
5. Hit **Start**. Progress shows in real-time; output lands at
   `{ImageOutputDir}/{rig}/siril/{target}/result_{timestamp}.fit`.

## Combo with GraXpert

If GraXpert is also installed, the modal offers a
**"Inject GraXpert BGE per-frame before stacking"** toggle. With
it on, Polaris runs GraXpert background extraction on each light
first (~10 s per frame), then feeds the cleaned `_bge` versions
into the Siril script. Slower but produces a much cleaner master
under heavy light pollution. See [graxpert-setup.md](graxpert-setup.md).

## Troubleshooting

- **"siril-cli exited with code 1"** — open the work directory
  shown in the error toast (Polaris keeps it on failure for
  debug). The Siril log in that folder usually identifies the
  issue (missing master, mismatched filter, etc).
- **No `result*.fit` appeared** — your script likely doesn't end
  with a `save result` line. Polaris bundled scripts all do; if
  you wrote your own, add `load result_*` + `save result` at the
  end.
- **Permission denied on the work dir** — the user that runs the
  Polaris service needs write access to
  `{ImageOutputDir}/.polaris-tmp/`. On Linux, ownership of the
  parent dir is the usual culprit.

## License

Siril is GPLv3. Polaris invokes it via the CLI only — no Siril
code is linked or redistributed. The bundled scripts are MPL-2.0
(same as Polaris) and were authored from scratch following the
[Siril command reference](https://siril.readthedocs.io).
