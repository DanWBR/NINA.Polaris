"""Export a trained checkpoint to ONNX (fp32 dynamic + fp32 static + fp16).

  # decon (NCHW [N,2,H,W] -> [N,1,H,W], the original contract):
  python export.py --ckpt checkpoints/decon/best.pt --out models --size 256

  # bge / denoise (NHWC [N,256,256,3] -> [N,256,256,3], GraXpert/Polaris contract):
  python export.py --task denoise --ckpt checkpoints/denoise/best.pt --out models
  python export.py --task bge     --ckpt checkpoints/bge/best.pt     --out models

Decon keeps the NCHW image+sigma contract Polaris already consumes. BGE and
denoise are wrapped so the ONNX takes/returns NHWC RGB tensors -- exactly the
shape ``onnx-pipelines.js`` feeds the GraXpert models -- making them drop-in.
"""
from __future__ import annotations

import argparse
import glob
import os

import torch
import torch.nn as nn

from model import ConditionedUNet, UpscaleNet

# in/out channels + tensor layout per task
TASKS = {
    "decon":   {"in": 2, "out": 1, "layout": "nchw"},
    "denoise": {"in": 3, "out": 3, "layout": "nhwc"},
    "bge":     {"in": 3, "out": 3, "layout": "nhwc"},
    "upscale": {"in": 3, "out": 3, "layout": "nhwc"},
}


class NHWCWrapper(nn.Module):
    """Wrap an NCHW model so its ONNX I/O is NHWC [N,H,W,C] (GraXpert layout)."""

    def __init__(self, net: nn.Module):
        super().__init__()
        self.net = net

    def forward(self, x):
        x = x.permute(0, 3, 1, 2).contiguous()   # NHWC -> NCHW
        y = self.net(x)
        return y.permute(0, 2, 3, 1).contiguous()  # NCHW -> NHWC


def inline_onnx(path: str) -> None:
    """Re-save an ONNX file with all weights embedded (single self-contained
    file) and remove any sibling external-data blobs."""
    import onnx

    m = onnx.load(path, load_external_data=True)
    onnx.save(m, path, save_as_external_data=False)
    for blob in glob.glob(path + ".data") + glob.glob(path + "_data"):
        try:
            os.remove(blob)
        except OSError:
            pass


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--task", default="decon", choices=list(TASKS))
    ap.add_argument("--ckpt", required=True)
    ap.add_argument("--out", default="models")
    ap.add_argument("--size", type=int, default=256)
    ap.add_argument("--base", type=int, default=96)
    ap.add_argument("--depth", type=int, default=4)
    ap.add_argument("--blocks", type=int, default=3)
    ap.add_argument("--opset", type=int, default=17)
    ap.add_argument("--scale", type=int, default=2, choices=[2, 3, 4],
                    help="(upscale) super-resolution factor; --size is the LR input size")
    ap.add_argument("--no-fp16", action="store_true")
    args = ap.parse_args()

    spec = TASKS[args.task]
    os.makedirs(args.out, exist_ok=True)
    if args.task == "upscale":
        net = UpscaleNet(scale=args.scale, base=args.base, depth=args.depth,
                         blocks=args.blocks)
    else:
        net = ConditionedUNet(in_channels=spec["in"], base=args.base, depth=args.depth,
                              blocks=args.blocks, out_channels=spec["out"])
    net.load_state_dict(torch.load(args.ckpt, map_location="cpu"))
    net.eval()

    if spec["layout"] == "nhwc":
        model = NHWCWrapper(net).eval()
        dummy = torch.randn(1, args.size, args.size, spec["in"])
    else:
        model = net
        dummy = torch.randn(1, spec["in"], args.size, args.size)

    in_names, out_names = ["gen_input_image"], ["output"]
    fp32 = os.path.join(args.out, f"{args.task}_fp32.onnx")
    torch.onnx.export(
        model, dummy, fp32, opset_version=args.opset,
        input_names=in_names, output_names=out_names,
        dynamic_axes={"gen_input_image": {0: "batch"}, "output": {0: "batch"}},
        dynamo=False,
    )
    inline_onnx(fp32)
    print("wrote", fp32)

    fp32_static = os.path.join(args.out, f"{args.task}_fp32_{args.size}.onnx")
    torch.onnx.export(model, dummy, fp32_static, opset_version=args.opset,
                      input_names=in_names, output_names=out_names, dynamo=False)
    inline_onnx(fp32_static)
    print("wrote", fp32_static)

    if not args.no_fp16:
        try:
            import onnx
            from onnxconverter_common import float16

            m = onnx.load(fp32_static)
            m16 = float16.convert_float_to_float16(m, keep_io_types=True)
            fp16 = os.path.join(args.out, f"{args.task}_fp16_{args.size}.onnx")
            onnx.save(m16, fp16)
            print("wrote", fp16)
        except Exception as e:  # noqa: BLE001
            print("fp16 conversion skipped:", e)

    print("\nNext (build a calib set first: "
          f"python quantize.py calib --task {args.task} "
          f"--pairs data/own/{args.task}_tiles --out models/calib_{args.task}):")
    print(f"  int16 : python quantize.py int16 --onnx {fp32_static} "
          f"--calib models/calib_{args.task} --out models/{args.task}_int16_{args.size}.onnx")
    print(f"  int8  : python quantize.py int8  --onnx {fp32_static} "
          f"--calib models/calib_{args.task} --out models/{args.task}_int8_{args.size}.onnx")


if __name__ == "__main__":
    main()
