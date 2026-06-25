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

using System;
using System.Runtime.InteropServices;

namespace NINA.Image.FileFormat.Raw;

/// <summary>
/// Decodes a camera-native RAW (CR2/NEF/ARW/…) into a real 16-bit linear image
/// via the system <c>libraw</c> shared library (the same engine dcraw/RawTherapee
/// use). Used for DSLR/mirrorless frames coming through indi_gphoto in
/// FORMAT_NATIVE: it gives the true sensor data (14-bit linear, demosaiced by
/// LibRaw's AHD) instead of the 8-bit gamma-encoded embedded JPEG, so the live
/// stack works on real data.
///
/// Implementation notes:
/// - Uses ONLY LibRaw's C setter functions + <c>dcraw_make_mem_image</c>, never
///   touches the <c>libraw_data_t</c> struct, so there is no version-fragile
///   field marshaling and a layout mismatch can't crash the process.
/// - The result is interleaved 16-bit RGB; the caller re-mosaics it to a Bayer
///   grid so it flows through Polaris's existing mono+Bayer relay/stack.
/// - Entirely optional: <see cref="IsAvailable"/> is false when libraw isn't
///   installed, and every call is wrapped so the caller falls back to the
///   embedded-JPEG path.
/// </summary>
public static class LibRawDecoder {
    // libraw is distributed as libraw.so.<soname> (e.g. .23/.20); the dev
    // symlink libraw.so may be absent on a runtime-only box. Resolve across the
    // common sonames so a plain runtime install still works.
    private const string Lib = "raw";
    private static readonly string[] Candidates = {
        "libraw.so", "libraw.so.23", "libraw.so.22", "libraw.so.21",
        "libraw.so.20", "libraw.so.19", "raw"
    };

    static LibRawDecoder() {
        try {
            NativeLibrary.SetDllImportResolver(typeof(LibRawDecoder).Assembly, (name, asm, path) => {
                if (name != Lib) return IntPtr.Zero;
                foreach (var c in Candidates)
                    if (NativeLibrary.TryLoad(c, out var h)) return h;
                return IntPtr.Zero;
            });
        } catch { /* resolver already set / not supported — DllImport still tries */ }
    }

    [DllImport(Lib, EntryPoint = "libraw_init")] private static extern IntPtr libraw_init(uint flags);
    [DllImport(Lib, EntryPoint = "libraw_close")] private static extern void libraw_close(IntPtr p);
    [DllImport(Lib, EntryPoint = "libraw_open_buffer")] private static extern int libraw_open_buffer(IntPtr p, byte[] buf, UIntPtr size);
    [DllImport(Lib, EntryPoint = "libraw_unpack")] private static extern int libraw_unpack(IntPtr p);
    [DllImport(Lib, EntryPoint = "libraw_dcraw_process")] private static extern int libraw_dcraw_process(IntPtr p);
    [DllImport(Lib, EntryPoint = "libraw_dcraw_make_mem_image")] private static extern IntPtr libraw_dcraw_make_mem_image(IntPtr p, out int errc);
    [DllImport(Lib, EntryPoint = "libraw_dcraw_clear_mem")] private static extern void libraw_dcraw_clear_mem(IntPtr img);
    // Stable setter helpers (libraw C API ≥0.18) — avoid struct marshaling.
    [DllImport(Lib, EntryPoint = "libraw_set_output_bps")] private static extern void libraw_set_output_bps(IntPtr p, int value);
    [DllImport(Lib, EntryPoint = "libraw_set_output_color")] private static extern void libraw_set_output_color(IntPtr p, int value);
    [DllImport(Lib, EntryPoint = "libraw_set_no_auto_bright")] private static extern void libraw_set_no_auto_bright(IntPtr p, int value);
    [DllImport(Lib, EntryPoint = "libraw_set_gamma")] private static extern void libraw_set_gamma(IntPtr p, int index, float value);
    [DllImport(Lib, EntryPoint = "libraw_set_user_flip")] private static extern void libraw_set_user_flip(IntPtr p, int value);

