# NPU acceleration (Rockchip RK3588)

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
