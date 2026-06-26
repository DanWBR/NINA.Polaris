# Hardware benchmark

Polaris runs on a wide range of computers: Raspberry Pi 4 / 5, Orange Pi
5 Pro, Radxa Dragon Q6A, x86 mini-PCs and PC sticks, and more. They differ a lot in how
fast they can stack frames and encode the live video stream. The
**Hardware Benchmark** (Settings → Hardware benchmark) measures your
machine so you can compare boards and pick the right one for your rig.

## What it measures

The benchmark runs the *real* Polaris image-processing code over a fixed,
computer-generated star field, so every machine runs the identical
workload and the scores are directly comparable.

- **Stacking pipeline** — star detection, alignment (RANSAC), resampling,
  accumulation and SNR. This is the per-frame cost of live stacking.
- **Capture / video encode** — debayer, auto-stretch, JPEG downscale and
  LZ4 compression. This is the per-frame cost of the live video/preview
  stream.
- **CPU / memory** — a raw single-thread and multi-thread floating-point
  test plus a memory-bandwidth test, independent of the astro code. The
  multi-thread result shows how much your machine's extra cores actually
  help.

Each section reports throughput in frames per second and **megapixels per
second (Mpx/s)**, plus a single headline **Polaris score** (higher is
better; a Raspberry Pi 5 lands around 100).

## Running it

1. Open **Settings → Hardware benchmark**.
2. Click **Run benchmark**. It takes roughly 15–30 seconds depending on
   the machine, and shows a progress bar.
3. When it finishes, the results table and the Polaris score appear, and
   the run is saved to this device's history.

You cannot start a benchmark while live stacking or a video stream is
running — stop those first so they don't skew the numbers.

## Why synthetic, not your camera?

Real-camera capture speed is mostly decided by the camera and its USB
link, not by the computer. If you benchmarked with the camera, a slow
camera would make a fast computer look slow, and the numbers wouldn't be
comparable between machines. The synthetic test removes the camera from
the equation and measures only the computer.

### Optional: measure the connected camera

If you do want to see real capture timing, tick **Also measure the
connected camera** before running (and set an exposure/gain). Polaris
then runs two camera measurements:

- **Capture** — times a few real still exposures and reports the mean
  capture time, achievable frame rate and frame size.
- **Video stream** — starts the live video stream (the camera's native
  video mode when supported, otherwise the server capture loop), runs it
  for a few seconds, and reports the achieved **capture FPS**, the
  **transmitted FPS** (the downscaled JPEG actually sent to the browser),
  the frame size and the raw on-wire MB/s. This is the number that tells
  you how smooth the live view will be on a given board.

These numbers are **camera-dependent** and are shown separately — do not
use them to compare different computers unless you move the same camera
between them. The video-stream test needs the live stream and the regular
video tab to be stopped first.

## Comparing your machines

To compare boards, run the benchmark on each one and compare the Polaris
scores and the Mpx/s figures.

- **History** — each device keeps its recent runs in the card.
- **Export JSON** — downloads this device's full run history as a JSON
  file. Export from each machine and compare side by side, or keep them
  for your records.
- **Clear history** — removes the saved runs on the current device.

## Reference results

Scores collected on real hardware, for picking a board. Higher Polaris
score is better. (More boards added as they arrive: x86 PC stick.)

### Raspberry Pi 4 Model B (4 cores)

Run: 2026-06-08 (2 consecutive runs, both scored 110).

| Metric | Value |
|---|---|
| **Polaris score** | **110** |
| Stacking throughput | 1.32 fps · 22.2 Mpx/s (16.78 MP frames) |
| Stacking detect / align / resample / stats | 170.39 / 0.97 / 277.37 / 306.44 ms |
| Capture/video throughput | 0.68 fps · 11.5 Mpx/s |
| Debayer / JPEG / LZ4 | 199.61 / 1233.65 / 29.29 ms (LZ4 1092.4 MB/s) |
| CPU single / multi-thread | 1797 / 6804 MFLOPS (3.79× scaling) |
| Memory bandwidth | 4.7 GB/s |

### Raspberry Pi 5 Model B (4 cores)

Run: best 2026-06-10, throttled 2026-06-08. The score varies with
cooling/throttling. The higher run had near-full multi-thread scaling, the
lower one was thermally limited (2.98×) — keep the Pi 5 actively cooled for
the best result.

