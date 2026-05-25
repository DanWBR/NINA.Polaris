# AI processing (ONNX in the browser)

Polaris embeds the **GraXpert AI models** (Background Extraction,
Denoise, Deconvolution) and runs them in your browser via
[onnxruntime-web](https://onnxruntime.ai/docs/tutorials/web/). The
server hosts the model files; any client browser fetches them on
demand, caches them in IndexedDB, and runs the inference locally.

This decouples Polaris from the GraXpert CLI subprocess — works on
any device (laptop, Android, iOS 16.4+) without installing GraXpert
on the host, and keeps the server's CPU free for capture/guiding.

## Setup

1. **Get the models**. Polaris does not ship the `.onnx` files;
   they're CC BY-NC-SA 4.0 (non-commercial, attribution, share-alike).
   Clone the [GraXpert repo](https://github.com/Steffenhir/GraXpert)
   to any path — the models ship inside under `models/`. Or copy
   the five `model.onnx` files into a folder of your own with the
   GraXpert layout:

   ```
   {your-root}/
     bge-ai-models/1.0.1/model.onnx
     denoise-ai-models/2.0.0/model.onnx
     denoise-ai-models/3.0.2/model.onnx
     deconvolution-stars-ai-models/1.0.0/model.onnx
     deconvolution-object-ai-models/1.0.1/model.onnx
   ```

2. **Point Polaris at it**. Settings → **AI inference (ONNX)** →
   *Models path* → paste the absolute path → tab out. The table
   below populates with the detected models. First-time scan computes
   SHA-256 hashes (~5 seconds total on SSD).

3. **(Optional) Pick the default denoise version**. v2 (284 MB) is
   the default; v3 (456 MB) is sharper but heavier — useful on
   desktops, tight on iOS. Override via
   `Onnx:DefaultDenoiseVersion` in Settings.

That's it. The browser is now ready to run AI.

## Running

### From the FILES tab

1. Select one or more FITS files.
2. Click **🌅 BGE** / **🔇 Denoise** / **✨ Decon** in the toolbar.
3. The modal opens with **Run in browser** ticked (default when an
   ONNX model is available + Prefer-CLI is off).
4. Click **Start**.

First run per model downloads the bytes from the server (~200-500
MB depending on op) and caches them in IndexedDB. Inference time
on a laptop with WebGPU is ~30 seconds for BGE on a 24-megapixel
master, ~3-5 minutes for Decon. Each input becomes a sibling
`{stem}_bge.fits` / `{stem}_denoise.fits` / `{stem}_decon.fits`
next to the source.

### From the EDITOR

Open any FITS, scroll the right panel down to the new **AI (GraXpert)**
section. Three buttons — Background / Denoise / Deconvolve. Each:

- Processes the editor's current source FITS,
- Saves a sibling FITS next to the source,
- Auto-loads the sibling as the new editor source,
- Preserves your Light/Color/Effects slider state across the swap.

The original source FITS is never modified. Ctrl+Z after an AI
swap reverts your edits (file remains on disk).

## First-use licence

The very first time you trigger any AI op per browser, a modal
prompts you to acknowledge the **CC BY-NC-SA 4.0** licence of the
GraXpert AI weights. Non-commercial use, attribution, share-alike
on derivatives. Polaris itself is MPL 2.0 and does not redistribute
the model files. Click **I agree** to continue; consent is cached
in localStorage + the server profile so subsequent sessions and
other browsers on the same profile don't re-prompt.

## Browser requirements

| Backend | Browser | Notes |
|---|---|---|
| **WebGPU** | Chrome/Edge 113+, Safari 16.4+ (macOS/iOS), Firefox 121+ behind flag | Fast — uses the device GPU. Default when supported. |
| **WASM SIMD** | All modern browsers | CPU-only fallback. ~5-10× slower than WebGPU but works everywhere. |

The runtime picks WebGPU > WASM SIMD > scalar WASM automatically.
Detect what you got via DevTools console: `OnnxRegistry.pickBackends()`.

### Memory caveats

Each model loaded into the runtime takes ~250-500 MB of resident
memory. On iOS Safari the per-origin heap caps around 2 GB —
loading Denoise v3 (456 MB raw + ~500 MB runtime) can fail. Default
to v2 (284 MB) on mobile, override per session in Settings.

The IndexedDB cache persists across sessions. Total disk cost in
the browser ~1.5 GB if you've used all five models. Drop via
**Clear cache** in the Settings AI panel.

## Falling back to CLI

If your browser can't run the models for any reason (very old, no
WebGPU + no SIMD, OOM on a huge master) you can still use the
GraXpert CLI subprocess on the host:

Settings → AI inference (ONNX) → **Advanced** → tick *Prefer CLI
subprocess by default*. The FILES-tab modal now opens with
**Run in browser** unchecked; it's still per-run editable.

You can also run both side by side — install the CLI on the host
AND keep the ONNX path enabled. The toggle picks per invocation.

## How it works

```
┌─ Browser (any OS) ──────────────────────┐
│  onnx-pipelines.js                      │
│    OnnxRegistry.loadSession(family, v)  │
│      → IndexedDB lookup by SHA-256      │
│      → GET /api/onnx/model/...          │
│      → ort.InferenceSession.create()    │
│    BgePipeline.run(pixels, w, h, opts)  │
│      → MAD normalize → session.run →    │
│         denormalize → smooth → correct  │
└──────────────┬──────────────────────────┘
               │ HTTP (model bytes, pixels)
┌──────────────▼──────────────────────────┐
│  Polaris server                         │
│  OnnxModelRegistry                      │
│    walks Onnx:ModelsPath recursively    │
│    maps family/version from layout      │
│    lazy SHA-256 hash cache              │
│  GET  /api/onnx/manifest                │
│  GET  /api/onnx/model/{family}/{version}│
│       ETag + Cache-Control: immutable   │
│  GET  /api/onnx/source-pixels?path=     │
│  POST /api/onnx/save (sibling FITS)     │
└─────────────────────────────────────────┘
```

The server is a thin CDN + a file decoder/encoder. All AI math lives
client-side. Bandwidth: ~1.5 GB once per browser session (cached
afterwards) + ~50-200 MB per file processed (raw uint16 round-trip).

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| **No models in Settings** | Path empty or wrong. Confirm with `dir <path>\bge-ai-models\1.0.1\model.onnx` (Windows) or `ls <path>/bge-ai-models/1.0.1/model.onnx` (Unix). |
| **Hash column stuck on "(pending)"** | Manifest never fully fetched — visit Settings → AI inference once with the tab open and the rescan triggers it. |
| **`OnnxRegistry is not defined` in console** | `/js/onnx-pipelines.js` failed to load. Check DevTools Network for 404 / MIME issues. |
| **WebGPU not available** | Browser too old or feature flag off. Inference falls back to WASM SIMD; everything still works, just slower. |
| **Run hangs at "downloading 0%"** | Server CORS / Relay caching the model with the wrong content-type. Try Clear cache + reload. |
| **First save fails with 500** | Source path not writable by the Polaris service account. Check `journalctl -u nina-polaris` (Linux) or the EventLog (Windows). |
| **iOS Safari OOM on Denoise v3** | Override `Onnx:DefaultDenoiseVersion` to `2.0.0` in Settings. |

## Out of scope today

- **Sample-point background extraction** (manual BGE point picking +
  RBF interpolation). v1 runs auto-BGE only; the user-driven UX
  variant ships in a follow-up phase.
- **Mosaic-aware tiling**. Decon currently tiles per file; a
  super-tile path that uses overlap blending across a mosaic
  pass is interesting but deferred.
- **Full-bit-depth editor integration**. The editor's working
  buffer is 8-bit display-stretched; the AI section operates on
  the raw 16-bit source by reloading the editor with the AI
  output. A v2 will tap the raw plane in-session.
- **Custom user-trained models**. Only the five GraXpert official
  models are recognised by the family/version layout parser.
