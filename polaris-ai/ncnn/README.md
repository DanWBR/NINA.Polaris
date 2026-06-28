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

Two things must both hold: the converted graph must match ORT **and** it must
still be correct on the **Vulkan** compute path (some ops are fine on ncnn-CPU but
break on Vulkan). Plus the Q6A Adreno-643 timing.

| model | CPU parity | **Vulkan** parity | Adreno fp16 speedup | verdict |
|-------|-----------|-------------------|---------------------|---------|
| **bge** | 9.1e-04 | ✅ ok (fp16 ~0.07 max) | **5.1×** | ✅ usable |
| **denoise v2** | 1.5e-03 | ✅ ok (fp16 4e-3) | ~similar (untimed) | ✅ usable |
| **denoise v3** | 7.5e-04 | 🔴 **NaN** (fp32 & fp16) | — | ❌ broken on GPU |
| **decon object** | 1.8e-02 | not pursued | — | ❌ CPU parity weak |
| **decon stars** | 4.1e-01 | not pursued | — | ❌ not faithful |

**Verdict:** the ncnn-Vulkan lane is real **for BGE + denoise v2** — both convert
losslessly *and* run correctly on Vulkan, ~5× faster in fp16 on the Adreno 643.
fp16 ≈ 2× over fp32 and is the production mode.

**Caveats found the hard way:**
- **denoise v3 outputs NaN on the Vulkan path** (its LayerNorm/ReduceMean/Div/Sqrt
  chain) even though ncnn-CPU is perfect. So the model the NPU can't run *also*
  can't run on Vulkan as-converted — `bench.py` will time it but the output is
  garbage. (`bench.py` now flags NaN output.) v3 would need the offending op
  replaced/patched on ncnn's Vulkan backend before it's usable.
- **decon** isn't numerically faithful even on CPU (Swin window partition +
  ConvTranspose); keep it on the ORT/NPU path.
- Q6A CPU baselines vary run-to-run (thermal throttling under sustained load), so
  trust the **ratios**, not the absolute CPU ms.

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
