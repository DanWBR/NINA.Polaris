#!/usr/bin/env python3
# N.I.N.A. Polaris
# Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
# Licensed under the GNU Affero General Public License v3.0 or later.
"""
Convert the bundled GraXpert ONNX models to Rockchip RKNN (.rknn) for NPU
acceleration on RK3588/RK3588S boards (Orange Pi 5 Pro, etc.).

For each `{family}-ai-models/{version}/model.onnx` under the models directory
this emits a sibling `model.rknn` (target rk3588, fp16, no quantization). The
N.I.N.A. Polaris host picks the `.rknn` up automatically at runtime when an NPU
is present (see Services/Rknn/RknnInferenceService.cs); otherwise it falls back
to the GraXpert CLI / browser ONNX path.

Only BGE and Denoise are converted by default. Deconvolution uses a different
input layout (NCHW 512 + a sigma/strength params tensor) and multiple inputs,
which the host RKNN path does not implement yet, so converting it would produce
a `.rknn` the runtime won't use. Pass `--families bge,denoise,decon-stars,
decon-objects` only if you are extending the host path too.

Requirements (run on an x86_64 Linux / WSL box, NOT on the board):
    python3.11 -m venv ~/rknn && source ~/rknn/bin/activate
    pip install rknn-toolkit2 onnx
    python3 scripts/convert_rknn_models.py

The conversion is a build/dev-time step; commit the generated `model.rknn`
files next to their `model.onnx`.
"""

import argparse
import os
import sys

# Map model-directory prefixes to the canonical family ids the C# registry uses
# (OnnxModelRegistry.FamilyAliases). We convert by directory name here.
DEFAULT_FAMILIES = ["bge-ai-models", "denoise-ai-models"]
ALL_FAMILIES = DEFAULT_FAMILIES + [
    "deconvolution-stars-ai-models",
    "deconvolution-object-ai-models",
]


def find_input(onnx_path):
    """Return (input_name, [shape]) for the first graph input of an ONNX model."""
    import onnx
    m = onnx.load(onnx_path)
    inp = m.graph.input[0]
    name = inp.name
    dims = []
    for d in inp.type.tensor_type.shape.dim:
        # dim_value is 0 for dynamic dims; the GraXpert image input is fixed
        # except the batch axis, which we pin to 1.
        v = d.dim_value if d.dim_value > 0 else 1
        dims.append(v)
    if dims and dims[0] != 1:
        dims[0] = 1
    return name, dims


def convert_one(onnx_path, platform, force):
    rknn_path = os.path.join(os.path.dirname(onnx_path), "model.rknn")
    if os.path.exists(rknn_path) and not force:
        print(f"  skip (exists): {rknn_path}")
        return True

    from rknn.api import RKNN
    name, shape = find_input(onnx_path)
    print(f"  input '{name}' shape {shape}")

    rknn = RKNN(verbose=False)
    rknn.config(target_platform=platform)   # fp16 by default (no do_quantization)
    if rknn.load_onnx(model=onnx_path, inputs=[name], input_size_list=[shape]) != 0:
        print(f"  ERROR: load_onnx failed for {onnx_path}", file=sys.stderr)
        return False
    if rknn.build(do_quantization=False) != 0:   # fp16; int8 would need a dataset
        print(f"  ERROR: build failed for {onnx_path}", file=sys.stderr)
        return False
    if rknn.export_rknn(rknn_path) != 0:
        print(f"  ERROR: export_rknn failed for {onnx_path}", file=sys.stderr)
        return False
    rknn.release()
    print(f"  OK -> {rknn_path}")
    return True


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    default_models = os.path.normpath(os.path.join(
        here, "..", "src", "NINA.Polaris", "wwwroot", "graxpert", "models"))

    ap = argparse.ArgumentParser(description="Convert GraXpert ONNX models to RKNN.")
    ap.add_argument("--models-dir", default=default_models,
                    help=f"models root (default: {default_models})")
    ap.add_argument("--platform", default="rk3588", help="RKNN target platform")
    ap.add_argument("--families", default=",".join(DEFAULT_FAMILIES),
                    help="comma-separated model-dir prefixes to convert "
                         f"(default bge+denoise; all: {','.join(ALL_FAMILIES)})")
    ap.add_argument("--force", action="store_true", help="re-convert even if model.rknn exists")
    args = ap.parse_args()

    families = [f.strip() for f in args.families.split(",") if f.strip()]
    root = args.models_dir
    if not os.path.isdir(root):
        print(f"models dir not found: {root}", file=sys.stderr)
        return 2

    total = ok = 0
    for family in families:
        fam_dir = os.path.join(root, family)
        if not os.path.isdir(fam_dir):
            continue
        for version in sorted(os.listdir(fam_dir)):
            onnx_path = os.path.join(fam_dir, version, "model.onnx")
            if not os.path.isfile(onnx_path):
                continue
            total += 1
            print(f"[{family}/{version}]")
            if convert_one(onnx_path, args.platform, args.force):
                ok += 1

    print(f"\nDone: {ok}/{total} model(s) converted/up-to-date.")
    return 0 if ok == total else 1


if __name__ == "__main__":
    sys.exit(main())
