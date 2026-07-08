# NPU acceleration

Polaris can run the GraXpert AI models (background extraction, denoise) on a
board's **NPU** instead of the CPU, on two families of SBC:

- **Rockchip RK3588 / RK3588S** (Orange Pi 5 Pro, etc.) — via the RKNPU2 runtime.
- **Qualcomm** (Radxa Dragon Q6A / QCS6490, Hexagon V68) — via the QAIRT runtime.

Both are fully automatic and isolated behind a runtime probe: when a supported
NPU + the matching runtime + a converted model are present, Polaris uses the NPU;
otherwise it falls back to the GraXpert CLI / in-browser ONNX path with no change
in behaviour. The Rockchip path is documented first, then the Qualcomm path.

## Rockchip RK3588

On Rockchip RK3588 / RK3588S boards (Orange Pi 5 Pro, and similar) Polaris can
run the GraXpert AI models on the board's **NPU** instead of the CPU. On an
Orange Pi 5 Pro this is about **5x faster** for background extraction and
denoise (measured 91 ms/tile on the NPU vs 457 ms/tile on the 8 CPU cores), and
it frees the CPU cores for live stacking.

It is fully automatic: when an NPU is present and a converted model is bundled,
Polaris uses it; otherwise it falls back to the GraXpert CLI (or the in-browser
ONNX path) with no change in behaviour.

## What is accelerated

| Operation | NPU | Notes |
|---|---|---|
| Background extraction (BGE) | yes | single forward pass |
| Denoise | yes | tiled 256/128 |
| Deconvolution | no | uses the GraXpert CLI (different model layout) |

Only FITS inputs take the NPU path; other formats use the CLI. The NPU path
works **even if the GraXpert CLI is not installed**.

## Requirements on the board

1. An RK3588/RK3588S board with the **RKNPU driver** in the kernel. The stock
   Orange Pi / vendor Ubuntu images already include it — check with:

   ```bash
   ls /dev/dri/renderD*        # an NPU render node should exist
   sudo cat /sys/kernel/debug/rknpu/version
   ```

2. The **RKNPU2 runtime** `librknnrt.so`. The Polaris `.deb` for `linux-arm64`
   bundles it next to the app, so normally there is nothing to do. If you build
   yourself, run `scripts/fetch-librknnrt.sh` before publishing (see below).

You can confirm Polaris detected the NPU from **GraXpert status**
(`/api/graxpert/status` → `npuAvailable: true`, `npuDiagnostics`).

To force the NPU off (e.g. to compare against the CPU), set
`POLARIS_DISABLE_NPU=1` in the service environment.

## Building the models and runtime (maintainers)

The `.rknn` models and `librknnrt.so` are produced/fetched at build time and are
not committed (the models derive from GraXpert's NonCommercial AI weights; the
runtime is a Rockchip vendor binary).

1. **Convert the ONNX models to RKNN** — on an x86_64 Linux / WSL box with
   `rknn-toolkit2` (Python 3.11):

   ```bash
   python3.11 -m venv ~/rknn && source ~/rknn/bin/activate
   pip install rknn-toolkit2 onnx
   python3 scripts/convert_rknn_models.py
   ```

   This writes `model.rknn` next to each `model.onnx` under
   `src/NINA.Polaris/wwwroot/graxpert/models/` (BGE + Denoise by default).

2. **Fetch the runtime** for the linux-arm64 publish:

   ```bash
   ./scripts/fetch-librknnrt.sh        # → external/rknpu/aarch64/librknnrt.so
   ```

3. Publish / build the `.deb` for `linux-arm64` as usual; the csproj bundles the
   `.so` and the models ship under `wwwroot/graxpert/models`.

## How it works

- `RknnRuntime` probes for an NPU (arm64 + a `/dev/dri/renderD*` node +
  loadable `librknnrt.so`).
- `RknnSession` (P/Invoke over `librknnrt`) loads a `.rknn`, pins all three NPU
  cores, and runs one `[1,256,256,3]` fp32 tile at a time.
- `RknnInferenceService` does the tiling / normalization, mirroring the
  in-browser ONNX pipeline so NPU and browser output match.
- `GraXpertService` tries the NPU first for BGE/Denoise and falls back to the
  CLI on any failure.

The models run in **fp16** (no quantization), so there is no quality difference
versus the CPU/ONNX path.

## Qualcomm (Radxa Dragon Q6A / QCS6490, Hexagon V68)

On Qualcomm SBCs Polaris can run BGE / Denoise on the **Hexagon NPU (HTP)** via
the **QAIRT** runtime (Qualcomm AI Runtime, formerly "QNN"). On the Radxa Dragon
Q6A the denoise model runs at about **29.5 ms/tile** (int16) — roughly **150x**
the CPU onnxruntime baseline (~4488 ms/tile) — and frees the CPU for live
stacking.

### Integer-only: int16 vs int8

