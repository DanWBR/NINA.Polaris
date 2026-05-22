# Bundled Siril scripts

These `.ssf` files ship with N.I.N.A. Polaris and cover the most
common preprocessing + stacking workflows for both OSC (one-shot
color, DSLR/astro-cameras) and monochrome (LRGB / narrowband)
imaging rigs.

| Script | What it does |
|---|---|
| `OSC_Preprocessing.ssf` | Full pipeline: bias + dark + flat + lights → registered, stacked, debayered FITS |
| `OSC_Preprocessing_WithoutDark.ssf` | Same minus darks (use when temp-matched darks aren't available) |
| `OSC_Preprocessing_WithoutFlat.ssf` | Same minus flats (rely on GraXpert / DBE downstream for vignette) |
| `OSC_Preprocessing_WithoutDBF.ssf` | Lights only — debayer + register + stack |
| `Mono_Preprocessing.ssf` | Mono LRGB / narrowband full pipeline (no debayer) |
| `Mono_Preprocessing_WithoutDark.ssf` | Mono, no darks |
| `Mono_Preprocessing_WithoutFlat.ssf` | Mono, no flats |
| `Mono_Preprocessing_WithoutDBF.ssf` | Mono lights only |
| `OSC_Extract_HaOIII.ssf` | Split a stacked OSC result into Ha + OIII channels (for L-eXtreme / L-Ultimate filters) |

## How Polaris uses them

`SirilService` extracts these `.ssf` files from the assembly's
embedded resources to `%LOCALAPPDATA%/NINA.Headless/siril/
scripts-bundled/` on first use. They show up in the STUDIO
"Stack with Siril" dropdown labelled `(bundled)`.

User-installed scripts in `%APPDATA%/siril/scripts` (Windows) or
`~/.siril/scripts` (Linux/macOS) also appear in the same dropdown
labelled `(your script)`. When names collide, the user copy wins
so power users can override Polaris's stock behaviour.

## Editing

Don't edit these files in place — Polaris re-extracts them on
upgrade. Instead copy one into your Siril user-scripts directory
and tweak it there. The user copy will take precedence
automatically.

## License

These scripts were authored for Polaris from scratch following the
Siril command reference at https://siril.readthedocs.io. Released
under the same MPL-2.0 license as the rest of Polaris. They run
against Siril (GPLv3) via the CLI; no Siril code is linked or
redistributed here.
