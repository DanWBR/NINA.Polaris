"""
GX-12n: shrink the GraXpert ONNX models so they fit comfortably under
iOS Safari's per-tab memory budget (~1 GB). Two strategies are
supported here; either is safe to ship alongside the FP32 original.

  FP16 (recommended)   — halves the on-disk size (284 → ~142 MB) and
                         the runtime memory cost, with quality drop
                         that is essentially imperceptible on
                         astrophotography frames. Compatible with both
                         ONNX Runtime Web's wasm and webgpu backends.

  INT8 (dynamic)       — quarters the on-disk size (284 → ~71 MB) and
                         is faster on CPU/WASM. Quality drop is small
                         but real on AI image-restoration models —
                         you may see slightly more posterisation in
                         deep shadows. Try it; if the output looks
                         worse than FP16, fall back to FP16.

Outputs land next to the source as a sibling version directory so
OnnxModelRegistry picks them up automatically as new {family}/{version}
entries. Example: a source at

  wwwroot/graxpert/models/denoise-ai-models/2.0.0/model.onnx

becomes

  wwwroot/graxpert/models/denoise-ai-models/2.0.0-fp16/model.onnx
  wwwroot/graxpert/models/denoise-ai-models/2.0.0-int8/model.onnx

The UI dropdown then sees three Denoise versions: "2.0.0" (original
FP32), "2.0.0-fp16", "2.0.0-int8". Pick the one that fits.

Usage:

  pip install onnx onnxruntime onnxconverter-common onnxsim
  python scripts/quantize_onnx_models.py            # all models, FP16 (sibling dir)
  python scripts/quantize_onnx_models.py --int8     # all models, INT8
  python scripts/quantize_onnx_models.py --fp16 --int8   # both
  python scripts/quantize_onnx_models.py --only denoise   # one family

  # Star-removal models: shrink the bundled 1.0.0 IN PLACE (halves them,
  # version stays 1.0.0, runs on phones/tablets via WebGPU + WASM):
  python scripts/quantize_onnx_models.py --fp16 --replace \
      --only nox --only starrem2k13 --only starnet
"""
import argparse, os, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MODELS_ROOT = ROOT / "src" / "NINA.Polaris" / "wwwroot" / "graxpert" / "models"


def _find_originals():
    """Walk the wwwroot models tree, yielding (family_dir, version_dir,
    onnx_path) tuples for every plain model.onnx that ISN'T already a
    quantized sibling (skip versions ending in -fp16 / -int8)."""
    if not MODELS_ROOT.is_dir():
        print(f"ERROR: models dir not found at {MODELS_ROOT}")
        print("Drop your GraXpert models there first (see the README in "
              "that folder).")
        sys.exit(1)
    for family_dir in sorted(MODELS_ROOT.iterdir()):
        if not family_dir.is_dir() or not family_dir.name.endswith("-ai-models"):
            continue
        for version_dir in sorted(family_dir.iterdir()):
            if not version_dir.is_dir():
                continue
            name = version_dir.name
            # Skip our own outputs so re-runs are idempotent.
            if name.endswith("-fp16") or name.endswith("-int8"):
                continue
            onnx = version_dir / "model.onnx"
            if onnx.is_file():
                yield family_dir, version_dir, onnx


def _size_mb(path: Path) -> float:
    return path.stat().st_size / (1024 * 1024)


def to_fp16(src: Path, dst: Path) -> None:
    """Convert all float32 weights to float16. Uses
    onnxconverter_common.float16.convert_float_to_float16 which is the
    canonical path — it walks the graph, swaps tensor types, and
    inserts the small number of Cast nodes needed at boundaries (input/
    output ops that the runtime can't accept in fp16)."""
    import onnx
    from onnxconverter_common import float16
    print(f"  loading {src.name} ({_size_mb(src):.1f} MB)...")
    model = onnx.load(str(src))
    # Simplify FIRST. tf2onnx exports (StarNet / nox / starrem2k13) decompose
    # LayerNorm into Mul/Div/Sub/Cast chains that trip the fp16 converter
    # (it leaves a Div / Cast with mixed fp16+fp32 inputs → the model fails
    # to load). onnxsim folds those constant-casts and canonicalises the
    # graph so the conversion produces a valid model. Best-effort: if onnxsim
    # is missing or fails, fall back to the raw model (fine for the simpler
    # GraXpert graphs that converted cleanly before).
    try:
        from onnxsim import simplify
        inp = model.graph.input[0]
        dims = [(d.dim_value if (d.HasField('dim_value') and d.dim_value > 0) else 1)
                for d in inp.type.tensor_type.shape.dim]
        model, ok = simplify(model, overwrite_input_shapes={inp.name: dims})
        if not ok:
            print("  (onnxsim reported a check mismatch — using simplified graph anyway)")
    except Exception as e:
        print(f"  (onnxsim unavailable/failed: {e} — converting raw graph)")
    # keep_io_types=True so we still accept FP32 inputs / emit FP32 outputs —
    # saves the JS pipeline from converting tensors. shape inference ON so the
    # converter places the boundary Cast nodes with correct types.
    converted = float16.convert_float_to_float16(
        model, keep_io_types=True, disable_shape_infer=False)
    dst.parent.mkdir(parents=True, exist_ok=True)
    onnx.save(converted, str(dst))
    print(f"  → {dst.relative_to(MODELS_ROOT)} ({_size_mb(dst):.1f} MB)")


