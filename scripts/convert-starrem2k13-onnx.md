# Converting starrem2k13 (U2NETP) to ONNX for Polaris

[starrem2k13](https://github.com/code2k13/starrem2k13) is an **MIT-licensed**
star-removal model (U2NETP architecture). Because both the code and the
trained weights are MIT, Polaris can bundle the converted model **by default**
— unlike StarNet, whose weights are NonCommercial.

It is offered as the **default, bundled** star-removal model. StarNet++ remains
an opt-in alternative (higher quality, but NonCommercial weights — see
[`convert-starnet-onnx.md`](convert-starnet-onnx.md)). When both are installed
the **Remove stars** dialog shows a **Model** dropdown.

## One-time install

The upstream repo **ships a prebuilt `weights/model.onnx`** (U2NETP is tiny,
~2.6 MB), so the easiest path is just to copy it — the script does this
automatically when it finds it:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\convert-starrem2k13-onnx.ps1
```

If you instead want to re-export from the trained weights (a Release asset),
the repo ships a first-class ONNX exporter (`export_to_onnx.py`) and the script
will run it in Docker:

```powershell
# find the current weights asset on the releases page first:
#    https://github.com/code2k13/starrem2k13/releases
powershell -ExecutionPolicy Bypass -File scripts\convert-starrem2k13-onnx.ps1 `
    -WeightsUrl https://github.com/code2k13/starrem2k13/releases/download/<tag>/<asset>
```

Either way it writes `model.onnx` to
`src/NINA.Polaris/wwwroot/graxpert/models/starrem2k13-ai-models/1.0.0/`.
Restart Polaris (or `POST /api/onnx/rescan`) and the **🌠 Remove stars** button
appears once the `starrem2k13` family shows up in `GET /api/onnx/manifest`.

The `.onnx` is git-ignored (the model binaries are large); only the conversion
script + this doc + the `LICENSE.txt` are committed.

## Model facts (what the Polaris pipeline assumes)

These are baked into `StarRemovalPipeline` (`onnx-pipelines.js`, the
`starrem2k13` profile):

| Property | Value |
|---|---|
| Architecture | U2NETP |
| Input | `args_0`, `float32 [1, 512, 512]` — **single channel, 3D (no channel axis)** |
| Output | `final_output`, `float32 [1, 512, 512, 1]` — **sigmoid** (0..1), the starless image directly (not a mask) |
| RGB | run **per channel** (3 inferences per tile) |
| Tile | 512, processed with 32-px overlap (stride 448) |
| Normalization | net trained on 8-bit `/382`; Polaris feeds `stretched·(255/382)` and reads `output·(382/255)` |
| Opset | 13 (producer tf2onnx 1.16.1) |

Verified against the actual `model.onnx` with onnxruntime.

Polaris still applies its MTF auto-stretch into the trained (non-linear) domain
before inference and inverse-stretches afterwards (same as StarNet), plus the
optional mask-guided **halo reduction** post-process.

## Licensing

starrem2k13 is **MIT** (code and weights), Copyright (c) code2k13
(Ashish Patel). The full notice ships beside the model as
`starrem2k13-ai-models/1.0.0/LICENSE.txt` and is listed in the in-app
third-party licenses. MIT permits commercial use — no NonCommercial gate.