| Metric | Best run | Throttled run |
|---|---|---|
| **Polaris score** | **211** | 172 |
| Stacking throughput | 2.38 fps · 39.9 Mpx/s | 2.01 fps · 33.7 Mpx/s |
| Stacking detect / align / resample / stats | 96.5 / 0.6 / 166.27 / 157.59 ms | 91.13 / 0.46 / 248.62 / 157.32 ms |
| Capture/video throughput | 1.63 fps · 27.3 Mpx/s | 1.35 fps · 22.7 Mpx/s |
| Debayer / JPEG / LZ4 | 84.63 / 511.52 / 17.69 ms (1808.5 MB/s) | 110.47 / 611.77 / 16.26 ms (1968.2 MB/s) |
| CPU single / multi-thread | 2888 / 11248 MFLOPS (3.89× scaling) | 2884 / 8581 MFLOPS (2.98× scaling) |
| Memory bandwidth | 7.7 GB/s | 8.1 GB/s |

(16.78 MP frames.)

### Orange Pi 5 Pro (8 cores, RK3588S)

Run: 2026-06-12 (best 242, with the OpenCL GPU backend enabled). Armbian
(Ubuntu) arm64, Mali-G610 via libmali + OpenCL ICD. Earlier CPU-only runs
(2026-06-09) topped out at 227.

| Metric | Value |
|---|---|
| **Polaris score** | **242** (GPU on) / 227 (CPU only) |
| Stacking throughput | 2.75 fps · 46.2 Mpx/s (16.78 MP frames) |
| Stacking detect / align / resample / stats | 92.43 / 0.48 / 118 / 152.09 ms |
| Capture/video throughput | 1.84 fps · 30.9 Mpx/s |
| Debayer / JPEG / LZ4 | 58.82 / 471.14 / 13.55 ms (LZ4 2361.7 MB/s) |
| CPU single / multi-thread | 2783 / 12879 MFLOPS (4.63× scaling) |
| Memory bandwidth | 23.1 GB/s |
| GPU vs CPU (Mali-G610 r0p0) | warp 3.41× · debayer 1.19× · blur 12.95× · **overall 3.74×** (geo-mean) |

Slightly ahead of the Raspberry Pi 5 — the RK3588S big.LITTLE cores give it
strong multi-thread scaling (4.6× across its 8 cores) and much higher memory
bandwidth (23 vs ~7 GB/s), which is why its stacking throughput leads. With the
Mali-G610 OpenCL backend on, the offloaded kernels (alignment warp, separable
blur, debayer) run ~3.7× faster than the CPU path on average (geometric mean;
every op wins here because the shared memory makes offload free), lifting the
overall score from ~227 to ~242 and freeing CPU headroom during a live-stack
session.

> **This is the board's real ceiling — power/governor don't change it.**
> Measured at full clocks (4× Cortex-A76 @ 2.35 GHz + 4× Cortex-A55 @ 1.8 GHz,
> their `scaling_max_freq`) and ~47°C, so no thermal throttle. Forcing the
> `performance` governor made no difference (cores already hit max under load),
> and the score is the same (~1.2 A / ~6 W draw) whether powered from a PC
> USB-C port or a dedicated 5V/5A PSU — a CPU/memory benchmark with no
> peripherals never approaches the 5A peak rating. The ~4.7× (not 8×)
> multi-thread scaling is architectural: the 4 little A55 cores are far weaker
> than the 4 big A76s, so ~227 is the SoC's genuine CPU-only limit here, not a
> power/cooling/config bottleneck — the Mali-G610 OpenCL backend is what lifts
> the overall score past it (242). Keep the 5V/5A PSU for field stability
> (NVMe + USB + camera + dew heater peaks), not for compute.

### Radxa Dragon Q6A (8 cores, Qualcomm QCS6490)

Run: 2026-06-26 (2 consecutive runs, 289 / 287; CPU only). Ubuntu 24.04
(noble) arm64. Kryo cores (Cortex-A78 + A55) with an Adreno 643 GPU (OpenCL
backend not yet enabled in this run).

