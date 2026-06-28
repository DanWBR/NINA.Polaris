# ncnn spike — running the GraXpert models on the SBC GPU (Vulkan)

Feasibility spike for the **NCNN-GPU** lane in [`PLAN.md`](../../PLAN.md): can the
GraXpert ONNX models run on the SBC GPU through [Tencent ncnn](https://github.com/Tencent/ncnn)
(Vulkan compute)? This is the open, vendor-neutral GPU path — Vulkan runs on the
**Adreno 643 of the Radxa Dragon Q6A** (via Turnip), Mali, Intel, etc., with no
per-vendor SDK. It complements the RKNN/QNN NPU lanes (which can't run the
LayerNorm-heavy denoise v3).

ncnn can't load ONNX directly, so the route is:

```
model.onnx ──onnxsim (static shape)──► sim.onnx ──pnnx──► sim.ncnn.param + sim.ncnn.bin
```

then verify the converted graph matches ONNX Runtime numerically before trusting it.

## Scripts

| script | what it does | runs where |
|--------|--------------|------------|
| `convert_graxpert.py` | converts **all** GraXpert models and prints a parity table | dev box (needs onnxsim + pnnx + ort + ncnn) |
| `parity_check.py`     | one model: ncnn vs ORT max\|Δ\| / correlation | dev box |
| `bench.py`            | one model: CPU vs **Vulkan GPU** ms/tile + speedup | **the SBC** (Q6A) |

```bash
pip install onnx onnxsim pnnx onnxruntime ncnn numpy   # dev box (conversion)
python convert_graxpert.py                              # -> out/*.ncnn.{param,bin}
```

Converted `out/*.ncnn.*` are large (100–240 MB each) and git-ignored — regenerate
them from the scripts.

## Spike result (2026-06-28)

Conversion + ONNX-Runtime parity (random input; CPU; the Vulkan run uses the same
graph/weights):

| model | result | max\|Δ\| vs ORT | bin | notes |
|-------|--------|-----------------|-----|-------|
| **bge** (background extraction) | ✅ PASS | 9.1e-04 | 109 MB | NHWC 256³ U-Net |
| **denoise v2** | ✅ PASS | 1.5e-03 | 149 MB | + SE/attention |
| **denoise v3** | ✅ PASS | 7.5e-04 | 239 MB | **LayerNorm/transformer — the model the NPU can't run** |
| **decon object** | 🟡 CLOSE | 1.8e-02 | 139 MB | windowed-attention; visually check |
| **decon stars** | 🔴 DIFF | 4.1e-01 | 139 MB | same arch, weights expose a fragile op — needs work |

**Verdict:** BGE + both denoise models convert with essentially lossless parity
(≤1.5e-3). That alone makes the ncnn-Vulkan lane worthwhile — notably **denoise v3
runs**, which the Hexagon NPU can't. The **decon** family converts structurally
but isn't numerically faithful yet (the Swin-style window partition via
Reshape/Slice/Gather + ConvTranspose is the fragile part); leave decon on the
existing ORT/NPU path until that's chased down.

The decon models take **two inputs**: `gen_input_image` (1×1×512×512) and `params`
(1×2, the strength/bg vector) — both must be fed (ncnn blobs `in0` and `in1`).

## Running the GPU benchmark on the Q6A

ncnn's pip wheel ships with Vulkan, so no C++ build is needed:

```bash
# on the Radxa Dragon Q6A
pip install ncnn numpy
# copy the converted out/*.ncnn.{param,bin} over, then:
python bench.py out/bge_sim.ncnn        256 256 3
python bench.py out/denoise_v3_sim.ncnn 256 256 3
```

`bench.py` reports `get_gpu_count()` (should be ≥1 = Turnip sees the Adreno), then
CPU vs Vulkan ms/tile and the speedup. If `get_gpu_count()` is 0, the Vulkan
loader/Turnip driver isn't visible to the process — that's the first thing to fix.

> The numbers from the dev box (a dGPU) are not representative — only the Q6A run
> tells us whether the Adreno is a net win there. (On the Q6A the OpenCL classic-math
> path was a net loss; Vulkan AI inference is a different workload, hence this spike.)

## If we build the lane

Mirror the RKNN lane (see PLAN.md `NCNN-GPU`): a `NcnnRuntime` probe + P/Invoke
binding to `libncnn`, an `NcnnInferenceService` reusing the tiling math, model
conversion wired into the build, and a chooser slot in `GraXpertService`
(ncnn-Vulkan for denoise v3 + fp16-quality runs, CPU/NPU fallback elsewhere).
fp16 is a one-flag change in ncnn (`opt.use_fp16_storage/arithmetic`) and is the
expected production mode on Adreno.
