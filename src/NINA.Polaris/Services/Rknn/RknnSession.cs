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
using static NINA.Polaris.Services.Rknn.RknnNative;

namespace NINA.Polaris.Services.Rknn;

/// <summary>
/// A loaded RKNN model running on the Rockchip NPU. Wraps the raw
/// <see cref="RknnNative"/> handle: init from a <c>.rknn</c> byte buffer, pin
/// the NPU to all three cores, and run one fp32 NHWC tile at a time.
///
/// <para>Thread safety: a single <c>rknn_context</c> is not safe for
/// concurrent <c>rknn_run</c>, so <see cref="RunTile"/> is serialized under a
/// lock. The tile loop is sequential anyway (one NPU), and the three cores are
/// used within a single inference via the core mask, not by parallel
/// calls.</para>
///
/// <para>This type is only ever constructed when <see cref="RknnRuntime.IsAvailable"/>
/// is true. Construction throws <see cref="RknnException"/> if the model fails
/// to load (corrupt file, driver mismatch, no NPU) so the caller can fall back
/// to the CPU/CLI path.</para>
/// </summary>
public sealed class RknnSession : IRknnTileRunner {
    private readonly object _lock = new();
    private ulong _ctx;
    private bool _disposed;

    public int TileSize { get; }
    public int Channels { get; }

    // Output tensor geometry, learned from RKNN_QUERY_OUTPUT_ATTR. RKNN's
    // want_float output is typically NCHW (planar) even when the ONNX output
    // was NHWC, so RunTile converts to canonical NHWC interleaved using these.
    private readonly int _outC;
    private readonly int _outH;
    private readonly int _outW;
    private readonly bool _outNchw;

    private RknnSession(ulong ctx, int tileSize, int channels,
                        int outC, int outH, int outW, bool outNchw) {
        _ctx = ctx;
        TileSize = tileSize;
        Channels = channels;
        _outC = outC;
        _outH = outH;
        _outW = outW;
        _outNchw = outNchw;
    }

    /// <summary>
    /// Load a model from raw <c>.rknn</c> bytes. <paramref name="tileSize"/> /
    /// <paramref name="channels"/> describe the model's input window (256 / 3
    /// for the GraXpert models). Throws <see cref="RknnException"/> on failure.
    /// </summary>
    public static RknnSession Load(byte[] modelBytes, int tileSize = 256, int channels = 3,
                                   ILogger? logger = null) {
        if (modelBytes == null || modelBytes.Length == 0)
            throw new RknnException("Empty RKNN model buffer");

        int ret = rknn_init(out var ctx, modelBytes, (uint)modelBytes.Length,
            RKNN_FLAG_PRIOR_HIGH, IntPtr.Zero);
        if (ret != 0)
            throw new RknnException($"rknn_init failed ({ret})");

        try {
            // Use all three NPU cores for a single inference (the model was
            // compiled with multi-core-model-mode). Non-fatal if the runtime
            // rejects it (single-core boards) — the model still runs.
            int cm = rknn_set_core_mask(ctx, rknn_core_mask.RKNN_NPU_CORE_0_1_2);
            if (cm != 0) logger?.LogDebug("rknn_set_core_mask returned {Ret} (continuing single-core)", cm);

            // Sanity: exactly one input and at least one output.
            var ion = new rknn_input_output_num();
            int q = rknn_query(ctx, rknn_query_cmd.RKNN_QUERY_IN_OUT_NUM, ref ion,
                (uint)Marshal.SizeOf<rknn_input_output_num>());
            if (q == 0 && (ion.n_input < 1 || ion.n_output < 1))
                throw new RknnException($"unexpected model IO (in={ion.n_input}, out={ion.n_output})");

            // Learn the output layout. Default to NCHW (RKNN want_float's usual
            // layout) with the model's known geometry; refine from the query.
            int outC = channels, outH = tileSize, outW = tileSize;
            bool outNchw = true;
            try {
                var oattr = new rknn_tensor_attr { dims = new uint[RKNN_MAX_DIMS], name = "", index = 0 };
                int oq = rknn_query(ctx, rknn_query_cmd.RKNN_QUERY_OUTPUT_ATTR, ref oattr,
                    (uint)Marshal.SizeOf<rknn_tensor_attr>());
                if (oq == 0 && oattr.n_dims == 4 && oattr.dims != null) {
                    if (oattr.fmt == rknn_tensor_format.RKNN_TENSOR_NHWC) {
                        outH = (int)oattr.dims[1]; outW = (int)oattr.dims[2]; outC = (int)oattr.dims[3];
                        outNchw = false;
                    } else { // NCHW (and NC1HWC2 reports logical NCHW dims here)
                        outC = (int)oattr.dims[1]; outH = (int)oattr.dims[2]; outW = (int)oattr.dims[3];
                        outNchw = true;
                    }
                }
                logger?.LogInformation("RKNN output attr: fmt={Fmt} dims=[{D0},{D1},{D2},{D3}] -> {C}x{H}x{W} nchw={Nchw}",
                    oattr.fmt, oattr.dims?[0], oattr.dims?[1], oattr.dims?[2], oattr.dims?[3],
                    outC, outH, outW, outNchw);
            } catch (Exception ex) {
                logger?.LogWarning(ex, "RKNN output attr query failed; assuming NCHW {C}x{H}x{W}", outC, outH, outW);
            }

            logger?.LogInformation("RKNN model loaded (in={In}, out={Out}, tile={Tile}, ch={Ch})",
                ion.n_input, ion.n_output, tileSize, channels);
            return new RknnSession(ctx, tileSize, channels, outC, outH, outW, outNchw);
        } catch {
            try { rknn_destroy(ctx); } catch { }
            throw;
        }
    }

