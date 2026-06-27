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
# Calibration set: representative [2,H,W] inputs (image + sigma map)
# --------------------------------------------------------------------------- #
def cmd_calib(args):
    import torch
    from dataset import DeconDataset

    os.makedirs(args.out, exist_ok=True)
    ds = DeconDataset(args.tiles, tile=args.size, augment=False)
    idxs = np.random.default_rng(0).choice(len(ds), size=min(args.count, len(ds)),
                                           replace=False)
    listf = open(os.path.join(args.out, "list.txt"), "w")
    for j, i in enumerate(idxs):
        x, _ = ds[int(i)]                  # x: [2,H,W] float32
        arr = x.numpy().astype(np.float32)
        np.save(os.path.join(args.out, f"calib_{j:04d}.npy"), arr)
        # also a NHWC .raw for qnn-style calibration (qairt/AI Hub want raw)
        nhwc = np.transpose(arr, (1, 2, 0)).copy()
        nhwc.tofile(os.path.join(args.out, f"calib_{j:04d}.raw"))
        listf.write(os.path.join(args.out, f"calib_{j:04d}.raw") + "\n")
    listf.close()
    print(f"wrote {len(idxs)} calibration tiles -> {args.out}")


# --------------------------------------------------------------------------- #
# ONNX Runtime static int8 (QDQ) -- CPU sanity baseline
# --------------------------------------------------------------------------- #
def cmd_int8(args):
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
    quantize_static(
        args.onnx, args.out,
        calibration_data_reader=Reader(files, input_name),
        quant_format=QuantFormat.QDQ,
        activation_type=QuantType.QInt8,
        weight_type=QuantType.QInt8,
        per_channel=True,
    )
    print("wrote", args.out)
    print("test on CPU with onnxruntime; compare PSNR/FWHM vs the fp32 model.")


def main():
    ap = argparse.ArgumentParser(description="polaris-ai quantization")
    sub = ap.add_subparsers(dest="cmd", required=True)

    c = sub.add_parser("calib", help="dump a calibration set from training tiles")
    c.add_argument("--tiles", required=True)
    c.add_argument("--out", default="models/calib")
    c.add_argument("--count", type=int, default=300)
    c.add_argument("--size", type=int, default=256)
    c.set_defaults(func=cmd_calib)

    q = sub.add_parser("int8", help="ONNX Runtime static int8 (QDQ) baseline")
    q.add_argument("--onnx", required=True)
    q.add_argument("--calib", default="models/calib")
    q.add_argument("--out", default="models/decon_int8.onnx")
    q.set_defaults(func=cmd_int8)

    args = ap.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
