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
connected camera** before running (and set an exposure/gain). Polaris will
time a few real exposures and report the mean capture time, achievable
frame rate and frame size. These numbers are **camera-dependent** and are
shown separately — do not use them to compare different computers unless
you move the same camera between them.

## Comparing your machines

To compare boards, run the benchmark on each one and compare the Polaris
scores and the Mpx/s figures.

- **History** — each device keeps its recent runs in the card.
- **Export JSON** — downloads this device's full run history as a JSON
  file. Export from each machine and compare side by side, or keep them
  for your records.
- **Clear history** — removes the saved runs on the current device.

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
