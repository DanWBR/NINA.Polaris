"""Export a trained checkpoint to ONNX (fp32) and a float16 ONNX.

  python export.py --ckpt checkpoints/decon/best.pt --out models --size 256

The ONNX input is a single tensor [N, 2, H, W] (image + sigma map) and output is
[N, 1, H, W] -- matching the model contract documented in the README. Both a
fixed-size (NPU/converter-friendly) and a dynamic-batch graph are emitted.
"""
from __future__ import annotations

import argparse
import os

import torch

from model import ConditionedUNet


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--ckpt", required=True)
    ap.add_argument("--out", default="models")
    ap.add_argument("--size", type=int, default=256)
    ap.add_argument("--base", type=int, default=48)
    ap.add_argument("--depth", type=int, default=4)
    ap.add_argument("--opset", type=int, default=17)
    ap.add_argument("--no-fp16", action="store_true")
    args = ap.parse_args()

    os.makedirs(args.out, exist_ok=True)
    net = ConditionedUNet(in_channels=2, base=args.base, depth=args.depth)
    net.load_state_dict(torch.load(args.ckpt, map_location="cpu"))
    net.eval()

    dummy = torch.randn(1, 2, args.size, args.size)
    fp32 = os.path.join(args.out, "decon_fp32.onnx")
    torch.onnx.export(
        net, dummy, fp32, opset_version=args.opset,
        input_names=["input"], output_names=["output"],
        dynamic_axes={"input": {0: "batch"}, "output": {0: "batch"}},
    )
    print("wrote", fp32)

    # also a fixed-shape (static batch=1) graph -- friendliest for NPU converters
    fp32_static = os.path.join(args.out, f"decon_fp32_{args.size}.onnx")
    torch.onnx.export(
        net, dummy, fp32_static, opset_version=args.opset,
        input_names=["input"], output_names=["output"],
    )
    print("wrote", fp32_static)

    if not args.no_fp16:
        try:
            import onnx
            from onnxconverter_common import float16

            m = onnx.load(fp32_static)
            m16 = float16.convert_float_to_float16(m, keep_io_types=True)
            fp16 = os.path.join(args.out, f"decon_fp16_{args.size}.onnx")
            onnx.save(m16, fp16)
            print("wrote", fp16)
        except Exception as e:  # noqa: BLE001
            print("fp16 conversion skipped:", e)

    print("\nNext:")
    print("  RKNN  : scripts/convert_rknn_models.py on decon_fp32_*.onnx")
    print("  QNN   : qairt-converter (GPU fp32 / HTP int via quantize.py)")
    print("  ORT-Web: drop decon_fp32.onnx / decon_fp16_*.onnx into the registry")


if __name__ == "__main__":
    main()
