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

namespace NINA.Polaris.Services.Ncnn;

/// <summary>
/// P/Invoke bindings to ncnn's stable C API (<c>c_api.h</c>, built when
/// <c>NCNN_C_API=ON</c> — the default). All handles are opaque pointers; the C
/// API is plain <c>extern "C"</c> so the calling convention is Cdecl.
///
/// We only bind the slice needed to load a converted ncnn model
/// (<c>.param</c>+<c>.bin</c>) and run a single tensor tile on the Vulkan GPU.
/// The library is <c>libncnn.so</c> (resolved via the default search path: app
/// dir, the .deb's /usr/lib copy, LD_LIBRARY_PATH).
/// </summary>
internal static class NcnnNative {
    private const string Lib = "ncnn";

    // ─── option ─────────────────────────────────────────────────────────
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr ncnn_option_create();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ncnn_option_destroy(IntPtr opt);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ncnn_option_set_num_threads(IntPtr opt, int n);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ncnn_option_set_use_vulkan_compute(IntPtr opt, int enable);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ncnn_option_set_use_fp16_packed(IntPtr opt, int enable);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ncnn_option_set_use_fp16_storage(IntPtr opt, int enable);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ncnn_option_set_use_fp16_arithmetic(IntPtr opt, int enable);

    // ─── net ────────────────────────────────────────────────────────────
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr ncnn_net_create();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ncnn_net_destroy(IntPtr net);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ncnn_net_set_option(IntPtr net, IntPtr opt);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int ncnn_net_load_param(IntPtr net, string path);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int ncnn_net_load_model(IntPtr net, string path);

    // ─── mat ────────────────────────────────────────────────────────────
    // create_external_3d wraps caller-owned memory (no copy, elemsize=float).
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr ncnn_mat_create_external_3d(int w, int h, int c, IntPtr data, IntPtr allocator);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ncnn_mat_destroy(IntPtr mat);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ncnn_mat_get_w(IntPtr mat);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ncnn_mat_get_h(IntPtr mat);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ncnn_mat_get_c(IntPtr mat);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr ncnn_mat_get_data(IntPtr mat);

    // ─── extractor ──────────────────────────────────────────────────────
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr ncnn_extractor_create(IntPtr net);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ncnn_extractor_destroy(IntPtr ex);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int ncnn_extractor_input(IntPtr ex, string name, IntPtr mat);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int ncnn_extractor_extract(IntPtr ex, string name, out IntPtr mat);
}