    /// <summary>Convenience: load straight from a <c>.rknn</c> file path.</summary>
    public static RknnSession LoadFromFile(string path, int tileSize = 256, int channels = 3,
                                           ILogger? logger = null)
        => Load(File.ReadAllBytes(path), tileSize, channels, logger);

    /// <inheritdoc/>
    public float[] RunTile(float[] nhwcInput) {
        if (nhwcInput == null) throw new ArgumentNullException(nameof(nhwcInput));
        int expected = TileSize * TileSize * Channels;
        if (nhwcInput.Length != expected)
            throw new ArgumentException($"tile input length {nhwcInput.Length}, expected {expected}");

        lock (_lock) {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var pin = GCHandle.Alloc(nhwcInput, GCHandleType.Pinned);
            try {
                var inputs = new[] {
                    new rknn_input {
                        index = 0,
                        buf = pin.AddrOfPinnedObject(),
                        size = (uint)(nhwcInput.Length * sizeof(float)),
                        pass_through = 0,
                        type = rknn_tensor_type.RKNN_TENSOR_FLOAT32,
                        fmt = rknn_tensor_format.RKNN_TENSOR_NHWC
                    }
                };
                int si = rknn_inputs_set(_ctx, 1, inputs);
                if (si != 0) throw new RknnException($"rknn_inputs_set failed ({si})");

                int rr = rknn_run(_ctx, IntPtr.Zero);
                if (rr != 0) throw new RknnException($"rknn_run failed ({rr})");

                // want_float=1 → the runtime dequantizes/converts to fp32 for us
                // and allocates the output buffer (is_prealloc=0).
                var outputs = new[] {
                    new rknn_output { want_float = 1, is_prealloc = 0, index = 0, buf = IntPtr.Zero, size = 0 }
                };
                int go = rknn_outputs_get(_ctx, 1, outputs, IntPtr.Zero);
                if (go != 0) throw new RknnException($"rknn_outputs_get failed ({go})");

                try {
                    int n = (int)(outputs[0].size / sizeof(float));
                    var raw = new float[n];
                    Marshal.Copy(outputs[0].buf, raw, 0, n);
                    // Normalize to NHWC interleaved (the pipelines read
                    // out[p*C + c]). RKNN want_float is usually NCHW (planar),
                    // so transpose [C,H,W] -> [H,W,C] when needed.
                    return _outNchw && _outC > 1 ? PlanarToInterleaved(raw) : raw;
                } finally {
                    try { rknn_outputs_release(_ctx, 1, outputs); } catch { }
                }
            } finally {
                pin.Free();
            }
        }
    }

    /// <summary>Transpose a planar NCHW output [C,H,W] to NHWC interleaved [H,W,C].</summary>
    private float[] PlanarToInterleaved(float[] planar) {
        int hw = _outH * _outW;
        if (planar.Length < hw * _outC) return planar;   // unexpected size, leave as-is
        var inter = new float[hw * _outC];
        for (int c = 0; c < _outC; c++) {
            int baseC = c * hw;
            for (int p = 0; p < hw; p++)
                inter[p * _outC + c] = planar[baseC + p];
        }
        return inter;
    }

    public void Dispose() {
        lock (_lock) {
            if (_disposed) return;
            _disposed = true;
            if (_ctx != 0) {
                try { rknn_destroy(_ctx); } catch { }
                _ctx = 0;
            }
        }
    }
}

/// <summary>Thrown when an RKNN runtime call fails. Callers catch this and fall
/// back to the CPU/CLI inference path.</summary>
public sealed class RknnException : Exception {
    public RknnException(string message) : base(message) { }
    public RknnException(string message, Exception inner) : base(message, inner) { }
}
