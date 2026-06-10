# Vendored fonts

App-wide font picker (Settings → Appearance → Font). Every family
below is **SIL Open Font License 1.1** (OFL-1.1), redistributable
inside Polaris (MPL 2.0 product) with attribution preserved.

| Family | Folder | Upstream | License |
|---|---|---|---|
| Atkinson Hyperlegible | `atkinson/` | https://github.com/googlefonts/atkinson-hyperlegible | [OFL-1.1](https://github.com/googlefonts/atkinson-hyperlegible/blob/main/OFL.txt) |
| Inter | `inter/` | https://github.com/rsms/inter | [OFL-1.1](https://github.com/rsms/inter/blob/master/LICENSE.txt) |
| IBM Plex Sans | `plex-sans/` | https://github.com/IBM/plex | [OFL-1.1](https://github.com/IBM/plex/blob/master/packages/plex-sans/LICENSE.txt) |
| JetBrains Mono | `jetbrains-mono/` | https://github.com/JetBrains/JetBrainsMono | [OFL-1.1](https://github.com/JetBrains/JetBrainsMono/blob/master/OFL.txt) |

Only Regular + Bold weights are vendored to keep the bundle under
~600 KB. If you need italic / extra weights, drop the additional
`.woff2` files here and extend `@font-face` declarations in
`wwwroot/css/app.css` accordingly.

## Refresh

Re-download via PowerShell from repo root:

```powershell
$base = "src\NINA.Polaris\wwwroot\fonts"
$atk  = "https://github.com/googlefonts/atkinson-hyperlegible/raw/main/fonts/webfonts"
$intr = "https://github.com/rsms/inter/raw/master/docs/font-files"
$plex = "https://github.com/IBM/plex/raw/master/packages/plex-sans/fonts/complete/woff2"
$jbm  = "https://github.com/JetBrains/JetBrainsMono/raw/master/fonts/webfonts"

iwr "$atk/AtkinsonHyperlegible-Regular.woff2" -OutFile "$base\atkinson\AtkinsonHyperlegible-Regular.woff2"
iwr "$atk/AtkinsonHyperlegible-Bold.woff2"    -OutFile "$base\atkinson\AtkinsonHyperlegible-Bold.woff2"
iwr "$intr/Inter-Regular.woff2"               -OutFile "$base\inter\Inter-Regular.woff2"
iwr "$intr/Inter-Bold.woff2"                  -OutFile "$base\inter\Inter-Bold.woff2"
iwr "$plex/IBMPlexSans-Regular.woff2"         -OutFile "$base\plex-sans\IBMPlexSans-Regular.woff2"
iwr "$plex/IBMPlexSans-Bold.woff2"            -OutFile "$base\plex-sans\IBMPlexSans-Bold.woff2"
iwr "$jbm/JetBrainsMono-Regular.woff2"        -OutFile "$base\jetbrains-mono\JetBrainsMono-Regular.woff2"
iwr "$jbm/JetBrainsMono-Bold.woff2"           -OutFile "$base\jetbrains-mono\JetBrainsMono-Bold.woff2"
```
