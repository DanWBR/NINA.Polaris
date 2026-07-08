#!/usr/bin/env python3
# N.I.N.A. Polaris
# Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
# Licensed under the GNU Affero General Public License v3.0 or later.
"""
Convert bundled ONNX models to a Qualcomm Hexagon HTP **context binary**
(`{family}_v68_int16.bin`) for NPU acceleration on the QCS6490 (Radxa Dragon
Q6A) via the QAIRT runtime. The N.I.N.A. Polaris host picks the `.bin` up
automatically at runtime (see Services/Qnn/QnnInferenceService.QnnBinaryFor);
otherwise it falls back to RKNN / the GraXpert CLI / the browser ONNX path.

Why this exists
---------------
The GraXpert BGE/Denoise context binaries were built ad-hoc on a Qualcomm AI
Hub box and were never committed (their weights are GraXpert NonCommercial).
The **Polaris-trained** models (our own weights) can and should ship their
`.bin` in-repo, but until now there was no reproducible converter. This is it:
the proven Qualcomm AI Hub (`qai_hub`) recipe, parameterised.

The recipe (what actually works on the QCS6490 / Hexagon v68)
-------------------------------------------------------------
The v68 HTP is **integer-only** (INT8/INT16, no FP16). We target the production
**int16** precision via the AI Hub **w8a16** path (INT8 weights + INT16
activations), which lands at ~fp16 quality; plain int8 is ~4x faster but
visibly lower quality on denoise (offered as the `--int8` "turbo" variant).

  source model.onnx
    -> submit_compile_job(--target_runtime onnx, input_specs pinned to [1,H,W,C])
    -> submit_quantize_job(weights=INT8, activations=INT16, calibration_data)
    -> submit_compile_job(--target_runtime qnn_context_binary --quantize_io)
    -> download -> wwwroot/graxpert/models/qnn/{family}-ai-models/{ver}/{family}_v68_int16.bin

Requirements (run on the Linux box with a Qualcomm AI Hub account, NOT here)
---------------------------------------------------------------------------
This uses the cloud **Qualcomm AI Hub** path (`qai_hub`), which lives in the
`qnn` env on the build box. (`qairt-py` is the separate *local* QAIRT-converter
toolchain — `qairt-converter` + `qnn-context-binary-generator` — an alternative
that does the same job offline; this script does not use it.)

    source ~/qnn/bin/activate                         # env that has qai_hub
    qai-hub configure --api_token <YOUR_TOKEN>        # one-time, if not already
    python3 scripts/qnn-convert.py                    # bge + denoise Polaris models

The `.bin` this emits is safe to commit (our own weights). Commit it next to
the parallel ONNX version dir under wwwroot/graxpert/models/qnn/.

By default this converts only the Polaris-trained BGE and Denoise models, the
two families the host QNN path implements (QnnInferenceService.TryFamily).
Deconvolution / halo / upscale use different I/O layouts or run browser-side,
so converting them would produce a `.bin` the runtime won't load yet.
"""

from __future__ import annotations

import argparse
import os
import sys

# (family-dir, version-dir) pairs under the models root. The source ONNX is the
# fp32 base version; AI Hub does the w8a16 quantization itself, so do NOT feed
# an already -int16/-fp16 QDQ export here (that would double-quantize).
#
# DEFAULT = the two families the host QNN path already serves
# (QnnInferenceService.TryFamily): converting these makes them run on the NPU
# immediately.
DEFAULT_TARGETS = [
    ("bge-ai-models", "polaris-1.0.0"),
    ("denoise-ai-models", "polaris-1.0"),
]

# EXTRA (via --all) = families NOT yet wired into the host QNN path. Their `.bin`
# convert fine (halo/upscale are single-input NHWC like bge; decon has two inputs
# — image NCHW 512 + a `params` tensor — which the converter handles generically),
# but QnnInferenceService.TryFamily + the tile pipelines must be extended before
# the runtime actually uses them (today they run browser-side). Decon versions are
# GraXpert NonCommercial weights, so those `.bin` stay gitignored like the GraXpert
# bge/denoise ones; halo/upscale are Polaris weights.
EXTRA_TARGETS = [
    ("halo-ai-models", "polaris-1.0.0"),
    ("upscale-ai-models", "polaris-1.0.0"),
    ("deconvolution-stars-ai-models", "1.0.0"),
    ("deconvolution-object-ai-models", "1.0.1"),
]

# Qualcomm AI Hub device whose SoC is the QCS6490 (Hexagon v68) — the Q6A.
DEFAULT_DEVICE = "Dragonwing RB3 Gen 2 Vision Kit"

DEFAULT_MODELS_ROOT = os.path.join(
    "src", "NINA.Polaris", "wwwroot", "graxpert", "models")


def _family_id(family_dir: str) -> str:
    """`bge-ai-models` -> `bge` (the `.bin` filename prefix the host expects)."""
    return family_dir.replace("-ai-models", "")


def _onnx_inputs(onnx_path: str):
    """Return [(name, [dims]), ...] for ALL graph inputs, pinning any
    symbolic/zero dim to 1 so AI Hub gets a concrete shape. Handles the
    multi-input decon models (image NCHW 512 + a `params` tensor) as well as
    the single-input NHWC bge/denoise/halo/upscale models."""
    import onnx
    g = onnx.load(onnx_path).graph
    out = []
    for i in g.input:
        dims = []
        for d in i.type.tensor_type.shape.dim:
            v = d.dim_value
            dims.append(v if v and v > 0 else 1)
        out.append((i.name, dims))
    return out


