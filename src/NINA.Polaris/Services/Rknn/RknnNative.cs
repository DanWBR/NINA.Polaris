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

namespace NINA.Polaris.Services.Rknn;

/// <summary>
/// Raw P/Invoke surface for Rockchip's RKNPU2 runtime (<c>librknnrt.so</c>),
/// the user-space library that drives the NPU on RK3588/RK3588S boards
/// (Orange Pi 5 Pro, etc.). Struct layouts and enum values are transcribed
/// verbatim from <c>rknn_api.h</c> (RKNPU2 runtime, version 2.3.x) so the
/// marshaling matches the native ABI exactly.
///
/// This binding is host-only and Linux-arm64-only at runtime. The DllImport
/// declarations themselves are harmless on other platforms (the import is
/// resolved lazily on first call); callers MUST gate every use behind
/// <see cref="RknnRuntime.IsAvailable"/> so the library is never touched on a
/// machine that has no NPU.
///
/// We do not expose the full API, only what the GraXpert tile pipeline needs:
/// init (from a model byte buffer), set the 3-core mask, set one fp32 NHWC
/// input, run, read one fp32 output, release, destroy. Everything else in the
/// header (zero-copy memory, async, perf counters, dynamic shapes) is omitted
/// on purpose.
/// </summary>
internal static class RknnNative {
    // The native runtime ships as librknnrt.so. .NET maps the bare name
    // "rknnrt" to "librknnrt.so" on Linux. We also copy the .so next to the
    // app (csproj, linux-arm64) and the .deb drops it in /usr/lib, so the
    // default resolver finds it without LD_LIBRARY_PATH fiddling.
    private const string Lib = "rknnrt";

    // rknn_context is `typedef uint64_t rknn_context;` — an opaque handle.

    // ─── flags (rknn_init) ──────────────────────────────────────────────
    public const uint RKNN_FLAG_PRIOR_HIGH = 0x00000000;
    public const uint RKNN_FLAG_PRIOR_MEDIUM = 0x00000001;
    public const uint RKNN_FLAG_PRIOR_LOW = 0x00000002;

    public const int RKNN_MAX_DIMS = 16;
    public const int RKNN_MAX_NAME_LEN = 256;

    // ─── enums ──────────────────────────────────────────────────────────
    public enum rknn_tensor_type {
        RKNN_TENSOR_FLOAT32 = 0,
        RKNN_TENSOR_FLOAT16,
        RKNN_TENSOR_INT8,
        RKNN_TENSOR_UINT8,
        RKNN_TENSOR_INT16,
        RKNN_TENSOR_UINT16,
        RKNN_TENSOR_INT32,
        RKNN_TENSOR_UINT32,
        RKNN_TENSOR_INT64,
        RKNN_TENSOR_BOOL,
        RKNN_TENSOR_INT4,
        RKNN_TENSOR_BFLOAT16,
        RKNN_TENSOR_TYPE_MAX
    }

    public enum rknn_tensor_qnt_type {
        RKNN_TENSOR_QNT_NONE = 0,
        RKNN_TENSOR_QNT_DFP,
        RKNN_TENSOR_QNT_AFFINE_ASYMMETRIC,
        RKNN_TENSOR_QNT_MAX
    }

    public enum rknn_tensor_format {
        RKNN_TENSOR_NCHW = 0,
        RKNN_TENSOR_NHWC,
        RKNN_TENSOR_NC1HWC2,
        RKNN_TENSOR_UNDEFINED,
        RKNN_TENSOR_FORMAT_MAX
    }

    public enum rknn_core_mask {
        RKNN_NPU_CORE_AUTO = 0,
        RKNN_NPU_CORE_0 = 1,
        RKNN_NPU_CORE_1 = 2,
        RKNN_NPU_CORE_2 = 4,
        RKNN_NPU_CORE_0_1 = 3,
        RKNN_NPU_CORE_0_1_2 = 7,
        RKNN_NPU_CORE_ALL = 0xffff
    }

    public enum rknn_query_cmd {
        RKNN_QUERY_IN_OUT_NUM = 0,
        RKNN_QUERY_INPUT_ATTR = 1,
        RKNN_QUERY_OUTPUT_ATTR = 2
    }

    // ─── structs ────────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    public struct rknn_input_output_num {
        public uint n_input;
        public uint n_output;
    }

    // Field order/types verbatim from rknn_api.h. LayoutKind.Sequential with
    // the default pack reproduces the C natural alignment (padding after the
    // int8 fl / uint8 pass_through), which matches the native struct.
    [StructLayout(LayoutKind.Sequential)]
    public struct rknn_tensor_attr {
        public uint index;
        public uint n_dims;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = RKNN_MAX_DIMS)]
        public uint[] dims;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = RKNN_MAX_NAME_LEN)]
        public string name;
        public uint n_elems;
        public uint size;
        public rknn_tensor_format fmt;
        public rknn_tensor_type type;
        public rknn_tensor_qnt_type qnt_type;
        public sbyte fl;
        public int zp;
        public float scale;
        public uint w_stride;
        public uint size_with_stride;
        public byte pass_through;
        public uint h_stride;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct rknn_input {
        public uint index;
        public IntPtr buf;
        public uint size;
        public byte pass_through;
        public rknn_tensor_type type;
        public rknn_tensor_format fmt;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct rknn_output {
        public byte want_float;
        public byte is_prealloc;
        public uint index;
        public IntPtr buf;
        public uint size;
    }

    // ─── functions ──────────────────────────────────────────────────────
    // int rknn_init(rknn_context* context, void* model, uint32_t size, uint32_t flag, rknn_init_extend* extend);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rknn_init(out ulong context, byte[] model, uint size, uint flag, IntPtr extend);

    // int rknn_destroy(rknn_context context);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rknn_destroy(ulong context);

    // int rknn_query(rknn_context, rknn_query_cmd, void* info, uint32_t size);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rknn_query(ulong context, rknn_query_cmd cmd, ref rknn_input_output_num info, uint size);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rknn_query(ulong context, rknn_query_cmd cmd, ref rknn_tensor_attr info, uint size);

    // int rknn_inputs_set(rknn_context, uint32_t n_inputs, rknn_input inputs[]);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rknn_inputs_set(ulong context, uint n_inputs, rknn_input[] inputs);

    // int rknn_run(rknn_context, rknn_run_extend* extend);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rknn_run(ulong context, IntPtr extend);

    // int rknn_outputs_get(rknn_context, uint32_t n_outputs, rknn_output outputs[], rknn_output_extend* extend);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rknn_outputs_get(ulong context, uint n_outputs,
        [In, Out] rknn_output[] outputs, IntPtr extend);

    // int rknn_outputs_release(rknn_context, uint32_t n_outputs, rknn_output outputs[]);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rknn_outputs_release(ulong context, uint n_outputs, rknn_output[] outputs);

    // int rknn_set_core_mask(rknn_context, rknn_core_mask core_mask);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rknn_set_core_mask(ulong context, rknn_core_mask core_mask);
}
