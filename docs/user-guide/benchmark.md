# Hardware benchmark

Polaris runs on a wide range of computers: Raspberry Pi 4 / 5, Orange Pi
5 Pro, x86 mini-PCs and PC sticks, and more. They differ a lot in how
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

Run: 2026-06-08. Two runs; the score varies with cooling/throttling. The
higher run had full multi-thread scaling (3.97×), the lower one was thermally
limited (2.98×) — keep the Pi 5 actively cooled for the best result.

| Metric | Best run | Throttled run |
|---|---|---|
| **Polaris score** | **197** | 172 |
| Stacking throughput | 2.2 fps · 36.8 Mpx/s | 2.01 fps · 33.7 Mpx/s |
| Stacking detect / align / resample / stats | 83.33 / 0.44 / 220.48 / 151.26 ms | 91.13 / 0.46 / 248.62 / 157.32 ms |
| Capture/video throughput | 1.42 fps · 23.8 Mpx/s | 1.35 fps · 22.7 Mpx/s |
| Debayer / JPEG / LZ4 | 103.15 / 584.09 / 17.19 ms (1861.4 MB/s) | 110.47 / 611.77 / 16.26 ms (1968.2 MB/s) |
| CPU single / multi-thread | 2868 / 11374 MFLOPS (3.97× scaling) | 2884 / 8581 MFLOPS (2.98× scaling) |
| Memory bandwidth | 6.9 GB/s | 8.1 GB/s |

(16.78 MP frames.)

### Orange Pi 5 Pro (8 cores, RK3588S)

Run: 2026-06-09 (two runs, 227 best / 222). Ubuntu 26.04 arm64.

| Metric | Value |
|---|---|
| **Polaris score** | **227** (best) / 222 |
| Stacking throughput | 2.66 fps · 44.6 Mpx/s (16.78 MP frames) |
| Stacking detect / align / resample / stats | 100.41 / 0.56 / 115.76 / 159.71 ms |
| Capture/video throughput | 1.56 fps · 26.2 Mpx/s |
| Debayer / JPEG / LZ4 | 70.94 / 555.39 / 13.67 ms (LZ4 2341.4 MB/s) |
| CPU single / multi-thread | 2778 / 12940 MFLOPS (4.66× scaling) |
| Memory bandwidth | 23.7 GB/s |

Slightly ahead of the Raspberry Pi 5 — the RK3588S big.LITTLE cores give it
strong multi-thread scaling (4.66× across its 8 cores) and much higher memory
bandwidth (23.7 vs ~7 GB/s), which is why its stacking throughput leads.

### x86 desktop — Core i9-13900KF (32 threads)

ASUSTeK ProArt B760-CREATOR D4, Core i9-13900KF, 64 GB DDR4-3200, 2 TB NVMe SSD.
Run: 2026-06-10 (Release build — see note).

| Metric | Value |
|---|---|
| **Polaris score** | **662** |
| Stacking throughput | 5.43 fps · 91.1 Mpx/s (16.78 MP frames) |
| Stacking detect / align / resample / stats | 42.46 / 0.93 / 50.36 / 90.46 ms |
| Capture/video throughput | 3.37 fps · 56.6 Mpx/s |
| Debayer / JPEG / LZ4 | 37.09 / 249.89 / 9.49 ms (LZ4 3372.6 MB/s) |
| CPU single / multi-thread | 4697 / 73240 MFLOPS (15.59× scaling) |
| Memory bandwidth | 46.7 GB/s |

> **Build matters.** This 662 is a **Release** build. An earlier run on the
> same machine scored 348 in a **Debug** build — roughly half. The SBC numbers
> above all come from the Release `.deb`, so compare against this 662, not the
> old Debug figure. Always benchmark a Release build for cross-board comparison.

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
