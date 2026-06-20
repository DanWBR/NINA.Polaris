# StarNet → ONNX conversion (SN-1)

Polaris runs star removal as an **ONNX model** through the existing ONNX stack
(`onnx-pipelines.js` in the browser; NPU later). The model comes from the user's
fork **https://github.com/DanWBR/starnet** (nekitmm StarNet v1), which ships a
**TensorFlow 1** checkpoint, not ONNX. This is the one-time, offline recipe to
produce `model.onnx`. It needs a TF1 environment (the checkpoint uses
`tf.contrib`/`tf.layers`), so it is **not** part of the Polaris build — run it on
a model-prep box, then drop the result into the registry.

## Model facts (verified from the fork, 2026-06)

Source: `model.py` (`generator`), `export.py`, `transform.py`.

| Property            | Value |
|---------------------|-------|
| Framework           | TensorFlow 1 (`tf.contrib`, `tf.layers`); checkpoint `model.ckpt.*` |
| Net used            | **generator only** (discriminator is training-only) |
| Input tensor        | `X:0`, shape `[None, 256, 256, 3]`, **RGB** |
| Output tensor       | `generator/g_deconv7/Sub:0` (residual `input − ReLU(decoder)`) |
| Tile (window)       | **256** (fixed; baked into the checkpoint — do not change) |
| Input range         | **[0,1]** — NOTE: the TF1 `transform.py` feeds `[0,1]` directly, it does **NOT** do `×2−1`. (The `starnet_v1_TF2.py` reimpl uses `×2−1`; that does **not** apply to this checkpoint.) |
| Output range        | clamp to `[0,1]` |
| Tiling at inference | stride `S` (≤256), `offset=(256−S)/2`; run each 256² tile, keep the centre `S×S` at `[offset:offset+S]`. Edge padding = reflect/wrap of image borders (see `transform.py`). |
| License             | code MIT; **weights CC BY-NC-SA 4.0 (NonCommercial)** — attribute + NC notice. |

## Easiest: one command via Docker (no local Python 3.7 needed)

```powershell
powershell -ExecutionPolicy Bypass -File scripts\convert-starnet-onnx.ps1 -Docker
```
Runs the whole conversion inside a throwaway `python:3.7-slim` container
(StarNet checkout mounted at `/work`, the models tree at `/out`) and drops
`model.onnx` straight into `starnet-ai-models/<version>/`. Requires Docker
Desktop. The manual steps below are the no-Docker equivalent.

## Steps

Run from inside the `DanWBR/starnet` checkout (has `model.ckpt.*`, `model.py`,
`export.py`, `gen_sub.txt`).

1. **TF1 env** (Python 3.7/3.8):
   ```
   pip install "tensorflow==1.15.*" tf2onnx onnx onnxruntime
   ```
   (or `tensorflow-cpu==2.x` with `tf.compat.v1` + `disable_v2_behavior()` if 1.15
   wheels are unavailable for your Python.)

2. **Freeze the generator subgraph (weights baked in).** Do **not** use the
   fork's `export.py` + `gen_sub.txt`: it relies on `extract_sub_graph`, which
   (a) does **not** bake the weights into the `.pb` (it leaves `Variable` nodes,
   useless for standalone ONNX) and (b) lists stale node names — TF 1.15 emits
   `FusedBatchNormV3`, not the `FusedBatchNorm` in `gen_sub.txt`, so it asserts
   `... is not in graph`. Instead, restore the checkpoint and freeze from the
   output node with `convert_variables_to_constants`:
   ```python
   import tensorflow as tf, model
   X = tf.placeholder(tf.float32, [None,256,256,3], name="X")
   Y = tf.placeholder(tf.float32, [None,256,256,3], name="Y")
   model.model(X, Y)
   saver = tf.train.Saver()
   with tf.Session() as sess:
       sess.run(tf.global_variables_initializer())
       saver.restore(sess, "./model.ckpt")
       gd = tf.graph_util.convert_variables_to_constants(
           sess, sess.graph.as_graph_def(), ["generator/g_deconv7/Sub"])
       tf.io.write_graph(gd, ".", "starnet_generator.pb", as_text=False)
   ```
   (The `.ps1` writes exactly this as `freeze_starnet.py` and runs it.) BN uses
   `training=True` (per-tile batch stats) — that is how StarNet runs inference,
   and tf2onnx decomposes the training-mode `FusedBatchNormV3` accordingly.

3. **GraphDef → ONNX** (fixed 256² input):
   ```
   python -m tf2onnx.convert \
     --graphdef starnet_generator.pb \
     --inputs  X:0 \
     --outputs generator/g_deconv7/Sub:0 \
     --opset   13 \
     --output  model.onnx
   ```
   Optional: rename IO to `input`/`output` with `--rename-inputs`/`--rename-outputs`
   for a cleaner JS contract.

4. **Sanity check** vs the reference `rgb_test5.tif` / `rgb_test5.tif_starless.tif`
   in the fork: feed normalised [0,1] 256² tiles through `onnxruntime`, clamp [0,1],
   compare to the reference starless. Expect near-identical (≤ a few LSB).

5. **(Optional) FP16** for the Hexagon NPU path later
   (`onnxconverter_common.float16.convert_float_to_float16`); keep an fp32 copy
   for the browser WASM/WebGPU default.

## Install into Polaris

Drop the result at:
```
src/NINA.Polaris/wwwroot/graxpert/models/starnet-ai-models/1.0.0/model.onnx
```
The registry already maps `starnet-ai-models → "starnet"` (OnnxModelRegistry
FamilyAliases). Bundling the weights requires the **CC BY-NC-SA 4.0** attribution
+ NonCommercial notice in `3rd-party-licenses.txt` and the in-app About list; or
leave it out and point the Settings `OnnxModelsPath` at a user-provided copy.

## SN-2 (client pipeline) must match

In `onnx-pipelines.js` the `StarRemovalPipeline` replicates the inference-side of
`transform.py`: normalise FITS pixels to **[0,1]** (no ×2−1), tile **256** with
overlap (`stride` configurable, default 128 → offset 64), run the session, clamp
**[0,1]**, stitch the centre `stride×stride` of each tile, then save
`_starless` and the auto-derived `_stars = clamp(original − starless, 0)`.
RGB only (3-channel); for a mono FITS, feed the single plane to all 3 channels
and average the output back to mono.
