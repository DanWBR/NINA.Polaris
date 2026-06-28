#!/usr/bin/env python3
"""Convert every GraXpert ONNX model to ncnn and check parity against ORT.

This is the feasibility spike for the ncnn-Vulkan lane (PLAN.md NCNN-GPU): can we
run the GraXpert models on the SBC GPU through ncnn? ncnn can't load ONNX, so the
route is  ONNX --onnxsim--> static graph --pnnx--> .ncnn.param/.bin , then verify
the converted graph matches ONNX Runtime numerically (CPU here; the Vulkan run on
the Q6A uses the identical graph/weights).

Run from polaris-ai/ncnn:
    python convert_graxpert.py
Outputs land in out/<key>.ncnn.{param,bin}; a PASS/FAIL table is printed.
"""
import os
import subprocess
import sys
import glob
import numpy as np

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
GX = os.path.join(ROOT, "src", "NINA.Polaris", "wwwroot", "graxpert", "models")
OUT = os.path.join(os.path.dirname(__file__), "out")
os.makedirs(OUT, exist_ok=True)

# key -> relative onnx path, list of (input name, shape) — the decon models take a
# second "params" input (strength/bg vector) alongside the image.
MODELS = {
    "bge":          ("bge-ai-models/1.0.1/model.onnx",                 [("gen_input_image", (1, 256, 256, 3))]),
    "denoise_v2":   ("denoise-ai-models/2.0.0/model.onnx",             [("gen_input_image", (1, 256, 256, 3))]),
    "denoise_v3":   ("denoise-ai-models/3.0.2/model.onnx",             [("gen_input_image", (1, 256, 256, 3))]),
    "decon_stars":  ("deconvolution-stars-ai-models/1.0.0/model.onnx", [("gen_input_image", (1, 1, 512, 512)), ("params", (1, 2))]),
    "decon_object": ("deconvolution-object-ai-models/1.0.1/model.onnx",[("gen_input_image", (1, 1, 512, 512)), ("params", (1, 2))]),
}

PNNX = glob.glob(os.path.join(os.path.dirname(__import__("pnnx").__file__), "**", "pnnx*"), recursive=True)
PNNX = next(p for p in PNNX if p.endswith(".exe") or os.access(p, os.X_OK) and not p.endswith(".py"))


def sh(cmd, **kw):
    # errors="replace": pnnx prints non-ASCII progress glyphs that crash the
    # default cp1252 decode on Windows.
    return subprocess.run(cmd, shell=True, capture_output=True, text=True,
                          encoding="utf-8", errors="replace", **kw)


def convert_one(key, rel, inputs):
    src = os.path.join(GX, rel)
    if not os.path.exists(src):
        return key, "MISSING", "-", "onnx not found"
    sim = os.path.join(OUT, f"{key}_sim.onnx")
    shapes = [",".join(str(s) for s in shp) for _, shp in inputs]
    overwrite = " ".join(f"{n}:{s}" for (n, _), s in zip(inputs, shapes))

    # 1) simplify with static input shapes (folds dynamic Shape/Gather chains)
    r = sh(f'python -m onnxsim "{src}" "{sim}" --overwrite-input-shape {overwrite}')
    if not os.path.exists(sim):
        return key, "SIM-FAIL", "-", " ".join((r.stderr or r.stdout).strip().splitlines()[-1:])

    # 2) pnnx -> ncnn  (inputshape=[..],[..] for multi-input models)
    stem = os.path.join(OUT, f"{key}_sim")
    bracket = ",".join("[" + s + "]" for s in shapes)
    r = sh(f'"{PNNX}" "{sim}" inputshape={bracket}', cwd=OUT)
    param, binf = stem + ".ncnn.param", stem + ".ncnn.bin"
    if not (os.path.exists(param) and os.path.exists(binf)):
        tail = (r.stderr or r.stdout).strip().splitlines()[-3:]
        return key, "PNNX-FAIL", "-", " | ".join(tail)

    # 3) parity vs ORT (image gets random data; params a typical [strength, bg])
    try:
        import onnxruntime as ort
        import ncnn
        rng = np.random.default_rng(0)
        feeds, mats = {}, []
        sess = ort.InferenceSession(sim, providers=["CPUExecutionProvider"])
        onames = [i.name for i in sess.get_inputs()]
        for idx, ((name, shp), oname) in enumerate(zip(inputs, onames)):
            a = (rng.random(shp, dtype=np.float32) if idx == 0
                 else np.array([0.5, 0.02], dtype=np.float32).reshape(shp))
            feeds[oname] = a
            mats.append(("in%d" % idx, np.ascontiguousarray(a.squeeze())))
        ref = np.asarray(sess.run(None, feeds)[0]).squeeze()
        net = ncnn.Net()
        net.load_param(param)
        net.load_model(binf)
        ex = net.create_extractor()
        for bn, arr in mats:
            ex.input(bn, ncnn.Mat(arr).clone())
        _, out = ex.extract("out0")
        got = np.asarray(out).squeeze()
        if got.shape != ref.shape and got.size == ref.size:
            got = got.reshape(ref.shape)
        md = float(np.abs(ref - got).max())
        sz = os.path.getsize(binf) / 1e6
        verdict = "PASS" if md < 5e-3 else ("CLOSE" if md < 5e-2 else "DIFF")
        return key, verdict, f"{md:.2e}", f"{sz:.0f} MB bin"
    except Exception as e:
        return key, "PARITY-ERR", "-", str(e)[:80]


def main():
    only = sys.argv[1:] or list(MODELS)
    rows = []
    for key in only:
        rel, inputs = MODELS[key]
        print(f"... converting {key} ({[s for _, s in inputs]})", flush=True)
        rows.append(convert_one(key, rel, inputs))
    print("\n=== GraXpert -> ncnn feasibility ===")
    print(f"{'model':14} {'result':12} {'max|d|':10} notes")
    for k, res, md, note in rows:
        print(f"{k:14} {res:12} {md:10} {note}")


if __name__ == "__main__":
    main()
