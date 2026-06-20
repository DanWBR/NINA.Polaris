# Converting nox to ONNX for Polaris

[nox](https://github.com/charvey2718/nox) is an **MIT-licensed** star-removal
network — a StarNet-like encoder/decoder (LayerNorm, GAN + perceptual losses,
~54M params) trained on a synthetic dataset. Both the code and the trained
weights are MIT, so Polaris can bundle it **by default** with no commercial
restriction. It's the recommended default star-removal model.

It ships two trained Keras weight files:

- `generator_color.h5` — native 3-channel RGB model
- `generator_gray.h5` — single-channel model

## One-time install

1. Download `generator_color.h5` and `generator_gray.h5` from the
   [nox releases](https://github.com/charvey2718/nox/releases) into a folder.
2. Run the converter (rebuilds the generator, loads each .h5, exports ONNX in
   Docker):

```powershell
powershell -ExecutionPolicy Bypass -File scripts\convert-nox-onnx.ps1 `
    -WeightsDir C:\path\to\nox\v1.0
```

It writes `model.onnx` into both
`wwwroot/graxpert/models/nox-color-ai-models/1.0.0/` and
`.../nox-gray-ai-models/1.0.0/`. Restart Polaris (or `POST /api/onnx/rescan`).
Pick **nox** in the **Remove stars** dialog's Model dropdown. The `.onnx`
files are git-ignored (large); only the script + this doc + `LICENSE.txt` are
committed.

## Model facts (what the Polaris pipeline assumes)

Verified against the exported ONNX with onnxruntime (54.4M params):

| Property | Value |
|---|---|
| Architecture | StarNet-like enc/dec, LayerNorm, GAN + perceptual |
| Input | `gen_input_image`, `float32 [1,512,512,C]` (C=3 colour, 1 gray) |
| Output | `tf.math.subtract`, `[1,512,512,C]`, subtractive `input − relu(decode)` in the **[-1,1]** domain |
| RGB | the **colour** model is run natively (one inference / tile); mono uses the **gray** model |
| Tile | 512, processed with 128-px overlap (stride 256) |
| Normalization | feed `2·stretched − 1`, read `(output + 1)/2` |
| Opset | 13 (producer tf2onnx) |
| Size | ~218 MB ONNX per model |

Polaris still applies its MTF auto-stretch into the trained (non-linear) domain
before inference and inverse-stretches afterwards, plus the optional halo
reduction.

## Licensing

nox is **MIT** (code and weights), Copyright © 2023 Christopher Harvey; its
architecture derives from StarNet ideas by Nikita Misiura, used under the MIT
License. Notice ships beside each model as `LICENSE.txt`.