def convert_one(hub, np, models_root, family_dir, version_dir, device_name,
                int8=False, calib_count=8, dry_run=False):
    family = _family_id(family_dir)
    src = os.path.join(models_root, family_dir, version_dir, "model.onnx")
    if not os.path.isfile(src):
        print(f"  SKIP {family_dir}/{version_dir}: no model.onnx at {src}")
        return False

    prec = "int8" if int8 else "int16"
    out_dir = os.path.join(models_root, "qnn", family_dir, version_dir)
    out_bin = os.path.join(out_dir, f"{family}_v68_{prec}.bin")
    inputs = _onnx_inputs(src)

    print(f"  {family_dir}/{version_dir}: {src}")
    for name, shape in inputs:
        print(f"    input={name} shape={shape}")
    print(f"    -> {out_bin}")
    if dry_run:
        print("    (dry-run: not submitting AI Hub jobs)")
        return True

    dev = hub.Device(name=device_name)

    # 1. Upload + compile the source to a concrete-shape ONNX target. Pin every
    #    input (decon has two: the image + a small `params` tensor).
    m = hub.upload_model(src)
    input_specs = {name: tuple(shape) for name, shape in inputs}
    sj = hub.submit_compile_job(
        model=m, device=dev,
        input_specs=input_specs,
        options="--target_runtime onnx")
    sm = sj.get_target_model()
    assert sm, f"onnx compile failed: {sj.url}"

    # 2. Quantize. int16 = w8a16 (INT8 weights + INT16 activations), the
    #    production near-fp16 path; int8 is the lossy "turbo" variant. Feed a
    #    random calibration tile per input (the vendor re-quantizes anyway, so
    #    representative ranges are what matter — [0,1) covers the normalized
    #    image and the 0..1 decon strength params).
    act = hub.QuantizeDtype.INT8 if int8 else hub.QuantizeDtype.INT16
    calib = {name: [np.random.rand(*shape).astype(np.float32)
                    for _ in range(calib_count)]
             for name, shape in inputs}
    qj = hub.submit_quantize_job(
        model=sm, calibration_data=calib,
        weights_dtype=hub.QuantizeDtype.INT8, activations_dtype=act)
    qm = qj.get_target_model()
    assert qm, f"quantize failed: {qj.url}"

    # 3. Compile the quantized model to a Hexagon context binary and download.
    cj = hub.submit_compile_job(
        model=qm, device=dev,
        options="--target_runtime qnn_context_binary --quantize_io")
    print(f"    compile: {cj.url}")
    os.makedirs(out_dir, exist_ok=True)
    cj.download_target_model(out_bin)
    ok = os.path.isfile(out_bin) and os.path.getsize(out_bin) > 0
    print(f"    {'OK' if ok else 'FAILED'} -> {out_bin}")
    return ok


def main():
    ap = argparse.ArgumentParser(
        description="Convert Polaris ONNX models to Hexagon HTP context binaries via Qualcomm AI Hub")
    ap.add_argument("--models-root", default=DEFAULT_MODELS_ROOT,
                    help="models directory (default: %(default)s)")
    ap.add_argument("--device", default=DEFAULT_DEVICE,
                    help="Qualcomm AI Hub device name (QCS6490 SoC). Default: %(default)s")
    ap.add_argument("--families", default="",
                    help="comma list of family-dir:version-dir to convert "
                         "(default: the Polaris BGE + Denoise models)")
    ap.add_argument("--all", action="store_true",
                    help="also convert halo + upscale + decon (stars/object). "
                         "Their .bin build fine but the host QNN path does not "
                         "serve these families yet (they run browser-side).")
    ap.add_argument("--int8", action="store_true",
                    help='build the lossy "turbo" int8 binary instead of int16')
    ap.add_argument("--calib-count", type=int, default=8,
                    help="number of random calibration tiles (default: %(default)s)")
    ap.add_argument("--dry-run", action="store_true",
                    help="resolve sources + output paths without submitting AI Hub jobs")
    args = ap.parse_args()

    if args.families:
        targets = []
        for tok in args.families.split(","):
            tok = tok.strip()
            if not tok:
                continue
            fam, _, ver = tok.partition(":")
            if not ver:
                print(f"bad --families entry '{tok}' (want family-dir:version-dir)")
                return 2
            targets.append((fam, ver))
    else:
        targets = list(DEFAULT_TARGETS)
        if args.all:
            targets += EXTRA_TARGETS

    hub = np = None
    if not args.dry_run:
        try:
            import qai_hub as hub  # type: ignore
            import numpy as np
        except ImportError:
            print("qai_hub / numpy not found. Run this in the `qnn` env (the one "
                  "with qai_hub) on the build box:\n"
                  "  source ~/qnn/bin/activate\n"
                  "  qai-hub configure --api_token <YOUR_TOKEN>   # if not already\n"
                  "Use --dry-run to preview source/output paths without it.")
            return 1

    print(f"device: {args.device}   precision: {'int8' if args.int8 else 'int16 (w8a16)'}")
    any_fail = False
    for fam, ver in targets:
        ok = convert_one(hub, np, args.models_root, fam, ver, args.device,
                         int8=args.int8, calib_count=args.calib_count,
                         dry_run=args.dry_run)
        any_fail = any_fail or not ok
    return 1 if any_fail else 0


if __name__ == "__main__":
    sys.exit(main())