The QCS6490 Hexagon HTP is **integer-only — INT8 and INT16, no FP16** (fp16 on
this chip runs on the GPU/CPU, not the NPU). Polaris ships **int16** models by
default: that is the production, near-fp16-quality path. An **int8** model is
~4x faster (~7.3 ms/tile) but visibly lower quality on denoise, so it is the
"turbo" option, not the default. The model resolver prefers, in order:
`fp16` → `int16` → `int8` (the fp16 tier is kept for future SoCs whose HTP
supports it).

### Requirements on the board

1. A Qualcomm SBC whose Hexagon cDSP is up — Polaris checks for
   `/dev/fastrpc-cdsp`:

   ```bash
   ls -l /dev/fastrpc-cdsp        # the Hexagon FastRPC bridge
   ```

2. The **QAIRT runtime** bundled at `/opt/polaris/qairt` (`bin/qnn-net-run`,
   `lib/libQnnHtp.so` + companions, `dsp/libQnnHtpV68Skel.so`). The arm64
   Polaris `.deb` bundles it when the maintainer staged it at build time (see
   below). Override the location with `POLARIS_QAIRT_ROOT`.

Confirm detection from **GraXpert status** (`/api/graxpert/status` →
`npuAvailable: true`, `npuDiagnostics`). Force the NPU off with
`POLARIS_DISABLE_NPU=1` (all NPU paths) or `POLARIS_DISABLE_QNN=1` (just QAIRT).

### Building the models and runtime (maintainers)

The HTP context binaries (`*_v68_int16.bin`) and the QAIRT runtime are
produced/assembled at build time and are **not committed** (the models derive
from GraXpert's NonCommercial AI weights; the runtime is proprietary, device-
version-locked Qualcomm code — see `licenses/QAIRT-LICENSE.txt`).

1. **Build an int16 context binary** via Qualcomm AI Hub (`qai_hub`), targeting
   the QCS6490 (device "Dragonwing RB3 Gen 2 Vision Kit"):
   compile→`onnx`, `submit_quantize_job` with `weights=INT8, activations=INT16`
   (w8a16), then compile→`qnn_context_binary --quantize_io`. Place the result at
   `wwwroot/graxpert/models/qnn/{family}-ai-models/{version}/{family}_v68_int16.bin`.

   For the **Polaris-trained** models (our own weights — the `.bin` is safe to
   commit, unlike the GraXpert NonCommercial ones) this whole flow is scripted:

   ```bash
   source ~/qnn/bin/activate                             # env that has qai_hub
   qai-hub configure --api_token <YOUR_TOKEN>            # one-time, if not already
   python3 scripts/qnn-convert.py --dry-run              # preview source/output paths
   python3 scripts/qnn-convert.py                        # convert Polaris BGE + Denoise
   python3 scripts/qnn-convert.py --all                  # + halo, upscale, decon
   ```

   `--all` also converts halo / upscale / decon (the converter handles decon's
   two inputs — image NCHW 512 + a `params` tensor — automatically). All three
   now run on the Hexagon NPU (QNN path) once their `.bin` is present:
   - **Decon** — automatic: a GraXpert "AI Sharpen" / decon run on a FITS takes
     the NPU like BGE/Denoise (`QnnInferenceService.RunDecon`).
   - **Halo + Upscale** — via `POST /api/onnx/npu-run { op, path, strength?,
     version? }` (`RunHalo` reuses the denoise pipeline; `RunUpscale` is the 2×
     SR path). A FILES/Editor "run on NPU" button is still TODO, so today these
     are API-only (test with curl). On the Rockchip (RKNN) path halo/upscale/
     decon still fall back to the browser / CLI.

   (`qai_hub` = cloud AI Hub path, in the `qnn` env. `qairt-py` is the separate
   local `qairt-converter` toolchain — same result offline, not used here.)

   It reads the input name/shape from each `model.onnx`, runs the same
   w8a16 → `qnn_context_binary` flow, and drops `{family}_v68_int16.bin` under
   the parallel `qnn/` subtree the runtime scans (pass `--int8` for the lossy
   turbo binary, or `--families family-dir:version-dir` for other versions).

2. **Assemble the QAIRT runtime** for the linux-arm64 publish. There is no public
   download for the device-matched 2.45 runtime (the public x86 SDK is 2.31 and
   is version-locked against the board's firmware), so copy it from the board or
   the matching 2.45 SDK:

   ```bash
   # e.g. pull the board's QAIRT tree first, then:
   ./scripts/fetch-qairt.sh /path/to/qairt-source   # → external/qairt/aarch64/{bin,lib,dsp}
   ```

3. Publish / build the `.deb` for `linux-arm64`; the csproj bundles the runtime
   tree at `/opt/polaris/qairt` and the models ship under `wwwroot/graxpert/models`.

### How it works

- `QnnRuntime` probes for the NPU (arm64 + `/dev/fastrpc-cdsp` + the bundled
  QAIRT under `POLARIS_QAIRT_ROOT`).
- `QnnInferenceService` reuses the validated `RknnPipelines` tile math unchanged
  via a record/replay trick (capture tiles → one batched `qnn-net-run` → replay),
  so NPU output matches the browser/CPU pipeline.
- `GraXpertService` tries the NPU first for BGE/Denoise (RKNN or QAIRT, whichever
  is present) and falls back to the CLI on any failure.
