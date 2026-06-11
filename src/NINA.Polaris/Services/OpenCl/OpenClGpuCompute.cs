// N.I.N.A. Polaris
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT
// ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or
// FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License
// for more details. You should have received a copy of the license along with
// this program. If not, see <https://www.gnu.org/licenses/>.

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NINA.Core.Enum;
using NINA.Image.Gpu;
using NINA.Image.ImageAnalysis;
using Silk.NET.OpenCL;

namespace NINA.Polaris.Services.OpenCl;

/// <summary>
/// <see cref="IGpuCompute"/> backed by the SBC GPU through OpenCL. Lazily builds
/// one <see cref="OpenClContext"/> on first use; if that fails (no GPU, no
/// driver, build error) the backend stays "off" and every op returns
/// <c>false</c> so callers run the CPU helper. Each op is wrapped in try/catch
/// and also returns <c>false</c> on any runtime error, so the GPU path can never
/// break correctness; the worst case is a transparent CPU fallback.
///
/// Kernels not yet ported to OpenCL (currently the bilinear debayer) simply
/// return <c>false</c>, so they keep running on the CPU until a kernel lands.
/// </summary>
public sealed unsafe class OpenClGpuCompute : IGpuCompute, IDisposable {
    private readonly ILogger<OpenClGpuCompute> _log;
    private readonly object _initGate = new();
    private OpenClContext? _ctx;
    private bool _initTried;
    private bool _initFailed;

    public OpenClGpuCompute(ILogger<OpenClGpuCompute> log) {
        _log = log;
    }

    public string BackendName => _ctx != null ? $"OpenCL: {_ctx.DeviceName}" : "OpenCL (uninitialised)";
    public bool IsHardware => true;

    private OpenClContext? Context() {
        if (_initFailed) return null;
        if (_ctx != null) return _ctx;
        lock (_initGate) {
            if (_ctx != null) return _ctx;
            if (_initTried) return _ctx;
            _initTried = true;
            try {
                if (!OpenClRuntime.IsAvailable) { _initFailed = true; return null; }
                var src = LoadKernelSource();
                _ctx = new OpenClContext(src);
                _log.LogInformation("OpenCL GPU backend ready: {Device}", _ctx.DeviceName);
                return _ctx;
            } catch (Exception ex) {
                _initFailed = true;
                _log.LogInformation("OpenCL GPU backend unavailable, using CPU: {Msg}", ex.Message);
                return null;
            }
        }
    }

    private static string LoadKernelSource() {
        var dir = Path.GetDirectoryName(typeof(OpenClGpuCompute).Assembly.Location) ?? AppContext.BaseDirectory;
        var path = Path.Combine(dir, "Services", "OpenCl", "kernels", "image_kernels.cl");
        if (!File.Exists(path)) {
            // Fallback to a flat copy next to the binary.
            path = Path.Combine(dir, "image_kernels.cl");
        }
        return File.ReadAllText(path);
    }

    // ─── kernels ──────────────────────────────────────────────────────────

    public bool TrySeparableBlur(ushort[] data, int width, int height, int radius,
                                 double sigma, out ushort[] result) {
        result = Array.Empty<ushort>();
        if (radius < 1) return false;
        var ctx = Context();
        if (ctx == null) return false;
        try {
            int n = width * height;
            var kernel = BuildGaussianKernel(radius, sigma <= 0 ? radius / 2.0 : sigma);
            var cl = ctx.Cl;
            lock (ctx.Gate) {
                nint bSrc = CreateFrom(ctx, MemFlags.ReadOnly | MemFlags.CopyHostPtr, data);
                nint bTmp = CreateEmpty(ctx, MemFlags.ReadWrite, (nuint)(n * sizeof(float)));
                nint bDst = CreateEmpty(ctx, MemFlags.WriteOnly, (nuint)(n * sizeof(ushort)));
                nint bKern = CreateFrom(ctx, MemFlags.ReadOnly | MemFlags.CopyHostPtr, kernel);
                try {
                    var kh = ctx.GetKernel("blur_h");
                    SetMem(cl, kh, 0, bSrc); SetMem(cl, kh, 1, bTmp); SetMem(cl, kh, 2, bKern);
                    SetVal(cl, kh, 3, radius); SetVal(cl, kh, 4, width); SetVal(cl, kh, 5, height);
                    Run2D(ctx, kh, width, height);

                    var kv = ctx.GetKernel("blur_v");
                    SetMem(cl, kv, 0, bTmp); SetMem(cl, kv, 1, bDst); SetMem(cl, kv, 2, bKern);
                    SetVal(cl, kv, 3, radius); SetVal(cl, kv, 4, width); SetVal(cl, kv, 5, height);
                    Run2D(ctx, kv, width, height);

                    var outp = new ushort[n];
                    ReadInto(ctx, bDst, outp);
                    result = outp;
                    return true;
                } finally {
                    cl.ReleaseMemObject(bSrc); cl.ReleaseMemObject(bTmp);
                    cl.ReleaseMemObject(bDst); cl.ReleaseMemObject(bKern);
                }
            }
        } catch (Exception ex) { _log.LogDebug("GPU blur fell back: {Msg}", ex.Message); return false; }
    }

