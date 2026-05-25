# GraXpert ONNX models (bundled fallback)

This directory is the **deploy-friendly fallback** location for the
GraXpert AI models that drive the browser-side BGE / Denoise / Decon
pipelines. When `Onnx:ModelsPath` in the user profile is unset (or
points at a directory that doesn't exist), `OnnxModelRegistry`
automatically scans here at startup.

## Layout

Mirror GraXpert's own bundled layout so the registry can infer
`family/version` from the path:

```
graxpert/models/
├── bge-ai-models/
│   └── 1.0.1/model.onnx                       (~208 MB)
├── denoise-ai-models/
│   ├── 2.0.0/model.onnx                       (~284 MB)
│   └── 3.0.2/model.onnx                       (~456 MB)
├── deconvolution-stars-ai-models/
│   └── 1.0.0/model.onnx                       (~267 MB)
└── deconvolution-object-ai-models/
    └── 1.0.1/model.onnx                       (~267 MB)
```

Total: ~1.5 GB if you ship all five. You can ship a subset (e.g.
BGE only) — the UI gates each operation by what's available in
the manifest.

## How to populate

Two options:

1. **Copy from a local GraXpert install.** Grab `models/` from a
   GraXpert clone or installed bundle and drop it here:

   ```
   cp -r /path/to/GraXpert/models/* graxpert/models/
   ```

2. **Download from upstream.** The models are mirrored on the
   GraXpert release page (CC BY-NC-SA 4.0 — non-commercial use).
   Use the GraXpert app itself to download them, then copy from
   `~/.local/share/GraXpert/models/` (Linux) or the equivalent
   Windows path into this directory.

## Why this directory?

Polaris servers often run on Raspberry Pi or mini-PC in an
observatory. The operator drops the published app folder onto the
box once; making `Onnx:ModelsPath` mandatory means an extra config
round-trip via the Settings UI before any AI op works. With the
bundled fallback, the operator drops the models alongside the
binary and there's zero config.

The profile setting still wins when both exist — handy if you
keep your models on an external SSD mounted somewhere else.

## Git

The `.onnx` files themselves are **gitignored** (too big, and the
license forbids redistribution from us). Only this README + the
`.gitkeep` files are tracked, so the directory structure survives
a fresh clone.
