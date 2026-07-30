# Polaris Sky sub-application

This directory hosts a separate web document loaded by the Polaris main
app via `<iframe src="/sky/">` from the SKY tab. The sub-app embeds
[stellarium-web-engine](https://github.com/Stellarium/stellarium-web-engine)
(WebAssembly + WebGL2) to render the sky map.

## Why an iframe boundary

stellarium-web-engine is **AGPLv3**. The rest of Polaris is **MPL 2.0**.
Running the engine inside its own document keeps the license boundary
clean: the AGPL code, the wasm asset bundle, and the small `sky-bridge.js`
shim that adapts it to a postMessage API all live entirely under `wwwroot/sky/`.
The main Polaris app communicates with this sub-app exclusively through
`window.postMessage`, so the two sides exchange data (RA/Dec, click events,
FOV overlay descriptors) without sharing process state or linked code.

Per-license obligations:

- `wwwroot/sky/LICENSE-AGPL.txt` (lands in SWE-2 alongside the WASM
  build) is the verbatim AGPLv3 text.
- The iframe footer renders a "source" link pointing back to the
  Polaris repo so network users can obtain it.
- The Polaris repo root `README.md` calls out the embedded AGPL
  component.

## Layout

```
wwwroot/sky/
├── index.html           # iframe shell + canvas + status pane (SWE-1)
├── js/
│   ├── sky-bridge.js    # postMessage RPC + engine init shim (SWE-1)
│   └── wasm/
│       ├── stellarium-web-engine.js     # built from upstream via
│       └── stellarium-web-engine.wasm   #   scripts/build-stellarium-web.sh (SWE-2)
├── data/
│   └── skydata/         # HiPS tile pyramids (gitignored; bundled via
│                        # scripts/fetch-stellarium-skydata.sh + csproj
│                        # Content Include for publish output) (SWE-3)
├── LICENSE-AGPL.txt     # arrives in SWE-2
└── README.md            # this file
```

## postMessage protocol

See the header comment in `js/sky-bridge.js` for the canonical contract.

Summary:

- Parent → iframe: `set-observer`, `set-time`, `look-at`, `search`,
  `get-center`, `set-fov-overlays`, `set-drag-mode`.
- Iframe → parent: `ready`, `search-result`, `center`, `map-click`,
  `webgl-unavailable`.

Every iframe → parent message carries `__from: "sky-bridge"` so the
parent's listener can ignore stray messages from other origins.

## Rebuilding the engine

Roll the upstream submodule pin, then run the build script (Docker
+ Emscripten, no host toolchain needed):

```
git submodule update --remote external/stellarium-web-engine
scripts/build-stellarium-web.sh
git add wwwroot/sky/js/wasm/*
git commit -m "Sky: bump stellarium-web-engine build"
```

(Submodule + script land in SWE-2.)
