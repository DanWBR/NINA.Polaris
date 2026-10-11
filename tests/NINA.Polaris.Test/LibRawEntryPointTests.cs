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

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Every libraw function the DSLR decoder imports has to exist.
///
/// <para>It imported <c>libraw_set_user_flip</c>, which is in no libraw's C
/// API. The call threw EntryPointNotFoundException on every frame, the catch
/// reported a plain "could not decode", and the operator was told the camera
/// was probably newer than libraw. No DSLR RAW was ever decoded: a Canon R8
/// in the field ran on its 8-bit preview JPEG while libraw 0.21.2 on the same
/// host reads that camera's CR3 perfectly well (2026-10-11).</para>
///
/// <para>The machines that run Polaris carry libraw 0.20 (Debian 12, Ubuntu
/// 22.04), 0.21 (Ubuntu 24.04) or 0.22, and the tests run where libraw is not
/// installed, so the imports are checked against the C API those three
/// versions share, taken from their libraw.h.</para>
/// </summary>
[TestFixture]
public class LibRawEntryPointTests {

    /// <summary>The extern "C" functions declared in libraw.h by 0.20.2,
    /// 0.21.2 and 0.22.0 alike.</summary>
    private static readonly HashSet<string> CommonCApi = new() {
        "libraw_adjust_sizes_info_only", "libraw_capabilities", "libraw_close",
        "libraw_dcraw_clear_mem", "libraw_dcraw_make_mem_image", "libraw_dcraw_make_mem_thumb",
        "libraw_dcraw_ppm_tiff_writer", "libraw_dcraw_process", "libraw_dcraw_thumb_writer",
        "libraw_free_image", "libraw_get_cam_mul", "libraw_get_color_maximum",
        "libraw_get_decoder_info", "libraw_get_iheight", "libraw_get_imgother",
        "libraw_get_iparams", "libraw_get_iwidth", "libraw_get_lensinfo", "libraw_get_pre_mul",
        "libraw_get_raw_height", "libraw_get_raw_width", "libraw_get_rgb_cam", "libraw_init",
        "libraw_open_buffer", "libraw_open_file", "libraw_open_file_ex", "libraw_open_wfile",
        "libraw_open_wfile_ex", "libraw_raw2image", "libraw_recycle", "libraw_recycle_datastream",
        "libraw_set_bright", "libraw_set_dataerror_handler", "libraw_set_demosaic",
        "libraw_set_exifparser_handler", "libraw_set_fbdd_noiserd", "libraw_set_gamma",
        "libraw_set_highlight", "libraw_set_no_auto_bright", "libraw_set_output_bps",
        "libraw_set_output_color", "libraw_set_output_tif", "libraw_set_progress_handler",
        "libraw_set_user_mul", "libraw_strerror", "libraw_strprogress", "libraw_subtract_black",
        "libraw_unpack", "libraw_unpack_function_name", "libraw_unpack_thumb", "libraw_version",
    };

    private static string Here([CallerFilePath] string p = "") => p;

    private static string DecoderSource() {
        var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Here())!, "..", "..",
            "src", "NINA.Image.Portable", "FileFormat", "Raw", "LibRawDecoder.cs"));
        Assert.That(File.Exists(path), $"missing {path}");
        return File.ReadAllText(path);
    }

    [Test]
    public void EveryImportExistsInLibrawsCApi() {
        var imports = Regex.Matches(DecoderSource(), @"EntryPoint\s*=\s*""(\w+)""")
            .Select(m => m.Groups[1].Value).ToList();
        Assert.That(imports, Is.Not.Empty, "no DllImport found; did the decoder move?");
        var missing = imports.Where(e => !CommonCApi.Contains(e)).ToList();
        Assert.That(missing, Is.Empty,
            "not in libraw's C API (0.20 to 0.22), so the call throws on every frame: "
            + string.Join(", ", missing));
    }

    [Test]
    public void TheOrientationSetterIsGone() {
        Assert.That(DecoderSource(), Does.Not.Match(@"EntryPoint\s*=\s*""libraw_set_user_flip"""));
    }
}