    public bool TryWarpAffine(ushort[] source, int width, int height,
                              AffineTransform transform, out ushort[] result) {
        result = Array.Empty<ushort>();
        var ctx = Context();
        if (ctx == null) return false;
        try {
            // Invert the forward transform so the kernel can map output->source.
            double det = transform.M00 * transform.M11 - transform.M01 * transform.M10;
            if (Math.Abs(det) < 1e-12) return false;
            double inv = 1.0 / det;
            float i00 = (float)(transform.M11 * inv);
            float i01 = (float)(-transform.M01 * inv);
            float i10 = (float)(-transform.M10 * inv);
            float i11 = (float)(transform.M00 * inv);
            float itx = (float)(-(i00 * transform.Tx + i01 * transform.Ty));
            float ity = (float)(-(i10 * transform.Tx + i11 * transform.Ty));
            int n = width * height;
            var cl = ctx.Cl;
            lock (ctx.Gate) {
                nint bSrc = CreateFrom(ctx, MemFlags.ReadOnly | MemFlags.CopyHostPtr, source);
                nint bDst = CreateEmpty(ctx, MemFlags.WriteOnly, (nuint)(n * sizeof(ushort)));
                try {
                    var k = ctx.GetKernel("warp_affine");
                    SetMem(cl, k, 0, bSrc); SetMem(cl, k, 1, bDst);
                    SetVal(cl, k, 2, width); SetVal(cl, k, 3, height);
                    SetVal(cl, k, 4, i00); SetVal(cl, k, 5, i01); SetVal(cl, k, 6, i10);
                    SetVal(cl, k, 7, i11); SetVal(cl, k, 8, itx); SetVal(cl, k, 9, ity);
                    Run2D(ctx, k, width, height);
                    var outp = new ushort[n];
                    ReadInto(ctx, bDst, outp);
                    result = outp;
                    return true;
                } finally {
                    cl.ReleaseMemObject(bSrc); cl.ReleaseMemObject(bDst);
                }
            }
        } catch (Exception ex) { _log.LogDebug("GPU warp fell back: {Msg}", ex.Message); return false; }
    }

    public bool TryDebayerBilinear(ushort[] cfa, int width, int height,
                                   BayerPatternEnum pattern, out BayerDebayer.Channels result) {
        // Not yet ported to OpenCL; CPU handles debayer.
        result = null!;
        return false;
    }

    public bool TryApplyLut8(ushort[] data, byte[] lut, out byte[] result) {
        result = Array.Empty<byte>();
        var ctx = Context();
        if (ctx == null) return false;
        try {
            int n = data.Length;
            var cl = ctx.Cl;
            lock (ctx.Gate) {
                nint bSrc = CreateFrom(ctx, MemFlags.ReadOnly | MemFlags.CopyHostPtr, data);
                nint bLut = CreateFrom(ctx, MemFlags.ReadOnly | MemFlags.CopyHostPtr, lut);
                nint bDst = CreateEmpty(ctx, MemFlags.WriteOnly, (nuint)n);
                try {
                    var k = ctx.GetKernel("apply_lut8");
                    SetMem(cl, k, 0, bSrc); SetMem(cl, k, 1, bDst); SetMem(cl, k, 2, bLut);
                    SetVal(cl, k, 3, n);
                    Run1D(ctx, k, n);
                    var outp = new byte[n];
                    ReadInto(ctx, bDst, outp);
                    result = outp;
                    return true;
                } finally {
                    cl.ReleaseMemObject(bSrc); cl.ReleaseMemObject(bLut); cl.ReleaseMemObject(bDst);
                }
            }
        } catch (Exception ex) { _log.LogDebug("GPU lut fell back: {Msg}", ex.Message); return false; }
    }