    // libraw_processed_image_t header: int type; ushort height,width,colors,bits;
    // uint data_size; (then the pixel bytes). 16-byte header on LP64/ILP32.
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessedImageHeader {
        public int Type;
        public ushort Height;
        public ushort Width;
        public ushort Colors;
        public ushort Bits;
        public uint DataSize;
    }

    private static bool? _available;

    /// <summary>True when libraw can be loaded + initialised on this host.</summary>
    public static bool IsAvailable {
        get {
            if (_available.HasValue) return _available.Value;
            try {
                var h = libraw_init(0);
                if (h != IntPtr.Zero) { libraw_close(h); _available = true; }
                else _available = false;
            } catch { _available = false; }
            return _available.Value;
        }
    }

    /// <summary>
    /// Decode a RAW file to a 16-bit linear RGGB Bayer mosaic so it flows through
    /// the mono+Bayer pipeline (the client debayers it back to colour). Returns
    /// false (and null outputs) if libraw is unavailable or the decode fails —
    /// the caller should fall back to the embedded JPEG.
    /// </summary>
    public static bool TryDecodeToRggb(byte[] rawBytes, out int width, out int height, out ushort[]? mosaic) {
        width = 0; height = 0; mosaic = null;
        if (!IsAvailable || rawBytes == null || rawBytes.Length == 0) return false;

        IntPtr lr = IntPtr.Zero, img = IntPtr.Zero;
        try {
            lr = libraw_init(0);
            if (lr == IntPtr.Zero) return false;

            if (libraw_open_buffer(lr, rawBytes, (UIntPtr)rawBytes.Length) != 0) return false;
            if (libraw_unpack(lr) != 0) return false;

            // Astro-friendly output: 16-bit, linear (gamma 1.0), no auto-bright,
            // sRGB primaries + camera white balance (defaults give a natural
            // preview that the auto-stretch handles), no orientation flip.
            libraw_set_output_bps(lr, 16);
            libraw_set_output_color(lr, 1);   // sRGB
            libraw_set_no_auto_bright(lr, 1);
            libraw_set_gamma(lr, 0, 1.0f);
            libraw_set_gamma(lr, 1, 1.0f);
            libraw_set_user_flip(lr, 0);

            if (libraw_dcraw_process(lr) != 0) return false;
            img = libraw_dcraw_make_mem_image(lr, out _);
            if (img == IntPtr.Zero) return false;

            var hdr = Marshal.PtrToStructure<ProcessedImageHeader>(img);
            int w = hdr.Width, h = hdr.Height;
            if (w <= 0 || h <= 0 || hdr.Colors != 3 || hdr.Bits != 16) return false;
            long expected = (long)w * h * 3 * 2;
            if (hdr.DataSize < expected) return false;

            // Pixel data starts right after the 16-byte header.
            IntPtr data = IntPtr.Add(img, Marshal.SizeOf<ProcessedImageHeader>());
            var rgb = new ushort[(long)w * h * 3];
            // Marshal.Copy has no ushort[] overload from IntPtr in older TFMs;
            // copy as bytes then reinterpret is simplest + portable.
            var bytes = new byte[expected];
            Marshal.Copy(data, bytes, 0, (int)expected);
            Buffer.BlockCopy(bytes, 0, rgb, 0, (int)expected);

            // Re-mosaic interleaved RGB16 → RGGB Bayer (matches the JPEG path so
            // the rest of Polaris treats DSLR frames identically).
            var bayer = new ushort[(long)w * h];
            for (int y = 0; y < h; y++) {
                int row = y * w;
                bool evenRow = (y & 1) == 0;
                for (int x = 0; x < w; x++) {
                    int p = (row + x) * 3;
                    bool evenCol = (x & 1) == 0;
                    bayer[row + x] = evenRow ? (evenCol ? rgb[p] : rgb[p + 1])
                                             : (evenCol ? rgb[p + 1] : rgb[p + 2]);
                }
            }
            width = w; height = h; mosaic = bayer;
            return true;
        } catch {
            return false;
        } finally {
            if (img != IntPtr.Zero) { try { libraw_dcraw_clear_mem(img); } catch { } }
            if (lr != IntPtr.Zero) { try { libraw_close(lr); } catch { } }
        }
    }
}
