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
/// Cosmetic correction (hot / cold pixel removal) on a FITS file. Reads the
/// source, runs <see cref="CosmeticCorrection"/>, and writes a sibling FITS
/// named `{stem}_cc.fits`. Mirrors the CropService path-in/path-out pattern.
/// </summary>
public class CosmeticService {
    private readonly ILogger<CosmeticService> _logger;

    public CosmeticService(ILogger<CosmeticService> logger) {
        _logger = logger;
    }

    public sealed record CosmeticResult(
        string OutputPath, int Width, int Height, int Channels, long Cold, long Hot);

    public CosmeticResult RunFits(string sourcePath,
                                  double sigmaCold = 5.0, double sigmaHot = 3.0,
                                  double amount = 1.0, bool cfa = false) {
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
        var (cold, hot) = CosmeticCorrection.Apply(pixels, w, h, channels, sigmaCold, sigmaHot, amount, cfa);

        var dst = new BaseImageData(pixels, src.Properties, src.MetaData);

        var dir = Path.GetDirectoryName(sourcePath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var outPath = Path.Combine(dir, stem + "_cc.fits");

        FITSWriter.Write(dst, outPath, customKeywords: new[] {
            new KeyValuePair<string, string>("CCSIGCLD", sigmaCold.ToString("0.###")),
            new KeyValuePair<string, string>("CCSIGHOT", sigmaHot.ToString("0.###")),
            new KeyValuePair<string, string>("CCCOLD", cold.ToString()),
            new KeyValuePair<string, string>("CCHOT", hot.ToString())
        });

        _logger.LogInformation(
            "Cosmetic: {Src} ({W}×{H} ch={Ch}) sigCold={SC} sigHot={SH} cfa={Cfa} → {Out} (cold={Cold} hot={Hot})",
            sourcePath, w, h, channels, sigmaCold, sigmaHot, cfa, outPath, cold, hot);

        return new CosmeticResult(outPath, w, h, channels, cold, hot);
    }
}
