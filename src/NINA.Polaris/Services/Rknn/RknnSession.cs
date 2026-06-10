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

    private RknnSession(ulong ctx, int tileSize, int channels) {
        _ctx = ctx;
        TileSize = tileSize;
        Channels = channels;
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

            logger?.LogInformation("RKNN model loaded (in={In}, out={Out}, tile={Tile}, ch={Ch})",
                ion.n_input, ion.n_output, tileSize, channels);
            return new RknnSession(ctx, tileSize, channels);
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
                    var result = new float[n];
                    Marshal.Copy(outputs[0].buf, result, 0, n);
                    return result;
                } finally {
                    try { rknn_outputs_release(_ctx, 1, outputs); } catch { }
                }
            } finally {
                pin.Free();
            }
        }
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
