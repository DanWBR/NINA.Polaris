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

using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;

namespace NINA.Polaris.Services.PostProcess;

/// <summary>
/// SCNR (Subtractive Chromatic Noise Reduction) on a FITS file. Reads the
/// source, removes the residual green cast via <see cref="Scnr"/>, and writes
/// a sibling FITS named `{stem}_scnr.fits`. Mono input is a no-op passthrough
/// (SCNR needs three colour planes).
///
/// Pure I/O + math — no SkiaSharp, ONNX, or external binary. Mirrors the
/// <see cref="CropService"/> path-in/path-out pattern so the Auto Workflow
/// runner and the Files toolbar can drive it as a plain FITS→FITS step.
/// </summary>
public class ScnrService {
    private readonly ILogger<ScnrService> _logger;

    public ScnrService(ILogger<ScnrService> logger) {
        _logger = logger;
    }

    public sealed record ScnrResult(
        string OutputPath, int Width, int Height, int Channels, long PixelsChanged);

    /// <summary>
    /// Read <paramref name="sourcePath"/>, apply SCNR with the given mode /
    /// amount / lightness-preserve, write `{stem}_scnr.fits` next to the
    /// source. <paramref name="mode"/> is a case-insensitive name
    /// (average-neutral | maximum-neutral | maximum-mask | additive-mask).
    /// </summary>
    public ScnrResult RunFits(string sourcePath, string mode,
                              double amount = 1.0, bool preserveLightness = false) {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("sourcePath is required", nameof(sourcePath));
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Source FITS not found", sourcePath);

        BaseImageData src;
        using (var fs = File.OpenRead(sourcePath)) {
            src = FITSReader.Read(fs);
        }

        int w = src.Properties.Width;
        int h = src.Properties.Height;
        int channels = src.Properties.Channels == 3 ? 3 : 1;
        var scnrMode = Scnr.ParseMode(mode);

        // Copy the buffer so the in-place transform never mutates a shared
        // read cache; SCNR is a no-op on mono, in which case the copy is
        // just written back verbatim (still gives the user a sibling file).
        var pixels = (ushort[])src.Data.Clone();
        long changed = Scnr.Apply(pixels, w, h, channels, scnrMode, amount, preserveLightness);

        var dst = new BaseImageData(pixels, src.Properties, src.MetaData);

        var dir = Path.GetDirectoryName(sourcePath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var outPath = Path.Combine(dir, stem + "_scnr.fits");

        FITSWriter.Write(dst, outPath, customKeywords: new[] {
            new KeyValuePair<string, string>("SCNRMODE", scnrMode.ToString()),
            new KeyValuePair<string, string>("SCNRAMT", amount.ToString("0.###")),
            new KeyValuePair<string, string>("SCNRPRSV", preserveLightness ? "1" : "0")
        });

        _logger.LogInformation(
            "SCNR: {Src} ({W}×{H} ch={Ch}) mode={Mode} amt={Amt} preserve={P} → {Out} (changed={Changed})",
            sourcePath, w, h, channels, scnrMode, amount, preserveLightness, outPath, changed);

        return new ScnrResult(outPath, w, h, channels, changed);
    }
}
