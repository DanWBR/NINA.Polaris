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

// --- Affine warp, image2d source (Adreno texture-cache path) ----------------
// Identical math to warp_affine, but the source is sampled through a read-only
// image2d_t (CL_R / CL_UNSIGNED_INT16) with NEAREST filtering, so reads are
// EXACT integers (no hardware interpolation -> bit-identical to the buffer
// kernel) while benefiting from the GPU's 2D texture cache for the scattered
// gather. Bilinear is still computed in float32 on the host side here. The
// explicit in-bounds test preserves the "zero outside source" behaviour
// (CLK_ADDRESS_CLAMP_TO_EDGE would clamp instead, so we gate the reads).
__constant sampler_t WARP_SMP =
    CLK_NORMALIZED_COORDS_FALSE | CLK_ADDRESS_CLAMP_TO_EDGE | CLK_FILTER_NEAREST;

__kernel void warp_affine_img(__read_only image2d_t src, __global ushort* dst,
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
        float p00 = (float)read_imageui(src, WARP_SMP, (int2)(x0,     y0)).x;
        float p10 = (float)read_imageui(src, WARP_SMP, (int2)(x0 + 1, y0)).x;
        float p01 = (float)read_imageui(src, WARP_SMP, (int2)(x0,     y0 + 1)).x;
        float p11 = (float)read_imageui(src, WARP_SMP, (int2)(x0 + 1, y0 + 1)).x;
        float top = p00 + (p10 - p00) * fx;
        float bot = p01 + (p11 - p01) * fx;
        val = top + (bot - top) * fy;
    }
    float v = clamp(round(val), 0.0f, 65535.0f);
    dst[y * width + x] = (ushort)v;
}

// --- Bilinear debayer (live-stack OSC preprocessing) ------------------------
// Mirrors BayerDebayer.Bilinear exactly: integer-truncating neighbour averages
// (sum / count, not rounded), edge handling = average only the in-bounds
// neighbours. The 2x2 colour block (0=R,1=G,2=B) is passed as b0..b3 =
// block[(y&1)*2 + (x&1)].

static int dbg_avgN4(__global const ushort* c, int x, int y, int w, int h) {
    int sum = 0, n = 0;
    if (y > 0)       { sum += c[(y - 1) * w + x]; n++; }
    if (y + 1 < h)   { sum += c[(y + 1) * w + x]; n++; }
    if (x > 0)       { sum += c[y * w + (x - 1)]; n++; }
    if (x + 1 < w)   { sum += c[y * w + (x + 1)]; n++; }
    return n == 0 ? 0 : sum / n;
}
static int dbg_avgDiag4(__global const ushort* c, int x, int y, int w, int h) {
    int sum = 0, n = 0;
    if (x > 0     && y > 0)     { sum += c[(y - 1) * w + (x - 1)]; n++; }
    if (x + 1 < w && y > 0)     { sum += c[(y - 1) * w + (x + 1)]; n++; }
    if (x > 0     && y + 1 < h) { sum += c[(y + 1) * w + (x - 1)]; n++; }
    if (x + 1 < w && y + 1 < h) { sum += c[(y + 1) * w + (x + 1)]; n++; }
    return n == 0 ? 0 : sum / n;
}
static int dbg_avgH(__global const ushort* c, int x, int y, int w) {
    int sum = 0, n = 0;
    if (x > 0)     { sum += c[y * w + (x - 1)]; n++; }
    if (x + 1 < w) { sum += c[y * w + (x + 1)]; n++; }
    return n == 0 ? 0 : sum / n;
}
static int dbg_avgV(__global const ushort* c, int x, int y, int w, int h) {
    int sum = 0, n = 0;
    if (y > 0)     { sum += c[(y - 1) * w + x]; n++; }
    if (y + 1 < h) { sum += c[(y + 1) * w + x]; n++; }
    return n == 0 ? 0 : sum / n;
}

__kernel void debayer_bilinear(__global const ushort* cfa,
                               __global ushort* r, __global ushort* g, __global ushort* b,
                               int width, int height, int b0, int b1, int b2, int b3) {
    int x = get_global_id(0);
    int y = get_global_id(1);
    if (x >= width || y >= height) return;
    int idx = y * width + x;
    int rowBase = (y & 1) << 1;
    int xp = x & 1;
    int block[4] = { b0, b1, b2, b3 };
    int colour = block[rowBase + xp];
    int raw = cfa[idx];
    if (colour == 0) {            // R site
        r[idx] = (ushort)raw;
        g[idx] = (ushort)dbg_avgN4(cfa, x, y, width, height);
        b[idx] = (ushort)dbg_avgDiag4(cfa, x, y, width, height);
    } else if (colour == 2) {     // B site
        b[idx] = (ushort)raw;
        g[idx] = (ushort)dbg_avgN4(cfa, x, y, width, height);
        r[idx] = (ushort)dbg_avgDiag4(cfa, x, y, width, height);
    } else {                      // G site
        g[idx] = (ushort)raw;
        if (block[rowBase + (xp ^ 1)] == 0) { // reds on this row
            r[idx] = (ushort)dbg_avgH(cfa, x, y, width);
            b[idx] = (ushort)dbg_avgV(cfa, x, y, width, height);
        } else {
            r[idx] = (ushort)dbg_avgV(cfa, x, y, width, height);
            b[idx] = (ushort)dbg_avgH(cfa, x, y, width);
        }
    }
}