| Metric | Value |
|---|---|
| **Polaris score** | **289** (CPU only) |
| Stacking throughput | 3.5 fps · 58.8 Mpx/s (16.78 MP frames) |
| Stacking detect / align / resample / stats | 71.78 / 0.53 / 78.36 / 134.83 ms |
| Capture/video throughput | 2.32 fps · 39 Mpx/s |
| Debayer / JPEG / LZ4 | 47.82 / 368.88 / 13.75 ms (LZ4 2327.2 MB/s) |
| CPU single / multi-thread | 3296 / 13699 MFLOPS (4.16× scaling) |
| Memory bandwidth | 13.2 GB/s |

The fastest SBC here by a clear margin — ~2.6× the Raspberry Pi 4 and ~19%
ahead of the Orange Pi 5 Pro's CPU-only result (and ~19% past its GPU-on 242).
It leads on raw per-core throughput: the strongest single-thread score of the
SBCs (3296 MFLOPS) and the lowest stacking detect/resample times, which is what
drives the 58.8 Mpx/s stacking throughput. Memory bandwidth (13.2 GB/s) sits
between the RK3588S boards and the Pi 5. This is a **CPU-only** result — the
Adreno 643 supports OpenCL, so enabling the GPU backend should lift it further
(unified memory makes offload cheap, as on the Mali boards); GPU-vs-CPU numbers
to be added once measured. On this SoC the **RKNN/NPU path does not apply**
(that's Rockchip-only); GPU acceleration here is via OpenCL on the Adreno.

### x86 desktop — Core i9-13900KF (32 threads)

ASUSTeK ProArt B760-CREATOR D4, Core i9-13900KF, 64 GB DDR4-3200, 2 TB NVMe SSD,
NVIDIA GeForce RTX 5070. Run: 2026-06-12 (Release build — see note).

| Metric | Value |
|---|---|
| **Polaris score** | **936** |
| Stacking throughput | 5.93 fps · 99.4 Mpx/s (16.78 MP frames) |
| Stacking detect / align / resample / stats | 24.2 / 0.61 / 75.23 / 68.67 ms |
| Capture/video throughput | 5.31 fps · 89 Mpx/s |
| Debayer / JPEG / LZ4 | 20.51 / 160.91 / 7.06 ms (LZ4 4535.4 MB/s) |
| CPU single / multi-thread | 6054 / 120336 MFLOPS (19.88× scaling) |
| Memory bandwidth | 41 GB/s |
| GPU vs CPU (RTX 5070, OpenCL) | warp 0.47× · debayer 0.40× · blur 16.17× · overall 1.45× (geo-mean) |

> **Discrete GPU caveat.** Unlike the unified-memory SBCs (Mali/Adreno), a
> discrete GPU sits behind PCIe, so the per-op host↔device copy dominates the
> small kernels: warp (0.47×) and debayer (0.40×) are actually *slower* on the
> RTX 5070 than on this fast CPU; only the heavier blur wins (16.17×). The
> "overall" figure is the **geometric mean** of the three (1.45×), so it is no
> longer inflated by the single blur win the way a plain average was (5.68×).
>
> **You don't need to do anything about this.** The OpenCL backend now detects
> the device's memory model at startup: on a unified-memory SBC it offloads
> every op (full zero-copy win); on a discrete GPU it runs a one-time micro-probe
> and offloads **only the ops that actually beat the CPU** — here, just the blur,
> while warp and debayer stay on the CPU. So leaving the GPU toggle on costs
> nothing on a discrete-GPU desktop: the light ops never regress and the editor's
> blur-heavy work still gets the 16× speedup. (`GET /api/system/gpu` reports
> `unifiedMemory` and the chosen `offloadedOps`.)

> **Build matters.** Use a **Release** build. Earlier runs on this machine in
> **Debug** scored ~348 — roughly half. The SBC numbers above all come from the
> Release `.deb`, so compare against the Release figure. Always benchmark a
> Release build for cross-board comparison.

### x86 PC stick

_Pending hardware._

## Reading the numbers

- **Stacking Mpx/s** — higher means live stacking keeps up with shorter
  sub-exposures and bigger sensors.
- **Encode Mpx/s / LZ4 MB/s** — higher means a smoother, higher-frame-rate
  live video stream.
- **CPU multi-thread + scaling** — a high scaling factor (close to the
  core count) means the board uses its cores well; a low one means it is
  limited by single-core speed.
- **Memory bandwidth** — matters most for large sensors, where moving
  pixels in and out of memory dominates.