def to_int8_dynamic(src: Path, dst: Path) -> None:
    """Quantize weights to INT8 (activations stay FP32). Dynamic
    quantization means the activations are computed in FP32 at
    inference time then converted to INT8 on the fly per op — no
    calibration dataset needed."""
    from onnxruntime.quantization import quantize_dynamic, QuantType
    print(f"  loading {src.name} ({_size_mb(src):.1f} MB)...")
    dst.parent.mkdir(parents=True, exist_ok=True)
    # weight_type=QInt8 over QUInt8 because image-restoration models'
    # weights tend to span the signed range; UInt8 truncates negatives.
    # per_channel=False keeps the output small and is more compatible
    # with ORT Web backends (per-channel int8 needs ContribOps in
    # some builds). reduce_range=True helps numerical stability on
    # WASM SIMD backends without per-channel quant.
    quantize_dynamic(
        model_input=str(src),
        model_output=str(dst),
        weight_type=QuantType.QInt8,
        per_channel=False,
        reduce_range=True,
    )
    print(f"  → {dst.relative_to(MODELS_ROOT)} ({_size_mb(dst):.1f} MB)")


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--fp16", action="store_true",
                    help="emit FP16 variants (default if neither flag passed)")
    ap.add_argument("--int8", action="store_true",
                    help="emit INT8 dynamic-quantized variants")
    ap.add_argument("--only", action="append", default=[],
                    metavar="FAMILY",
                    help="restrict to families containing this substring "
                         "(e.g. 'denoise', 'bge'). Repeat to combine.")
    ap.add_argument("--force", action="store_true",
                    help="overwrite existing quantized outputs")
    ap.add_argument("--replace", action="store_true",
                    help="overwrite the source model.onnx IN PLACE instead of "
                         "writing a -fp16/-int8 sibling (halves the bundled "
                         "model; the registry version stays e.g. '1.0.0'). "
                         "FP16 only.")
    args = ap.parse_args()
    if not args.fp16 and not args.int8:
        args.fp16 = True   # default action

    targets = list(_find_originals())
    if args.only:
        targets = [t for t in targets
                   if any(o.lower() in t[0].name.lower() for o in args.only)]
    if not targets:
        print("No FP32 model.onnx files found to quantize.")
        sys.exit(0)

    for family_dir, version_dir, onnx_path in targets:
        print(f"\n[{family_dir.name} / {version_dir.name}]")
        if args.fp16:
            dst = (onnx_path if args.replace
                   else family_dir / f"{version_dir.name}-fp16" / "model.onnx")
            if dst.exists() and not args.force and not args.replace:
                print(f"  fp16 already exists ({_size_mb(dst):.1f} MB) — skip "
                      f"(pass --force to overwrite)")
            else:
                try:
                    to_fp16(onnx_path, dst)
                except Exception as e:
                    print(f"  FP16 conversion FAILED: {e}")
        if args.int8:
            dst = family_dir / f"{version_dir.name}-int8" / "model.onnx"
            if dst.exists() and not args.force:
                print(f"  int8 already exists ({_size_mb(dst):.1f} MB) — skip "
                      f"(pass --force to overwrite)")
            else:
                try:
                    to_int8_dynamic(onnx_path, dst)
                except Exception as e:
                    print(f"  INT8 dynamic conversion FAILED: {e}")

    print("\nDone. Restart Polaris (or POST /api/onnx/rescan) so the "
          "registry picks up the new variants. They'll appear in the "
          "Denoise / Decon AI-model dropdowns as e.g. '2.0.0-fp16'.")


if __name__ == "__main__":
    main()