// --- Bilinear debayer, image2d source (Adreno texture-cache path) -----------
// Bit-identical to debayer_bilinear (same integer-truncating in-bounds
// neighbour averages), but the CFA is sampled through a read-only image2d_t so
// the heavy neighbour gather hits the 2D texture cache. NEAREST sampler =>
// reads are exact integers. Same explicit in-bounds tests as the buffer helpers.
__constant sampler_t CFA_SMP =
    CLK_NORMALIZED_COORDS_FALSE | CLK_ADDRESS_CLAMP_TO_EDGE | CLK_FILTER_NEAREST;

static int dbgi_px(__read_only image2d_t c, int x, int y) {
    return (int)read_imageui(c, CFA_SMP, (int2)(x, y)).x;
}
static int dbgi_avgN4(__read_only image2d_t c, int x, int y, int w, int h) {
    int sum = 0, n = 0;
    if (y > 0)     { sum += dbgi_px(c, x, y - 1); n++; }
    if (y + 1 < h) { sum += dbgi_px(c, x, y + 1); n++; }
    if (x > 0)     { sum += dbgi_px(c, x - 1, y); n++; }
    if (x + 1 < w) { sum += dbgi_px(c, x + 1, y); n++; }
    return n == 0 ? 0 : sum / n;
}
static int dbgi_avgDiag4(__read_only image2d_t c, int x, int y, int w, int h) {
    int sum = 0, n = 0;
    if (x > 0     && y > 0)     { sum += dbgi_px(c, x - 1, y - 1); n++; }
    if (x + 1 < w && y > 0)     { sum += dbgi_px(c, x + 1, y - 1); n++; }
    if (x > 0     && y + 1 < h) { sum += dbgi_px(c, x - 1, y + 1); n++; }
    if (x + 1 < w && y + 1 < h) { sum += dbgi_px(c, x + 1, y + 1); n++; }
    return n == 0 ? 0 : sum / n;
}
static int dbgi_avgH(__read_only image2d_t c, int x, int y, int w) {
    int sum = 0, n = 0;
    if (x > 0)     { sum += dbgi_px(c, x - 1, y); n++; }
    if (x + 1 < w) { sum += dbgi_px(c, x + 1, y); n++; }
    return n == 0 ? 0 : sum / n;
}
static int dbgi_avgV(__read_only image2d_t c, int x, int y, int w, int h) {
    int sum = 0, n = 0;
    if (y > 0)     { sum += dbgi_px(c, x, y - 1); n++; }
    if (y + 1 < h) { sum += dbgi_px(c, x, y + 1); n++; }
    return n == 0 ? 0 : sum / n;
}

__kernel void debayer_bilinear_img(__read_only image2d_t cfa,
                                   __global ushort* r, __global ushort* g, __global ushort* b,
                                   int width, int height, int b0, int b1, int b2, int b3) {
    int x = get_global_id(0);
    int y = get_global_id(1);
    if (x >= width || y >= height) return;
    int idx = y * width + x;
    int rowBase = (y & 1) << 1;
    int xp = x & 1;
    int block[4] = { b0, b1, b2, b3 };
    int colour = block[rowBase + xp];
    int raw = dbgi_px(cfa, x, y);
    if (colour == 0) {            // R site
        r[idx] = (ushort)raw;
        g[idx] = (ushort)dbgi_avgN4(cfa, x, y, width, height);
        b[idx] = (ushort)dbgi_avgDiag4(cfa, x, y, width, height);
    } else if (colour == 2) {     // B site
        b[idx] = (ushort)raw;
        g[idx] = (ushort)dbgi_avgN4(cfa, x, y, width, height);
        r[idx] = (ushort)dbgi_avgDiag4(cfa, x, y, width, height);
    } else {                      // G site
        g[idx] = (ushort)raw;
        if (block[rowBase + (xp ^ 1)] == 0) { // reds on this row
            r[idx] = (ushort)dbgi_avgH(cfa, x, y, width);
            b[idx] = (ushort)dbgi_avgV(cfa, x, y, width, height);
        } else {
            r[idx] = (ushort)dbgi_avgV(cfa, x, y, width, height);
            b[idx] = (ushort)dbgi_avgH(cfa, x, y, width);
        }
    }
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
