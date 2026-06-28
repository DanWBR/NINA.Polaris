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
using NINA.Polaris.Services.Rknn;   // reuse the backend-agnostic IRknnTileRunner

namespace NINA.Polaris.Services.Ncnn;

/// <summary>Thrown when an ncnn model fails to load or run (caller falls back).</summary>
public sealed class NcnnException : Exception {
    public NcnnException(string message) : base(message) { }
}

/// <summary>
/// One loaded ncnn model running on the Vulkan GPU. Implements the
/// backend-agnostic <see cref="IRknnTileRunner"/> so it slots straight into the
/// existing <c>RknnPipelines</c> tiling/normalization math — the GraXpert models
/// all take a <c>[1, 256, 256, 3]</c> NHWC fp32 tile and return the same shape.
///
/// Layout note: the pnnx-converted graph keeps the model's NHWC layout
/// (leading/trailing Permute). The input ncnn Mat is created as
/// <c>(w=channels, h=tile, c=tile)</c> over the row-major <c>[H][W][C]</c> tile
/// buffer — that memory ordering is identical, which is why no axis juggling is
/// needed (verified against ONNX Runtime in the polaris-ai/ncnn spike). Output
/// comes back the same way. fp16 (packed/storage/arithmetic) is on — the
/// production mode on Adreno, ~2x over fp32 and numerically safe for BGE/denoise.
/// </summary>
public sealed class NcnnSession : IRknnTileRunner {
    private IntPtr _net;
    private readonly object _lock = new();   // ncnn extractor is per-call; serialize tiles per session
    private bool _disposed;

    public int TileSize { get; }
    public int Channels { get; }

    private NcnnSession(IntPtr net, int tileSize, int channels) {
        _net = net;
        TileSize = tileSize;
        Channels = channels;
    }

    /// <summary>
    /// Load a converted model from its <c>.param</c> path; the weights
    /// (<c>.bin</c>) are the sibling with the extension swapped. Vulkan + fp16 on.
    /// </summary>
    public static NcnnSession LoadFromParam(string paramPath, int tileSize = 256, int channels = 3,
                                            ILogger? logger = null) {
        var binPath = paramPath.EndsWith(".param", StringComparison.Ordinal)
            ? paramPath[..^".param".Length] + ".bin"
            : paramPath + ".bin";
        if (!File.Exists(paramPath)) throw new NcnnException($"param not found: {paramPath}");
        if (!File.Exists(binPath)) throw new NcnnException($"bin not found: {binPath}");

        var opt = NcnnNative.ncnn_option_create();
        if (opt == IntPtr.Zero) throw new NcnnException("ncnn_option_create failed");
        IntPtr net = IntPtr.Zero;
        try {
            NcnnNative.ncnn_option_set_num_threads(opt, Math.Clamp(Environment.ProcessorCount, 1, 4));
            NcnnNative.ncnn_option_set_use_vulkan_compute(opt, 1);
            NcnnNative.ncnn_option_set_use_fp16_packed(opt, 1);
            NcnnNative.ncnn_option_set_use_fp16_storage(opt, 1);
            NcnnNative.ncnn_option_set_use_fp16_arithmetic(opt, 1);

            net = NcnnNative.ncnn_net_create();
            if (net == IntPtr.Zero) throw new NcnnException("ncnn_net_create failed");
            NcnnNative.ncnn_net_set_option(net, opt);   // copies the option into the net
            if (NcnnNative.ncnn_net_load_param(net, paramPath) != 0)
                throw new NcnnException($"load_param failed: {paramPath}");
            if (NcnnNative.ncnn_net_load_model(net, binPath) != 0)
                throw new NcnnException($"load_model failed: {binPath}");

            logger?.LogInformation("Loaded ncnn model {Param} (Vulkan, fp16)", paramPath);
            var s = new NcnnSession(net, tileSize, channels);
            net = IntPtr.Zero;   // ownership transferred
            return s;
        } finally {
            NcnnNative.ncnn_option_destroy(opt);
            if (net != IntPtr.Zero) NcnnNative.ncnn_net_destroy(net);
        }
    }

    /// <summary>
    /// Run one tile. <paramref name="nhwcInput"/> is row-major NHWC of length
    /// <c>TileSize*TileSize*Channels</c>. Returns a fresh NHWC fp32 array.
    /// </summary>
    public float[] RunTile(float[] nhwcInput) {
        int expected = TileSize * TileSize * Channels;
        if (nhwcInput.Length != expected)
            throw new NcnnException($"tile length {nhwcInput.Length} != {expected}");

        lock (_lock) {
            if (_disposed) throw new NcnnException("session disposed");
            var pin = GCHandle.Alloc(nhwcInput, GCHandleType.Pinned);
            IntPtr inMat = IntPtr.Zero, outMat = IntPtr.Zero, ex = IntPtr.Zero;
            try {
                inMat = NcnnNative.ncnn_mat_create_external_3d(
                    Channels, TileSize, TileSize, pin.AddrOfPinnedObject(), IntPtr.Zero);
                if (inMat == IntPtr.Zero) throw new NcnnException("mat_create_external_3d failed");

                ex = NcnnNative.ncnn_extractor_create(_net);
                if (ex == IntPtr.Zero) throw new NcnnException("extractor_create failed");
                if (NcnnNative.ncnn_extractor_input(ex, "in0", inMat) != 0)
                    throw new NcnnException("extractor_input(in0) failed");
                if (NcnnNative.ncnn_extractor_extract(ex, "out0", out outMat) != 0 || outMat == IntPtr.Zero)
                    throw new NcnnException("extractor_extract(out0) failed");

                int w = NcnnNative.ncnn_mat_get_w(outMat);
                int h = NcnnNative.ncnn_mat_get_h(outMat);
                int c = NcnnNative.ncnn_mat_get_c(outMat);
                int n = w * h * c;
                if (n <= 0) throw new NcnnException($"bad output shape {w}x{h}x{c}");

                var data = NcnnNative.ncnn_mat_get_data(outMat);
                if (data == IntPtr.Zero) throw new NcnnException("output data null");
                var outBuf = new float[n];
                // Packed copy is valid because cstep == w*h for these dims
                // (w*h = 768 is 4-float aligned, so no per-channel padding).
                Marshal.Copy(data, outBuf, 0, n);
                return outBuf;
            } finally {
                if (outMat != IntPtr.Zero) NcnnNative.ncnn_mat_destroy(outMat);
                if (ex != IntPtr.Zero) NcnnNative.ncnn_extractor_destroy(ex);
                if (inMat != IntPtr.Zero) NcnnNative.ncnn_mat_destroy(inMat);
                pin.Free();
            }
        }
    }

    public void Dispose() {
        lock (_lock) {
            if (_disposed) return;
            _disposed = true;
            if (_net != IntPtr.Zero) {
                try { NcnnNative.ncnn_net_destroy(_net); } catch { }
                _net = IntPtr.Zero;
            }
        }
    }
}
