# QNN GPU-backend spike — preparing an fp32 model and running it on the Adreno

Goal: prove the **Adreno 643 GPU** on the Radxa Dragon Q6A (QCS6490) can run the
GraXpert models the **Hexagon HTP can't** — denoise **v3** (LayerNorm, needs
V73+), deconvolution, star removal — and fringing-sensitive runs, **at fp32**
(no quantization), via QAIRT's `libQnnGpu.so` backend.

Why this is cheap: we already bundle the QAIRT 2.45 runtime (`/opt/polaris/qairt`)
and drive `qnn-net-run` in `Services/Qnn`. Using the GPU is just **(a)** swapping
`--backend libQnnHtp*.so` → `libQnnGpu.so` and **(b)** feeding a **non-quantized**
model. No `--quantize_io`, no `htp`/unsigned-PD config (those are HTP-only).

Background (the precision matrix that motivated this): Qualcomm AI Hub lists the
QCS6490 as CPU=int8/fp16/fp32, **GPU=fp16/fp32**, HTP=int8/int16. So fp16/fp32 on
this SoC means **CPU or GPU**, never the HTP.

---

## 1. Build an fp32 DLC on x86 (no quantization)

Use the same QAIRT x86 SDK + venv as the HTP path, but **stop at the DLC** and do
**not** quantize. (DLC is version-portable across QAIRT builds; context binaries
are not — so ship/copy the **DLC** and let the device load it.)

```bash
# venv with the QAIRT x86 tools on PATH (same as the HTP convert flow):
#   onnx pinned to 1.16.1, python 3.10 (see polaris_rknn_npu_feasibility memory)
# Source ONNX must be fp32 (the GPU runs float; do NOT pre-convert to fp16 here).

qairt-converter \
  --input_network denoise_v3.onnx \
  --output_path  denoise_v3_fp32.dlc
  # NOTE: no --quantize_io, no quantization job. Leave it float.
  # If the model has a dynamic batch, pin the tile shape:
  #   --source_model_input_shape "input:1,256,256,3"   (use the model's real input name/shape)
```

That's it — `denoise_v3_fp32.dlc` is the artifact. (Decon and star-removal models
have different shapes/inputs; convert each the same way, pinning their own input
shape. Decon is multi-input — sigma/strength — so list all inputs.)

> AI Hub alternative: submit a **compile** job to a QCS6490 target with **no
> quantize job** and runtime = QNN/DLC (or LiteRT GPU). You'll get a float model
> targeted at this SoC. The on-device `libQnnGpu.so` path below is preferred
> because it reuses the runtime we already package.

## 2. Copy the DLC to the Q6A

```bash
scp denoise_v3_fp32.dlc polaris@<q6a>:~/
```

## 3. Run the spike on the Q6A

```bash
# from the repo (or just copy scripts/qnn-gpu-spike.sh over):
bash scripts/qnn-gpu-spike.sh ~/denoise_v3_fp32.dlc --tile 256 --ch 3 --cpu
```

What it does:
1. Confirms `libQnnGpu.so` is in the QAIRT bundle and `ldd`-resolves (flags any
   missing **OpenCL / Adreno UMD** deps — the usual GPU-backend blocker).
2. Runs **one** inference (warm + op-support check): if the GPU backend can't
   lower an op (e.g. **LayerNorm**), the run fails and the script says so.
3. Times `(t_K − t_1)/(K−1)` ms/tile on the **GPU**, and — with `--cpu` — the
   **same DLC on `libQnnCpu.so`** for an apples-to-apples same-runtime baseline.

## 4. Reading the result

- **GPU ran it + ms/tile ≪ CPU** → GO. The GPU is the home for v3 / decon /
  star-removal (and fp32-quality denoise without int16 fringing). Next: add a
  `libQnnGpu` backend option to `Services/Qnn` (swap the `.so`, feed the float
  model), route those families to it, keep the HTP for BGE + denoise v2.
- **GPU failed on an op (LayerNorm / ReduceMean)** → `libQnnGpu` can't host it;
  fall back to the **LiteRT GPU delegate** (new runtime, more work) or keep that
  model on the **client browser** (WebGPU/WebGL, already fp32 + portable) / CPU.
- **`libQnnGpu.so` missing or won't `dlopen`** → re-extract `qairt-libs` to
  include the GPU backend, and make sure the Adreno OpenCL userspace is present.

Reference baselines (from the NPU work, same board): onnxruntime **fp32 CPU =
4488 ms/tile**; Hexagon **int16 HTP = 29.5 ms/tile** (but int16, and can't run
v3). The GPU number we want is fp32 and should beat the CPU by a wide margin to
justify the lane.
