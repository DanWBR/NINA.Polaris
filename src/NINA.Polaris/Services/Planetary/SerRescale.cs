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

namespace NINA.Polaris.Services.Planetary;

/// <summary>
/// Salvages a planetary SER recorded before the RAW16 left-align fix, where a
/// native 12/14-bit readout was written RIGHT-aligned (values 0..4095 for a
/// 12-bit sensor) into a SER whose header claims 16-bit. ZWO/FireCapture tools
/// expect a 16-bit SER to FILL the container, so they hard-stretch near-black
/// data into a bright colour cast. This rewrites the clip left-aligned so the
/// samples occupy the full 16-bit range, matching the convention those tools
/// (and the fixed recorder) use.
///
/// The significant depth is auto-detected from the brightest sample across a
/// sampling of frames and rounded up to the nearest common ADC depth
/// (8/10/12/14/16), or supplied explicitly. A file whose data already reaches
/// the top of the 16-bit range needs no shift and is left untouched.
/// </summary>
public static class SerRescale {
    public readonly record struct Result(
        bool Done, string? OutputPath, int SignificantBits, int Shift,
        int FrameCount, string Message);

    /// <summary>Common astro-camera ADC depths, used to round a detected bit
    /// length up so a faint clip (whose brightest pixel never reached the ADC
    /// ceiling) is still recognised as e.g. 12-bit rather than 11-bit.</summary>
    private static readonly int[] CommonDepths = { 8, 10, 12, 14, 16 };

    /// <param name="srcPath">Existing SER to salvage.</param>
    /// <param name="bitsOverride">Significant ADC depth (8..16). Null =
    /// auto-detect.</param>
    /// <param name="outPath">Destination. Null = "{name}-fixed16.ser" beside
    /// the source.</param>
    public static Result Rescale(string srcPath, int? bitsOverride, string? outPath) {
        if (string.IsNullOrWhiteSpace(srcPath) || !File.Exists(srcPath))
            throw new FileNotFoundException("SER not found", srcPath);

        using var reader = new SerFileReader(srcPath);

        if (reader.FrameCount <= 0)
            return new Result(false, null, 16, 0, 0, "The SER has no frames.");
        if (reader.ColorMode is SerColorMode.Rgb or SerColorMode.Bgr)
            return new Result(false, null, 16, 0, reader.FrameCount,
                "This SER is already RGB; rescaling only applies to raw mono/Bayer clips.");
        if (reader.BitDepth != 16)
            return new Result(false, null, reader.BitDepth, 0, reader.FrameCount,
                $"This SER is {reader.BitDepth}-bit; only 16-bit raw clips can be right-aligned by mistake.");

        int significantBits;
        if (bitsOverride is int b) {
            if (b is < 8 or > 16) throw new ArgumentOutOfRangeException(nameof(bitsOverride), "bits must be 8..16");
            significantBits = b;
        } else {
            significantBits = DetectSignificantBits(reader);
        }

        int shift = Math.Max(0, 16 - significantBits);
        if (shift == 0)
            return new Result(false, null, significantBits, 0, reader.FrameCount,
                "The samples already fill the 16-bit range; nothing to rescale.");

        var dst = string.IsNullOrWhiteSpace(outPath)
            ? DefaultOutputPath(srcPath)
            : outPath!;
        if (string.Equals(Path.GetFullPath(dst), Path.GetFullPath(srcPath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Output path must differ from the source.");

        using (var writer = new SerFileWriter(dst, reader.Width, reader.Height, 16,
                   reader.ColorMode, reader.Observer, reader.Instrument, reader.Telescope)) {
            for (int i = 0; i < reader.FrameCount; i++) {
                var px = reader.ReadFrameAsUshort(i);
                for (int j = 0; j < px.Length; j++) {
                    int x = px[j] << shift;
                    px[j] = (ushort)(x > 0xFFFF ? 0xFFFF : x);   // saturate, never wrap bright pixels dark
                }
                writer.WriteFrame(px, reader.TimestampOf(i));
            }
        }

        return new Result(true, dst, significantBits, shift, reader.FrameCount,
            $"Rescaled {reader.FrameCount} frames from {significantBits}-bit to full 16-bit range.");
    }

    /// <summary>Brightest sample across an even sampling of frames, rounded up
    /// to the nearest common ADC depth.</summary>
    private static int DetectSignificantBits(SerFileReader reader) {
        int sampleCount = Math.Min(reader.FrameCount, 30);
        int step = Math.Max(1, reader.FrameCount / sampleCount);
        ushort max = 0;
        for (int i = 0; i < reader.FrameCount; i += step) {
            var px = reader.ReadFrameAsUshort(i);
            for (int j = 0; j < px.Length; j++) if (px[j] > max) max = px[j];
            if (max >= 0xF000) break;   // clearly already fills the range, stop early
        }
        if (max == 0) return 16;        // black file: no shift, treated as no-op upstream
        int bitLen = 0;
        for (int v = max; v > 0; v >>= 1) bitLen++;
        foreach (var d in CommonDepths) if (d >= bitLen) return d;
        return 16;
    }

    private static string DefaultOutputPath(string srcPath) {
        var dir = Path.GetDirectoryName(srcPath) ?? ".";
        var name = Path.GetFileNameWithoutExtension(srcPath);
        return Path.Combine(dir, name + "-fixed16.ser");
    }
}
