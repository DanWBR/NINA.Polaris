"""Quantization helpers.

Reality check: for the on-device targets Polaris uses, the *actual* int8/int16
quantization is done by the vendor toolchain with a calibration set --
RKNN (`do_quantization` + a dataset), Qualcomm AI Hub (`submit_quantize_job`,
w8a16 for int16), or qairt with `--quantize_io`. Those re-quantize anyway, so the
most valuable, reusable artifact this script makes is a **calibration set** of
representative model inputs. It also produces an ONNX-Runtime **int8 QDQ** model
you can sanity-test on CPU immediately.

  # 1. dump a calibration set (image + sigma tiles) from the training data:
  python quantize.py calib --tiles data/tiles --out models/calib --count 300

  # 2. CPU-testable int8 baseline (static QDQ):
  python quantize.py int8 --onnx models/decon_fp32_256.onnx --calib models/calib \
       --out models/decon_int8_256.onnx

For int16 (the production NPU precision) use the vendor path with models/calib:
  * AI Hub : submit_quantize_job(weights=INT8, activations=INT16) then compile
             --quantize_io  (see polaris_rknn_npu_feasibility notes)
  * RKNN   : rknn.build(do_quantization=True, dataset=models/calib/list.txt)
"""
from __future__ import annotations

import argparse
import glob
import os

import numpy as np


# --------------------------------------------------------------------------- #
# Calibration set: representative model inputs.
#   decon          -> NCHW [2,H,W] (image + sigma map)
#   denoise / bge  -> NHWC [H,W,3] (matches the wrapped ONNX I/O)
# The saved .npy layout MUST match the exported ONNX so quantize_static feeds it
# correctly (the Reader just prepends a batch dim).
# --------------------------------------------------------------------------- #
def cmd_calib(args):
    os.makedirs(args.out, exist_ok=True)
    nhwc_task = args.task in ("denoise", "bge", "upscale", "halo")

    if args.task == "decon":
        from dataset import DeconDataset
        ds = DeconDataset(args.tiles, tile=args.size, augment=False)
        get = lambda i: ds[int(i)][0].numpy().astype(np.float32)   # [2,H,W]
        n = len(ds)
    else:
        from paired_dataset import PairedTileDataset
        ds = PairedTileDataset(args.pairs or args.tiles, augment=False)
        get = lambda i: ds[int(i)][0].numpy().astype(np.float32)   # [3,H,W]
        n = len(ds)

    idxs = np.random.default_rng(0).choice(n, size=min(args.count, n), replace=False)
    listf = open(os.path.join(args.out, "list.txt"), "w")
    for j, i in enumerate(idxs):
        arr = get(i)
        if nhwc_task:
            arr = np.transpose(arr, (1, 2, 0)).copy()              # [H,W,3] NHWC
        np.save(os.path.join(args.out, f"calib_{j:04d}.npy"), arr)
        # vendor tools (qairt/AI Hub) want raw NHWC
        raw = arr if nhwc_task else np.transpose(arr, (1, 2, 0)).copy()
        raw.tofile(os.path.join(args.out, f"calib_{j:04d}.raw"))
        listf.write(os.path.join(args.out, f"calib_{j:04d}.raw") + "\n")
    listf.close()
    print(f"wrote {len(idxs)} {args.task} calibration tiles -> {args.out}")


# --------------------------------------------------------------------------- #
# ONNX Runtime static quantization (QDQ) -- CPU sanity baseline
# --------------------------------------------------------------------------- #
# Precision matters a LOT for deconvolution: the model learns a small residual
# (out = image + delta), and int8's 256 levels round the high-frequency delta
# away -> garbage (PSNR collapses below the blurred input). int16 (or w8a16 =
# int8 weights + int16 activations, the AI-Hub production recipe) preserves the
# residual and lands near fp16. So int8 is "turbo, lossy"; int16/w8a16 is the
# real quantized path.
def _quant(args, act, weight):
    from onnxruntime.quantization import (CalibrationDataReader, QuantFormat,
                                          QuantType, quantize_static)

    files = sorted(glob.glob(os.path.join(args.calib, "*.npy")))
    if not files:
        raise FileNotFoundError(f"no calib .npy under {args.calib} (run `calib` first)")

    class Reader(CalibrationDataReader):
        def __init__(self, paths, input_name):
            self.paths = list(paths)
            self.input_name = input_name
            self.i = 0

        def get_next(self):
            if self.i >= len(self.paths):
                return None
            a = np.load(self.paths[self.i])[None, ...].astype(np.float32)  # [1,2,H,W]
            self.i += 1
            return {self.input_name: a}

    import onnx
    input_name = onnx.load(args.onnx).graph.input[0].name
    is16 = QuantType.QInt16 in (act, weight)
    quantize_static(
        args.onnx, args.out,
        calibration_data_reader=Reader(files, input_name),
        quant_format=QuantFormat.QDQ,
        activation_type=act,
        weight_type=weight,
        per_channel=True,
        # 16-bit QDQ uses the com.microsoft contrib ops unless opset>=21; this
        # keeps the CPU sanity check runnable.
        extra_options={"UseQDQContribOps": True} if is16 else None,
    )
    print("wrote", args.out)
    print("test on CPU with onnxruntime; compare PSNR/FWHM vs the fp32 model.")


def cmd_int8(args):
    from onnxruntime.quantization import QuantType
    _quant(args, QuantType.QInt8, QuantType.QInt8)


def cmd_int16(args):
    from onnxruntime.quantization import QuantType
    _quant(args, QuantType.QInt16, QuantType.QInt16)


def cmd_w8a16(args):
    from onnxruntime.quantization import QuantType
    _quant(args, QuantType.QInt16, QuantType.QInt8)   # int16 activations, int8 weights


def main():
    ap = argparse.ArgumentParser(description="polaris-ai quantization")
    sub = ap.add_subparsers(dest="cmd", required=True)

    c = sub.add_parser("calib", help="dump a calibration set from training tiles")
    c.add_argument("--task", default="decon",
                   choices=["decon", "denoise", "bge", "upscale", "halo"])
    c.add_argument("--tiles", default="", help="(decon) sharp-tile dir for DeconDataset")
    c.add_argument("--pairs", default="", help="(denoise/bge/upscale/halo) paired-tile root")
    c.add_argument("--out", default="models/calib")
    c.add_argument("--count", type=int, default=300)
    c.add_argument("--size", type=int, default=256)
    c.set_defaults(func=cmd_calib)

    def _add_quant(name, fn, default_out):
        q = sub.add_parser(name, help=f"ONNX Runtime static {name} (QDQ)")
        q.add_argument("--onnx", required=True)
        q.add_argument("--calib", default="models/calib")
        q.add_argument("--out", default=default_out)
        q.set_defaults(func=fn)

    _add_quant("int8", cmd_int8, "models/decon_int8.onnx")     # turbo, lossy -- avoid for decon
    _add_quant("int16", cmd_int16, "models/decon_int16.onnx")  # ~fp16 quality (recommended)
    _add_quant("w8a16", cmd_w8a16, "models/decon_w8a16.onnx")  # int8 weights + int16 acts

    args = ap.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
