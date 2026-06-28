#!/usr/bin/env python3
"""Compare a converted ncnn model against ONNX Runtime on the same random input.

Confirms the ONNX -> ncnn conversion is numerically faithful (CPU here; the SBC
run uses the Vulkan backend but the graph/weights are identical). Usage:

    python parity_check.py out/bge_sim.onnx out/bge_sim.ncnn 1 256 256 3
                            ^onnx           ^ncnn stem        N  H   W  C
"""
import sys
import numpy as np
import onnxruntime as ort
import ncnn


def main():
    onnx_path = sys.argv[1]
    ncnn_stem = sys.argv[2]                  # path without .param/.bin
    n, h, w, c = (int(x) for x in sys.argv[3:7])

    rng = np.random.default_rng(0)
    x = rng.random((n, h, w, c), dtype=np.float32)

    # --- ONNX Runtime (reference) ---
    sess = ort.InferenceSession(onnx_path, providers=["CPUExecutionProvider"])
    iname = sess.get_inputs()[0].name
    ref = sess.run(None, {iname: x})[0]      # (n,h,w,c)
    ref = np.asarray(ref).reshape(h, w, c)

    # --- ncnn ---
    # The pnnx graph keeps the model's NHWC layout (leading/trailing Permute),
    # so the input Mat is the (h,w,c) array and out0 comes back (h,w,c) directly
    # — no axis juggling needed (confirmed: as-is gives the smallest residual).
    net = ncnn.Net()
    net.load_param(ncnn_stem + ".param")
    net.load_model(ncnn_stem + ".bin")
    ex = net.create_extractor()
    ex.input("in0", ncnn.Mat(np.ascontiguousarray(x.reshape(h, w, c))).clone())
    _, out = ex.extract("out0")
    got = np.asarray(out)                     # already (h,w,c)

    # --- compare ---
    d = np.abs(ref - got)
    denom = np.abs(ref).mean() + 1e-9
    corr = np.corrcoef(ref.ravel(), got.ravel())[0, 1]
    print(f"shapes  ref={ref.shape}  ncnn={got.shape}")
    print(f"max|Δ|  = {d.max():.6e}")
    print(f"mean|Δ| = {d.mean():.6e}  ({100*d.mean()/denom:.4f}% of mean|ref|)")
    print(f"pearson = {corr:.8f}")
    ok = d.max() < 2e-3 and corr > 0.9999
    print("RESULT  :", "PASS — ncnn matches ORT" if ok else "CHECK — diff larger than expected")


if __name__ == "__main__":
    main()
