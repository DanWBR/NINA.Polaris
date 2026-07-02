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
/// Morphological star reduction on a FITS file. Reads the source, runs
/// <see cref="StarReduction"/>, and writes a sibling FITS named
/// `{stem}_starred.fits`. Mirrors the CropService path-in/path-out pattern.
/// </summary>
public class StarReductionService {
    private readonly ILogger<StarReductionService> _logger;

    public StarReductionService(ILogger<StarReductionService> logger) {
        _logger = logger;
    }

    public sealed record StarReductionResult(
        string OutputPath, int Width, int Height, int Channels, int StarsReduced);

    public StarReductionResult RunFits(string sourcePath,
                                       double amount = 0.5, int size = 2, bool protectCore = true) {
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

        var pixels = (ushort[])src.Data.Clone();
        int reduced = StarReduction.Apply(pixels, w, h, channels, amount, size, protectCore);

        var dst = new BaseImageData(pixels, src.Properties, src.MetaData);

        var dir = Path.GetDirectoryName(sourcePath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var outPath = Path.Combine(dir, stem + "_starred.fits");

        FITSWriter.Write(dst, outPath, customKeywords: new[] {
            new KeyValuePair<string, string>("STRDAMT", amount.ToString("0.###")),
            new KeyValuePair<string, string>("STRDSIZE", size.ToString()),
            new KeyValuePair<string, string>("STRDPROT", protectCore ? "1" : "0"),
            new KeyValuePair<string, string>("STRDNUM", reduced.ToString())
        });

        _logger.LogInformation(
            "Star reduction: {Src} ({W}×{H} ch={Ch}) amount={A} size={S} protect={P} → {Out} (stars={N})",
            sourcePath, w, h, channels, amount, size, protectCore, outPath, reduced);

        return new StarReductionResult(outPath, w, h, channels, reduced);
    }
}