    public bool TryAccumulate(ushort[] frame, float[] accum, int[] count, int length) {
        var ctx = Context();
        if (ctx == null) return false;
        try {
            var cl = ctx.Cl;
            lock (ctx.Gate) {
                nint bFrame = CreateFrom(ctx, MemFlags.ReadOnly | MemFlags.CopyHostPtr, frame);
                nint bAccum = CreateFrom(ctx, MemFlags.ReadWrite | MemFlags.CopyHostPtr, accum);
                nint bCount = CreateFrom(ctx, MemFlags.ReadWrite | MemFlags.CopyHostPtr, count);
                try {
                    var k = ctx.GetKernel("accumulate");
                    SetMem(cl, k, 0, bFrame); SetMem(cl, k, 1, bAccum); SetMem(cl, k, 2, bCount);
                    SetVal(cl, k, 3, length);
                    Run1D(ctx, k, length);
                    ReadInto(ctx, bAccum, accum);
                    ReadInto(ctx, bCount, count);
                    return true;
                } finally {
                    cl.ReleaseMemObject(bFrame); cl.ReleaseMemObject(bAccum); cl.ReleaseMemObject(bCount);
                }
            }
        } catch (Exception ex) { _log.LogDebug("GPU accumulate fell back: {Msg}", ex.Message); return false; }
    }

    // ─── helpers ──────────────────────────────────────────────────────────

    private static nint CreateFrom<T>(OpenClContext ctx, MemFlags flags, T[] data) where T : unmanaged {
        int err;
        nint buf;
        var span = (ReadOnlySpan<T>)data;
        fixed (T* p = span) {
            buf = ctx.Cl.CreateBuffer(ctx.Context, flags, (nuint)(data.Length * sizeof(T)), p, &err);
        }
        if (err != 0) throw new InvalidOperationException($"CreateBuffer({flags}) failed: {err}");
        return buf;
    }

    private static nint CreateEmpty(OpenClContext ctx, MemFlags flags, nuint size) {
        int err;
        nint buf = ctx.Cl.CreateBuffer(ctx.Context, flags, size, null, &err);
        if (err != 0) throw new InvalidOperationException($"CreateBuffer(empty {flags}) failed: {err}");
        return buf;
    }

    private static void SetMem(CL cl, nint kernel, uint index, nint mem) {
        int err = cl.SetKernelArg(kernel, index, (nuint)sizeof(nint), &mem);
        if (err != 0) throw new InvalidOperationException($"SetKernelArg(mem {index}) failed: {err}");
    }

    private static void SetVal<T>(CL cl, nint kernel, uint index, T value) where T : unmanaged {
        int err = cl.SetKernelArg(kernel, index, (nuint)sizeof(T), &value);
        if (err != 0) throw new InvalidOperationException($"SetKernelArg(val {index}) failed: {err}");
    }

    private static void Run1D(OpenClContext ctx, nint kernel, int n) {
        nuint global = (nuint)n;
        int err = ctx.Cl.EnqueueNdrangeKernel(ctx.Queue, kernel, 1, null, &global, null, 0, null, null);
        if (err != 0) throw new InvalidOperationException($"EnqueueNdrangeKernel(1D) failed: {err}");
        ctx.Cl.Finish(ctx.Queue);
    }

    private static void Run2D(OpenClContext ctx, nint kernel, int w, int h) {
        var global = stackalloc nuint[2] { (nuint)w, (nuint)h };
        int err = ctx.Cl.EnqueueNdrangeKernel(ctx.Queue, kernel, 2, null, global, null, 0, null, null);
        if (err != 0) throw new InvalidOperationException($"EnqueueNdrangeKernel(2D) failed: {err}");
        ctx.Cl.Finish(ctx.Queue);
    }

    private static void ReadInto<T>(OpenClContext ctx, nint buffer, T[] dest) where T : unmanaged {
        fixed (T* p = dest) {
            int err = ctx.Cl.EnqueueReadBuffer(ctx.Queue, buffer, true, 0,
                (nuint)(dest.Length * sizeof(T)), p, 0, null, null);
            if (err != 0) throw new InvalidOperationException($"EnqueueReadBuffer failed: {err}");
        }
    }

    private static float[] BuildGaussianKernel(int radius, double sigma) {
        if (sigma <= 0) sigma = 0.5;
        int size = 2 * radius + 1;
        var k = new float[size];
        double twoSigmaSq = 2 * sigma * sigma;
        double sum = 0;
        for (int i = -radius; i <= radius; i++) {
            double v = Math.Exp(-(i * i) / twoSigmaSq);
            k[i + radius] = (float)v;
            sum += v;
        }
        for (int i = 0; i < size; i++) k[i] = (float)(k[i] / sum);
        return k;
    }

    public void Dispose() => _ctx?.Dispose();
}
