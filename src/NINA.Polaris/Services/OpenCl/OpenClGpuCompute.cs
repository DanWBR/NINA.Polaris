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

using System.Diagnostics;
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
    private string? _initError;

    /// <summary>Per-op offload allow-list, decided once at init from the device's
    /// memory model (see <see cref="GpuOffloadPolicy"/>). <c>null</c> means "allow
    /// every op" — the state while the discrete-device probe is running (so each
    /// kernel can execute to be measured) and before init.</summary>
    private volatile GpuOffloadPolicy? _policy;

    public OpenClGpuCompute(ILogger<OpenClGpuCompute> log) {
        _log = log;
    }

    public string BackendName => _ctx != null ? $"OpenCL: {_ctx.DeviceName}" : "OpenCL (uninitialised)";
    public bool IsHardware => true;

    /// <summary>User toggle (Settings -> persisted UseGpuOpenCl). When false,
    /// every op declines so the CPU path runs, without tearing down the context.
    /// Checked per call so it can flip at runtime.</summary>
    public bool Enabled { get; set; } = true;

    // --- bring-up diagnostics (surfaced by GET /api/system/gpu) ---

    /// <summary>True once a context built successfully.</summary>
    public bool Initialized => _ctx != null;

    /// <summary>Device name when initialised, else null.</summary>
    public string? Device => _ctx?.DeviceName;

    /// <summary>The reason init failed (incl. the OpenCL build log) when it did,
    /// else null. Invaluable for the first hardware bring-up.</summary>
    public string? InitError => _initError;

    /// <summary>CL_DEVICE_HOST_UNIFIED_MEMORY for the active device (null until
    /// initialised). True for the SBC GPUs we target, false for a discrete card.</summary>
    public bool? HostUnifiedMemory => _ctx?.HostUnifiedMemory;

    /// <summary>The per-op offload policy chosen at init (null until initialised).
    /// Settable so the self-test / benchmark can force every kernel on to
    /// measure/validate the raw kernels regardless of the production decision;
    /// callers should restore the previous value (see <see cref="WithAllKernels"/>).</summary>
    public GpuOffloadPolicy? OffloadPolicy {
        get => _policy;
        set => _policy = value;
    }

    /// <summary>The ops actually offloaded to the GPU under the current policy
    /// (empty when uninitialised), for status/diagnostics.</summary>
    public IReadOnlyList<GpuOp> OffloadedOps =>
        _policy?.AllowedOps ?? Array.Empty<GpuOp>();

    /// <summary>True when <paramref name="op"/> may run on the GPU. A null policy
    /// (probe in flight / pre-init) allows everything so the gate never blocks a
    /// kernel that init itself needs to measure.</summary>
    private bool Offload(GpuOp op) => _policy?.Allows(op) ?? true;

    /// <summary>Run <paramref name="body"/> with the offload policy forced to
    /// allow every op, restoring the previous policy afterwards. Used by the
    /// self-test (validate every kernel on this GPU) and the benchmark (measure
    /// the raw per-op GPU-vs-CPU speed), which must exercise kernels the
    /// production policy would otherwise decline. Forces init first so a real
    /// context exists.</summary>
    public T WithAllKernels<T>(Func<T> body) {
        EnsureInitialized();
        var saved = _policy;
        _policy = GpuOffloadPolicy.AllowAll(_ctx?.HostUnifiedMemory ?? true);
        try { return body(); } finally { _policy = saved; }
    }

    /// <summary>Force the lazy init (idempotent) and report whether the GPU path
    /// is usable. Lets a status endpoint trigger + observe the real result.</summary>
    public bool EnsureInitialized() => Context() != null;

    private OpenClContext? Context() {
        if (!Enabled) return null;       // user disabled -> decline -> CPU path
        if (_initFailed) return null;
        if (_ctx != null) return _ctx;
        lock (_initGate) {
            if (_ctx != null) return _ctx;
            if (_initTried) return _ctx;
            _initTried = true;
            try {
                if (!OpenClRuntime.IsAvailable) {
                    _initFailed = true;
                    _initError = OpenClRuntime.Diagnostics;
                    return null;
                }
                var src = LoadKernelSource();
                _ctx = new OpenClContext(src);
                // Decide the per-op offload policy by probing each kernel
                // GPU-vs-CPU on this exact device — for both discrete GPUs and
                // unified-memory SBCs. "Unified memory ⇒ everything wins" is false
                // on some stacks (e.g. Qualcomm Adreno copies host↔device for
                // ordinary buffers, so warp/debayer lose while the same code wins
                // on Mali), so only ops the probe measured as actually faster get
                // offloaded; the rest run on the CPU. _policy stays null across the
                // probe so each kernel can run to be measured.
                var unified = _ctx.HostUnifiedMemory;
                var policy = ProbeOffloadPolicy(unified);
                _policy = policy;
                _log.LogInformation(
                    "OpenCL GPU backend ready: {Device} ({Mem} memory; offloading {Ops})",
                    _ctx.DeviceName,
                    unified ? "unified" : "discrete",
                    policy.AllowedOps.Count > 0 ? string.Join(", ", policy.AllowedOps) : "nothing");
                return _ctx;
            } catch (Exception ex) {
                _initFailed = true;
                _initError = ex.Message; // includes the OpenCL build log on a build failure
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

    // ─── per-op offload probe ─────────────────────────────────────────────

    // One representative tile (1 MP). Big enough that the per-op transfer-vs-
    // compute balance resembles a real frame, small enough that the whole probe
    // is well under a second. A tiny buffer would over-penalise the GPU (fixed
    // launch/transfer latency dominates), so this is a fair, slightly
    // conservative gate.
    private const int ProbeDim = 1024;
    private const int ProbeIters = 5;

    /// <summary>
    /// Times each offloadable kernel GPU-vs-CPU once (best-of-N) and returns the
    /// allow-list. Runs on both discrete and unified-memory devices, inside init,
    /// with <see cref="_policy"/> still null so the gate lets every kernel
    /// execute. BoxBlur8 isn't measured (the CPU backend declines it); the policy
    /// derives it from the separable-blur result. <paramref name="unifiedMemory"/>
    /// is recorded on the policy for diagnostics only — the gating is identical.
    /// </summary>
    private GpuOffloadPolicy ProbeOffloadPolicy(bool unifiedMemory) {
        const int w = ProbeDim, h = ProbeDim, n = w * h;
        var cpu = new CpuGpuCompute();
        var data = new ushort[n];
        for (int i = 0; i < n; i++) data[i] = (ushort)((i * 41 + (i % 7) * 1000) & 0xFFFF);
        var lut = new byte[65536];
        for (int i = 0; i < lut.Length; i++) lut[i] = (byte)((i * 255) / 65535);
        var t = new AffineTransform { M00 = 1, M11 = 1, Tx = 7.3, Ty = -5.1 };

        var speedups = new Dictionary<GpuOp, double> {
            [GpuOp.Warp] = Speedup(
                () => TryWarpAffine(data, w, h, t, out _),
                () => cpu.TryWarpAffine(data, w, h, t, out _)),
            [GpuOp.Debayer] = Speedup(
                () => TryDebayerBilinear(data, w, h, BayerPatternEnum.RGGB, out _),
                () => cpu.TryDebayerBilinear(data, w, h, BayerPatternEnum.RGGB, out _)),
            [GpuOp.SeparableBlur] = Speedup(
                () => TrySeparableBlur(data, w, h, 3, 1.5, out _),
                () => cpu.TrySeparableBlur(data, w, h, 3, 1.5, out _)),
            [GpuOp.ApplyLut8] = Speedup(
                () => TryApplyLut8(data, lut, out _),
                () => cpu.TryApplyLut8(data, lut, out _)),
            [GpuOp.Accumulate] = Speedup(
                () => { var a = new float[n]; var c = new int[n]; return TryAccumulate(data, a, c, n); },
                () => { var a = new float[n]; var c = new int[n]; return cpu.TryAccumulate(data, a, c, n); }),
        };
        return GpuOffloadPolicy.FromProbe(speedups, unifiedMemory: unifiedMemory);
    }

    /// <summary>GPU/CPU speedup (>1 means the GPU is faster) from the best (min)
    /// time of each side over a few iterations; min reduces GC/scheduler noise.
    /// Returns 0 — i.e. "GPU not faster", so the op is not offloaded — if either
    /// side declines or is unmeasurable.</summary>
    private static double Speedup(Func<bool> gpu, Func<bool> cpu) {
        double g = BestMs(gpu), c = BestMs(cpu);
        return g > 0 && c > 0 ? c / g : 0;
    }

    private static double BestMs(Func<bool> op) {
        if (!op()) return -1; // warm-up + capability probe (JIT, kernel build, buffers)
        double best = double.MaxValue;
        var sw = new Stopwatch();
        for (int i = 0; i < ProbeIters; i++) {
            sw.Restart();
            if (!op()) return -1;
            sw.Stop();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }
        return best > 0 ? best : double.Epsilon;
    }

    // ─── kernels ──────────────────────────────────────────────────────────

    public bool TrySeparableBlur(ushort[] data, int width, int height, int radius,
                                 double sigma, out ushort[] result) {
        result = Array.Empty<ushort>();
        if (radius < 1) return false;
        var ctx = Context();
        if (ctx == null || !Offload(GpuOp.SeparableBlur)) return false;
        try {
            int n = width * height;
            var kernel = BuildGaussianKernel(radius, sigma <= 0 ? radius / 2.0 : sigma);
            var cl = ctx.Cl;
            lock (ctx.Gate) {
                nint bSrc = CreateInput(ctx, MemFlags.ReadOnly, data);
                nint bTmp = CreateEmpty(ctx, MemFlags.ReadWrite, (nuint)(n * sizeof(float)));
                nint bDst = CreateOutput(ctx, MemFlags.WriteOnly, (nuint)(n * sizeof(ushort)));
                nint bKern = CreateInput(ctx, MemFlags.ReadOnly, kernel);
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
                    ReadResult(ctx, bDst, outp);
                    result = outp;
                    return true;
                } finally {
                    cl.ReleaseMemObject(bSrc); cl.ReleaseMemObject(bTmp);
                    cl.ReleaseMemObject(bDst); cl.ReleaseMemObject(bKern);
                }
            }
        } catch (Exception ex) { _log.LogDebug("GPU blur fell back: {Msg}", ex.Message); return false; }
    }

    public bool TryBoxBlur8(byte[] src, int width, int height, int radius, int passes, out byte[] result) {
        result = Array.Empty<byte>();
        if (radius < 1 || passes < 1) return false;
        var ctx = Context();
        if (ctx == null || !Offload(GpuOp.BoxBlur8)) return false;
        try {
            int n = width * height;
            var cl = ctx.Cl;
            lock (ctx.Gate) {
                // Ping-pong two device buffers; H then V per pass.
                nint a = CreateInput(ctx, MemFlags.ReadWrite, src);
                nint b = CreateEmpty(ctx, MemFlags.ReadWrite, (nuint)n);
                try {
                    var kh = ctx.GetKernel("box_blur_h");
                    var kv = ctx.GetKernel("box_blur_v");
                    for (int p = 0; p < passes; p++) {
                        // H: a -> b
                        SetMem(cl, kh, 0, a); SetMem(cl, kh, 1, b);
                        SetVal(cl, kh, 2, width); SetVal(cl, kh, 3, height); SetVal(cl, kh, 4, radius);
                        Run2D(ctx, kh, width, height);
                        // V: b -> a
                        SetMem(cl, kv, 0, b); SetMem(cl, kv, 1, a);
                        SetVal(cl, kv, 2, width); SetVal(cl, kv, 3, height); SetVal(cl, kv, 4, radius);
                        Run2D(ctx, kv, width, height);
                    }
                    var outp = new byte[n];
                    ReadResult(ctx, a, outp); // result lands back in 'a' after V
                    result = outp;
                    return true;
                } finally {
                    cl.ReleaseMemObject(a); cl.ReleaseMemObject(b);
                }
            }
        } catch (Exception ex) { _log.LogDebug("GPU box blur fell back: {Msg}", ex.Message); return false; }
    }

    public bool TryWarpAffine(ushort[] source, int width, int height,
                              AffineTransform transform, out ushort[] result) {
        result = Array.Empty<ushort>();
        var ctx = Context();
        if (ctx == null || !Offload(GpuOp.Warp)) return false;
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
                nint bSrc = CreateInput(ctx, MemFlags.ReadOnly, source);
                nint bDst = CreateOutput(ctx, MemFlags.WriteOnly, (nuint)(n * sizeof(ushort)));
                try {
                    var k = ctx.GetKernel("warp_affine");
                    SetMem(cl, k, 0, bSrc); SetMem(cl, k, 1, bDst);
                    SetVal(cl, k, 2, width); SetVal(cl, k, 3, height);
                    SetVal(cl, k, 4, i00); SetVal(cl, k, 5, i01); SetVal(cl, k, 6, i10);
                    SetVal(cl, k, 7, i11); SetVal(cl, k, 8, itx); SetVal(cl, k, 9, ity);
                    Run2D(ctx, k, width, height);
                    var outp = new ushort[n];
                    ReadResult(ctx, bDst, outp);
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
        result = null!;
        var block = ColorBlock(pattern);
        if (block == null) return false; // None/Auto/unsupported -> CPU
        var ctx = Context();
        if (ctx == null || !Offload(GpuOp.Debayer)) return false;
        try {
            int n = width * height;
            if (cfa.Length < n) return false;
            var cl = ctx.Cl;
            lock (ctx.Gate) {
                nint bCfa = CreateInput(ctx, MemFlags.ReadOnly, cfa);
                nint bR = CreateOutput(ctx, MemFlags.WriteOnly, (nuint)(n * sizeof(ushort)));
                nint bG = CreateOutput(ctx, MemFlags.WriteOnly, (nuint)(n * sizeof(ushort)));
                nint bB = CreateOutput(ctx, MemFlags.WriteOnly, (nuint)(n * sizeof(ushort)));
                try {
                    var k = ctx.GetKernel("debayer_bilinear");
                    SetMem(cl, k, 0, bCfa); SetMem(cl, k, 1, bR); SetMem(cl, k, 2, bG); SetMem(cl, k, 3, bB);
                    SetVal(cl, k, 4, width); SetVal(cl, k, 5, height);
                    SetVal(cl, k, 6, block[0]); SetVal(cl, k, 7, block[1]);
                    SetVal(cl, k, 8, block[2]); SetVal(cl, k, 9, block[3]);
                    Run2D(ctx, k, width, height);
                    var r = new ushort[n]; var g = new ushort[n]; var b = new ushort[n];
                    ReadResult(ctx, bR, r); ReadResult(ctx, bG, g); ReadResult(ctx, bB, b);
                    result = new BayerDebayer.Channels(r, g, b);
                    return true;
                } finally {
                    cl.ReleaseMemObject(bCfa); cl.ReleaseMemObject(bR);
                    cl.ReleaseMemObject(bG); cl.ReleaseMemObject(bB);
                }
            }
        } catch (Exception ex) { _log.LogDebug("GPU debayer fell back: {Msg}", ex.Message); return false; }
    }

    // 2x2 colour block (0=R,1=G,2=B) row-major, matching BayerDebayer.ColorBlockFor.
    private static int[]? ColorBlock(BayerPatternEnum pattern) => pattern switch {
        BayerPatternEnum.RGGB => new[] { 0, 1, 1, 2 },
        BayerPatternEnum.GRBG => new[] { 1, 0, 2, 1 },
        BayerPatternEnum.GBRG => new[] { 1, 2, 0, 1 },
        BayerPatternEnum.BGGR => new[] { 2, 1, 1, 0 },
        _ => null,
    };

    public bool TryApplyLut8(ushort[] data, byte[] lut, out byte[] result) {
        result = Array.Empty<byte>();
        var ctx = Context();
        if (ctx == null || !Offload(GpuOp.ApplyLut8)) return false;
        try {
            int n = data.Length;
            var cl = ctx.Cl;
            lock (ctx.Gate) {
                nint bSrc = CreateInput(ctx, MemFlags.ReadOnly, data);
                nint bLut = CreateInput(ctx, MemFlags.ReadOnly, lut);
                nint bDst = CreateOutput(ctx, MemFlags.WriteOnly, (nuint)n);
                try {
                    var k = ctx.GetKernel("apply_lut8");
                    SetMem(cl, k, 0, bSrc); SetMem(cl, k, 1, bDst); SetMem(cl, k, 2, bLut);
                    SetVal(cl, k, 3, n);
                    Run1D(ctx, k, n);
                    var outp = new byte[n];
                    ReadResult(ctx, bDst, outp);
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
        if (ctx == null || !Offload(GpuOp.Accumulate)) return false;
        try {
            var cl = ctx.Cl;
            lock (ctx.Gate) {
                nint bFrame = CreateInput(ctx, MemFlags.ReadOnly, frame);
                nint bAccum = CreateInput(ctx, MemFlags.ReadWrite, accum);
                nint bCount = CreateInput(ctx, MemFlags.ReadWrite, count);
                try {
                    var k = ctx.GetKernel("accumulate");
                    SetMem(cl, k, 0, bFrame); SetMem(cl, k, 1, bAccum); SetMem(cl, k, 2, bCount);
                    SetVal(cl, k, 3, length);
                    Run1D(ctx, k, length);
                    ReadResult(ctx, bAccum, accum);
                    ReadResult(ctx, bCount, count);
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

    // ─── unified-memory zero-copy buffers ──────────────────────────────────
    //
    // On a discrete GPU every input must be copied host→device and every result
    // copied back, so CreateFrom(CopyHostPtr)+ReadInto (real DMA) is correct and
    // unavoidable. But on a unified-memory device (CL_DEVICE_HOST_UNIFIED_MEMORY)
    // host and GPU share physical RAM, so those copies are pure waste — yet some
    // stacks (notably Qualcomm Adreno on the QCS6490) still perform a genuine
    // memcpy for an ordinary buffer, which is exactly why the light memory-bound
    // kernels (warp, debayer) measured *slower* than the CPU there.
    //
    // The fix: allocate buffers with CL_MEM_ALLOC_HOST_PTR so the driver places
    // them in host-accessible memory the GPU can read/write in place, and move
    // data through clEnqueueMapBuffer / clEnqueueUnmapMemObject. On unified memory
    // the map is a pointer hand-back into the same pages (no transfer); we still do
    // one plain CPU memcpy to/from the caller's managed array, but the device-side
    // staging copy — the part the Adreno stack was charging us for — is gone. The
    // discrete path is left byte-for-byte unchanged (it legitimately needs copies).
    // Map/unmap doesn't alter any computed value, so CPU-parity stays bit-exact.

    /// <summary>Create a device buffer and fill it from <paramref name="data"/>.
    /// Unified memory: ALLOC_HOST_PTR + map-write (no host→device staging copy);
    /// discrete: CopyHostPtr at create time. <paramref name="deviceAccess"/> is the
    /// kernel's view (ReadOnly / ReadWrite); the host map-write is always allowed.</summary>
    private static nint CreateInput<T>(OpenClContext ctx, MemFlags deviceAccess, T[] data) where T : unmanaged {
        if (ctx.HostUnifiedMemory) {
            nint buf = CreateEmpty(ctx, deviceAccess | MemFlags.AllocHostPtr,
                (nuint)(data.Length * sizeof(T)));
            WriteViaMap(ctx, buf, data);
            return buf;
        }
        return CreateFrom(ctx, deviceAccess | MemFlags.CopyHostPtr, data);
    }

    /// <summary>Create a kernel-output buffer. Unified memory: ALLOC_HOST_PTR so
    /// the result can be mapped back without a device→host copy; discrete: a plain
    /// device buffer read back with <see cref="ReadInto"/>.</summary>
    private static nint CreateOutput(OpenClContext ctx, MemFlags deviceAccess, nuint size) =>
        CreateEmpty(ctx, ctx.HostUnifiedMemory ? deviceAccess | MemFlags.AllocHostPtr : deviceAccess, size);

    /// <summary>Read a kernel result back into <paramref name="dest"/>: zero-copy
    /// map on unified memory, blocking EnqueueReadBuffer on a discrete device.</summary>
    private static void ReadResult<T>(OpenClContext ctx, nint buffer, T[] dest) where T : unmanaged {
        if (ctx.HostUnifiedMemory) ReadViaMap(ctx, buffer, dest);
        else ReadInto(ctx, buffer, dest);
    }

    /// <summary>Map <paramref name="buffer"/> for writing, memcpy <paramref name="data"/>
    /// in, unmap. Blocking; on the in-order queue the unmap is ordered before any
    /// kernel that later reads the buffer.</summary>
    private static void WriteViaMap<T>(OpenClContext ctx, nint buffer, T[] data) where T : unmanaged {
        long bytes = (long)data.Length * sizeof(T);
        int err;
        void* p = ctx.Cl.EnqueueMapBuffer(ctx.Queue, buffer, true, MapFlags.Write, 0,
            (nuint)bytes, 0, null, null, &err);
        if (err != 0) throw new InvalidOperationException($"EnqueueMapBuffer(write) failed: {err}");
        try {
            fixed (T* s = data) Buffer.MemoryCopy(s, p, bytes, bytes);
        } finally {
            int u = ctx.Cl.EnqueueUnmapMemObject(ctx.Queue, buffer, p, 0, null, null);
            if (u != 0) throw new InvalidOperationException($"EnqueueUnmapMemObject(write) failed: {u}");
        }
    }

    /// <summary>Map <paramref name="buffer"/> for reading, memcpy out into
    /// <paramref name="dest"/>, unmap. Blocking, so the data is valid on return.</summary>
    private static void ReadViaMap<T>(OpenClContext ctx, nint buffer, T[] dest) where T : unmanaged {
        long bytes = (long)dest.Length * sizeof(T);
        int err;
        void* p = ctx.Cl.EnqueueMapBuffer(ctx.Queue, buffer, true, MapFlags.Read, 0,
            (nuint)bytes, 0, null, null, &err);
        if (err != 0) throw new InvalidOperationException($"EnqueueMapBuffer(read) failed: {err}");
        try {
            fixed (T* d = dest) Buffer.MemoryCopy(p, d, bytes, bytes);
        } finally {
            int u = ctx.Cl.EnqueueUnmapMemObject(ctx.Queue, buffer, p, 0, null, null);
            if (u != 0) throw new InvalidOperationException($"EnqueueUnmapMemObject(read) failed: {u}");
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
