# Converting starrem2k13 (U2NETP) to ONNX for Polaris

[starrem2k13](https://github.com/code2k13/starrem2k13) is an **MIT-licensed**
star-removal model (U2NETP architecture). Because both the code and the
trained weights are MIT, Polaris can bundle the converted model **by default**,
unlike StarNet, whose weights are NonCommercial.

It is offered as the **default, bundled** star-removal model. StarNet++ remains
an opt-in alternative (higher quality, but NonCommercial weights; see
[`convert-starnet-onnx.md`](convert-starnet-onnx.md)). When both are installed
the **Remove stars** dialog shows a **Model** dropdown.

## Which model

The repo's **main branch** later switched to a tiny 646k-param U2NETP whose
prebuilt `weights/model.onnx` (~2.6 MB) removes stars **poorly** (rings around
every star + washed-out nebula). **Do not use it.**

Polaris uses the **larger pix2pix-style U-Net from the pinned commit
`0398ce05`** (~31M params, ~124 MB ONNX), the version that matches the
published trained weights (a 124 MB TensorFlow checkpoint). The script below
builds *that* model.

## One-time install

1. Download the trained weights (a TF checkpoint: `checkpoint`,
   `weights.index`, `weights.data-00000-of-00001`) from the project's GitHub
   Releases into a folder.
2. Run the converter (fetches `model.py` from the pinned commit, loads the
   checkpoint, exports ONNX in Docker):

```powershell
powershell -ExecutionPolicy Bypass -File scripts\convert-starrem2k13-onnx.ps1 `
    -WeightsDir C:\path\to\weights
```

It writes `model.onnx` to
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
| Architecture | pix2pix-style U-Net (~31M params), commit `0398ce05` |
| Input | `args_0`, `float32 [1, 512, 512]`: **single channel, 3D (no channel axis)** |
| Output | `conv2d_transpose_7`, `float32 [1, 512, 512, 1]`: **relu**, the starless image directly (not a mask) |
| RGB | run **per channel** (3 inferences per tile) |
| Tile | 512, processed with 32-px overlap (stride 448) |
| Normalization | net trained on 8-bit `/512`; Polaris feeds `stretched·(255/512)` and reads `output·(512/255)` |
| Opset | 13 (producer tf2onnx 1.16.1) |
| Size | ~124 MB ONNX |

Verified against the actual exported `model.onnx` with onnxruntime (param count
31,129,809; output relu, max ~0.38 on random input).

Polaris still applies its MTF auto-stretch into the trained (non-linear) domain
before inference and inverse-stretches afterwards (same as StarNet), plus the
optional mask-guided **halo reduction** post-process.

## Licensing

starrem2k13 is **MIT** (code and weights), Copyright (c) code2k13
(Ashish Patel). The full notice ships beside the model as
`starrem2k13-ai-models/1.0.0/LICENSE.txt` and is listed in the in-app
third-party licenses. MIT permits commercial use, with no NonCommercial gate.
