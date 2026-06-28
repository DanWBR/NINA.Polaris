#!/usr/bin/env python3
"""Convert every GraXpert family to fp16 ncnn and deploy into the app tree.

Produces `model.ncnn.param` + `model.ncnn.bin` (fp16 weights) for each family and
copies them to
    src/NINA.Polaris/wwwroot/graxpert/models/ncnn/{family}-ai-models/{version}/
which is the parallel layout NcnnInferenceService.SiblingNcnn resolves. CPU parity
vs ONNX Runtime is reported per family (random input); the Vulkan-path
correctness still has to be confirmed on the target GPU (bench.py) — families with
ReduceMean/ConvTranspose are the ones at risk of NaN on Vulkan (see the spike).

Run from polaris-ai/ncnn:  python deploy_ncnn_models.py
"""
import os
import shutil
import subprocess
import glob
import numpy as np

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
GX = os.path.join(ROOT, "src", "NINA.Polaris", "wwwroot", "graxpert", "models")
DEST_ROOT = os.path.join(GX, "ncnn")
WORK = os.path.join(os.path.dirname(__file__), "out")
os.makedirs(WORK, exist_ok=True)

PNNX = next(p for p in glob.glob(os.path.join(os.path.dirname(__import__("pnnx").__file__), "**", "pnnx*"), recursive=True)
            if p.endswith(".exe"))

# key -> (family-dir, version, [(input-name, shape)])  — shapes are the onnx layout
MODELS = {
    "bge":          ("bge-ai-models", "1.0.1",  [("gen_input_image", (1, 256, 256, 3))]),
    "denoise_v2":   ("denoise-ai-models", "2.0.0", [("gen_input_image", (1, 256, 256, 3))]),
    "denoise_v3":   ("denoise-ai-models", "3.0.2", [("gen_input_image", (1, 256, 256, 3))]),
    "decon_stars":  ("deconvolution-stars-ai-models", "1.0.0", [("gen_input_image", (1, 1, 512, 512)), ("params", (1, 2))]),
    "decon_object": ("deconvolution-object-ai-models", "1.0.1", [("gen_input_image", (1, 1, 512, 512)), ("params", (1, 2))]),
    "starnet":      ("starnet-ai-models", "1.0.0", [("X:0", (1, 256, 256, 3))]),
    "starrem2k13":  ("starrem2k13-ai-models", "1.0.0", [("args_0", (1, 512, 512))]),
    "nox_color":    ("nox-color-ai-models", "1.0.0", [("gen_input_image", (1, 512, 512, 3))]),
    "nox_gray":     ("nox-gray-ai-models", "1.0.0", [("gen_input_image", (1, 512, 512, 1))]),
}


def sh(cmd, **kw):
    return subprocess.run(cmd, shell=True, capture_output=True, text=True,
                          encoding="utf-8", errors="replace", **kw)


def convert(key, fam, ver, inputs):
    src = os.path.join(GX, fam, ver, "model.onnx")
    if not os.path.exists(src):
        return key, "MISSING", "-", "-"
    sim = os.path.join(WORK, f"{key}_d.onnx")
    shapes = [",".join(str(s) for s in shp) for _, shp in inputs]
    overwrite = " ".join(f"{n}:{s}" for (n, _), s in zip(inputs, shapes))
    r = sh(f'python -m onnxsim "{src}" "{sim}" --overwrite-input-shape {overwrite}')
    if not os.path.exists(sim):
        return key, "SIM-FAIL", "-", " ".join((r.stderr or r.stdout).strip().splitlines()[-1:])

    stem = os.path.join(WORK, f"{key}_d")
    bracket = ",".join("[" + s + "]" for s in shapes)
    sh(f'"{PNNX}" "{sim}" inputshape={bracket} fp16=1', cwd=WORK)
    param, binf = stem + ".ncnn.param", stem + ".ncnn.bin"
    if not (os.path.exists(param) and os.path.exists(binf)):
        return key, "PNNX-FAIL", "-", "-"

    # CPU parity vs ORT
    verdict = "?"
    try:
        import onnxruntime as ort, ncnn
        rng = np.random.default_rng(0)
        sess = ort.InferenceSession(sim, providers=["CPUExecutionProvider"])
        onames = [i.name for i in sess.get_inputs()]
        feeds, mats = {}, []
        for idx, ((nm, shp), on) in enumerate(zip(inputs, onames)):
            a = (rng.random(shp, dtype=np.float32) if idx == 0
                 else np.array([0.5, 0.02], dtype=np.float32).reshape(shp))
            feeds[on] = a
            mats.append(("in%d" % idx, np.ascontiguousarray(a.squeeze())))
        ref = np.asarray(sess.run(None, feeds)[0]).squeeze()
        net = ncnn.Net()
        net.load_param(param); net.load_model(binf)
        ex = net.create_extractor()
        for bn, arr in mats:
            ex.input(bn, ncnn.Mat(arr).clone())
        _, out = ex.extract("out0")
        got = np.asarray(out).squeeze()
        if got.shape != ref.shape and got.size == ref.size:
            got = got.reshape(ref.shape)
        md = float(np.abs(ref - got).max())
        verdict = "PASS" if md < 5e-3 else ("CLOSE" if md < 5e-2 else f"DIFF")
        verdict += f" {md:.1e}"
    except Exception as e:
        verdict = "ERR " + str(e)[:40]

    # deploy
    dest = os.path.join(DEST_ROOT, fam, ver)
    os.makedirs(dest, exist_ok=True)
    shutil.copy2(param, os.path.join(dest, "model.ncnn.param"))
    shutil.copy2(binf, os.path.join(dest, "model.ncnn.bin"))
    sz = os.path.getsize(binf) / 1e6
    return key, verdict, f"{sz:.0f}MB", f"{fam}/{ver}"


def main():
    import sys
    print(f"{'family':14} {'cpu parity':14} {'bin':8} dest", flush=True)
    total = 0.0
    for k, v in MODELS.items():
        try:
            _, verdict, sz, dest = convert(k, *v)
        except Exception as e:
            verdict, sz, dest = "CRASH " + str(e)[:30], "-", "-"
        print(f"{k:14} {verdict:14} {sz:8} {dest}", flush=True)
        if sz.endswith("MB"):
            total += float(sz[:-2])
    print(f"total committed: ~{total:.0f} MB", flush=True)
    sys.stdout.flush()
    os._exit(0)   # skip the ncnn native teardown crash on Windows


if __name__ == "__main__":
    main()
