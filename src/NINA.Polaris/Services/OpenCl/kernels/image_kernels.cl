// N.I.N.A. Polaris - OpenCL image kernels
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
// AGPL-3.0-or-later. See repository LICENSE.txt / NOTICE.
//
// Classic image-math kernels offloaded from the CPU (NINA.Image.Portable
// helpers) to the SBC GPU. Each kernel mirrors the C# reference exactly so the
// GPU output matches the CPU path within tolerance (the CPU stays canonical and
// is the unit-test reference). 16-bit data is carried as `ushort` (cast to int
// for math); LUT output is `uchar`.

// --- Separable Gaussian blur ------------------------------------------------
// Two passes (horizontal then vertical) over a float scratch buffer, clamped
// edges, matching GaussianBlur.Apply. The kernel is uploaded as a float[] of
// length (2*radius+1), normalised to sum 1.

__kernel void blur_h(__global const ushort* src, __global float* dst,
                     __global const float* kern, int radius, int width, int height) {
    int x = get_global_id(0);
    int y = get_global_id(1);
    if (x >= width || y >= height) return;
    int rowOff = y * width;
    float acc = 0.0f;
    for (int k = -radius; k <= radius; ++k) {
        int xs = clamp(x + k, 0, width - 1);
        acc += (float)src[rowOff + xs] * kern[k + radius];
    }
    dst[rowOff + x] = acc;
}

__kernel void blur_v(__global const float* src, __global ushort* dst,
                     __global const float* kern, int radius, int width, int height) {
    int x = get_global_id(0);
    int y = get_global_id(1);
    if (x >= width || y >= height) return;
    float acc = 0.0f;
    for (int k = -radius; k <= radius; ++k) {
        int ys = clamp(y + k, 0, height - 1);
        acc += src[ys * width + x] * kern[k + radius];
    }
    float v = round(acc);
    v = clamp(v, 0.0f, 65535.0f);
    dst[y * width + x] = (ushort)v;
}

// --- Affine warp + bilinear resample ----------------------------------------
// Inverse map output->source: src = Minv * (out - T). m = [m00,m01,m10,m11,tx,ty]
// passed as the FORWARD transform; we invert on the host and pass the inverse
// as mi = [i00,i01,i10,i11,itx,ity]. Bilinear with zero outside bounds, matching
// ImageResampler.ApplyTransform.

__kernel void warp_affine(__global const ushort* src, __global ushort* dst,
                          int width, int height,
                          float i00, float i01, float i10, float i11,
                          float itx, float ity) {
    int x = get_global_id(0);
    int y = get_global_id(1);
    if (x >= width || y >= height) return;
    float sx = i00 * x + i01 * y + itx;
    float sy = i10 * x + i11 * y + ity;
    int x0 = (int)floor(sx);
    int y0 = (int)floor(sy);
    float fx = sx - x0;
    float fy = sy - y0;
    float val = 0.0f;
    if (x0 >= 0 && y0 >= 0 && x0 + 1 < width && y0 + 1 < height) {
        float p00 = src[y0 * width + x0];
        float p10 = src[y0 * width + x0 + 1];
        float p01 = src[(y0 + 1) * width + x0];
        float p11 = src[(y0 + 1) * width + x0 + 1];
        float top = p00 + (p10 - p00) * fx;
        float bot = p01 + (p11 - p01) * fx;
        val = top + (bot - top) * fy;
    }
    float v = clamp(round(val), 0.0f, 65535.0f);
    dst[y * width + x] = (ushort)v;
}

// --- Per-pixel 16-bit -> 8-bit LUT apply (stretch hot path) -----------------
__kernel void apply_lut8(__global const ushort* src, __global uchar* dst,
                         __global const uchar* lut, int n) {
    int i = get_global_id(0);
    if (i >= n) return;
    dst[i] = lut[src[i]];
}

// --- 8-bit box blur (editor clarity/texture/sharpen) ------------------------
// One H or V pass of an edge-clamped box blur over a uchar plane. The editor
// runs 3 passes of (H then V) to approximate a Gaussian. iarr = 1/(2r+1).
// rint() = round-to-nearest-even, matching C# Math.Round; float (no fp64) for
// Mali/Adreno portability. Edge handling = clamped index (== the CPU fv/lv
// border extension).

__kernel void box_blur_h(__global const uchar* src, __global uchar* dst,
                         int width, int height, int r) {
    int x = get_global_id(0);
    int y = get_global_id(1);
    if (x >= width || y >= height) return;
    int row = y * width;
    int sum = 0;
    for (int k = -r; k <= r; ++k) {
        int xs = clamp(x + k, 0, width - 1);
        sum += src[row + xs];
    }
    float v = rint((float)sum / (float)(2 * r + 1));
    dst[row + x] = (uchar)clamp(v, 0.0f, 255.0f);
}

__kernel void box_blur_v(__global const uchar* src, __global uchar* dst,
                         int width, int height, int r) {
    int x = get_global_id(0);
    int y = get_global_id(1);
    if (x >= width || y >= height) return;
    int sum = 0;
    for (int k = -r; k <= r; ++k) {
        int ys = clamp(y + k, 0, height - 1);
        sum += src[ys * width + x];
    }
    float v = rint((float)sum / (float)(2 * r + 1));
    dst[y * width + x] = (uchar)clamp(v, 0.0f, 255.0f);
}

// --- Running-mean accumulate (live stacking) --------------------------------
// accum[i] += frame[i]; count[i]++  -- but ONLY for frame[i] > 0. Zero pixels
// are warp-edge "no data" and must not bias the running mean (matches the CPU
// LiveStackingService loop: `if (alignedData[i] > 0)`).
__kernel void accumulate(__global const ushort* frame, __global float* accum,
                         __global int* count, int n) {
    int i = get_global_id(0);
    if (i >= n) return;
    ushort v = frame[i];
    if (v > 0) {
        accum[i] += (float)v;
        count[i] += 1;
    }
}
