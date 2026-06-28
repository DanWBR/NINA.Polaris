#!/usr/bin/env python3
"""Time a converted ncnn model on CPU vs Vulkan GPU.

This is the SBC-side half of the ncnn spike. Run it on the target board (e.g. the
Radxa Dragon Q6A, Adreno 643 via Turnip/Vulkan):

    pip install ncnn numpy
    python bench.py out/bge_sim.ncnn 256 256 3          # NHWC model
    python bench.py out/decon_stars_sim.ncnn 1 512 512  # NCHW model (+ params in1)

It loads the .param/.bin, runs a warmup then N timed iterations on the CPU and,
if a Vulkan device is present, on the GPU, and prints ms/tile + speedup. Use this
to decide whether the Adreno GPU is worth it before wiring the lane in C#.
"""
import os
import sys
import time
import numpy as np
import ncnn


def bench(stem, shape, use_vulkan, loops, threads, extra_inputs, fp16=False, gpu=-1):
    net = ncnn.Net()
    net.opt.use_vulkan_compute = use_vulkan
    net.opt.num_threads = threads
    if use_vulkan and gpu >= 0:
        net.set_vulkan_device(gpu)   # pin to a specific device (skip llvmpipe)
    # fp16 is the production mode on Adreno (Turnip reports fp16-s/a=1). Storage +
    # packed cut bandwidth; arithmetic does the matmuls in half precision.
    net.opt.use_fp16_packed = fp16
    net.opt.use_fp16_storage = fp16
    net.opt.use_fp16_arithmetic = fp16
    net.load_param(stem + ".param")
    net.load_model(stem + ".bin")

    x = np.random.default_rng(0).random(shape, dtype=np.float32)

    def once():
        ex = net.create_extractor()
        ex.input("in0", ncnn.Mat(np.ascontiguousarray(x)).clone())
        for name, arr in extra_inputs:
            ex.input(name, ncnn.Mat(np.ascontiguousarray(arr)).clone())
        _, out = ex.extract("out0")
        return np.asarray(out)

    once()  # warmup (shader compile / allocation)
    once()
    t0 = time.perf_counter()
    for _ in range(loops):
        once()
    dt = (time.perf_counter() - t0) / loops * 1000.0
    net.clear()
    return dt


def pick_real_gpu():
    """Prefer a hardware device over a software rasterizer (llvmpipe/lavapipe)."""
    for i in range(ncnn.get_gpu_count()):
        try:
            name = ncnn.get_gpu_info(i).device_name().lower()
        except Exception:
            return i
        if "llvmpipe" not in name and "lavapipe" not in name and "software" not in name:
            return i
    return 0


def main():
    stem = sys.argv[1]
    dims = [int(a) for a in sys.argv[2:] if a.isdigit()]
    shape = tuple(dims)
    loops = 20
    threads = int(os.environ.get("THREADS", "4"))
    # decon models take a second input "in1" = params[2]
    extra = [("in1", np.array([0.5, 0.02], dtype=np.float32))] if "decon" in stem else []

    ngpu = ncnn.get_gpu_count()
    gpu_id = pick_real_gpu() if ngpu else -1
    gpu_name = ncnn.get_gpu_info(gpu_id).device_name() if ngpu else "-"
    print(f"model   : {stem}  shape={shape}")
    print(f"gpus    : {ngpu}  -> using device {gpu_id} ({gpu_name})")

    cpu = bench(stem, shape, False, loops, threads, extra)
    print(f"CPU         : {cpu:8.1f} ms/tile  ({threads} threads)")

    if ngpu > 0:
        for tag, fp16 in (("Vulkan fp32", False), ("Vulkan fp16", True)):
            try:
                g = bench(stem, shape, True, loops, threads, extra, fp16=fp16, gpu=gpu_id)
                print(f"{tag} : {g:8.1f} ms/tile   ({cpu/g:5.2f}x vs CPU)")
            except Exception as e:
                print(f"{tag} : FAILED — {e}")
    else:
        print("Vulkan      : skipped (no device)")


if __name__ == "__main__":
    main()
