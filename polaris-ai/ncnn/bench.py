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
import sys
import time
import numpy as np
import ncnn


def bench(stem, shape, use_vulkan, loops, threads, extra_inputs):
    net = ncnn.Net()
    net.opt.use_vulkan_compute = use_vulkan
    net.opt.num_threads = threads
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


def main():
    stem = sys.argv[1]
    dims = [int(a) for a in sys.argv[2:] if a.isdigit()]
    shape = tuple(dims)
    loops = 20
    threads = 4
    # decon models take a second input "in1" = params[2]
    extra = [("in1", np.array([0.5, 0.02], dtype=np.float32))] if "decon" in stem else []

    ngpu = ncnn.get_gpu_count()
    print(f"model   : {stem}  shape={shape}")
    print(f"gpus    : {ngpu}  (Vulkan {'available' if ngpu else 'NOT available'})")

    cpu = bench(stem, shape, False, loops, threads, extra)
    print(f"CPU     : {cpu:8.1f} ms/tile  ({threads} threads)")

    if ngpu > 0:
        try:
            gpu = bench(stem, shape, True, loops, threads, extra)
            print(f"Vulkan  : {gpu:8.1f} ms/tile")
            print(f"speedup : {cpu/gpu:6.2f}x  ({'GPU wins' if gpu < cpu else 'CPU wins'})")
        except Exception as e:
            print(f"Vulkan  : FAILED — {e}")
    else:
        print("Vulkan  : skipped (no device)")


if __name__ == "__main__":
    main()
