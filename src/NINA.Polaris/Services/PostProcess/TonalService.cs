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
/// Local-contrast + highlight tonal ops on a FITS file: CLAHE
/// (<see cref="Clahe"/>) and highlight recovery (<see cref="HighlightRecovery"/>).
/// Mirrors the CropService path-in/path-out pattern; writes `{stem}_clahe.fits`
/// / `{stem}_hlrec.fits`.
/// </summary>
public class TonalService {
    private readonly ILogger<TonalService> _logger;

    public TonalService(ILogger<TonalService> logger) {
        _logger = logger;
    }

    public sealed record TonalResult(string OutputPath, int Width, int Height, int Channels);

    public TonalResult Clahe(string sourcePath, double clipLimit, int tiles) {
        var (src, w, h, ch, pixels) = Load(sourcePath);
        NINA.Image.ImageAnalysis.Clahe.Apply(pixels, w, h, ch, clipLimit, tiles);
        var outPath = Write(src, sourcePath, pixels, "_clahe", new[] {
            new KeyValuePair<string, string>("CLAHECLP", clipLimit.ToString("0.###")),
            new KeyValuePair<string, string>("CLAHETIL", tiles.ToString())
        });
        _logger.LogInformation("CLAHE: {Src} ({W}×{H} ch={Ch}) clip={C} tiles={T} → {Out}",
            sourcePath, w, h, ch, clipLimit, tiles, outPath);
        return new TonalResult(outPath, w, h, ch);
    }

    public TonalResult HighlightRecovery(string sourcePath, double knee, double strength) {
        var (src, w, h, ch, pixels) = Load(sourcePath);
        NINA.Image.ImageAnalysis.HighlightRecovery.Apply(pixels, w, h, ch, knee, strength);
        var outPath = Write(src, sourcePath, pixels, "_hlrec", new[] {
            new KeyValuePair<string, string>("HLRKNEE", knee.ToString("0.###")),
            new KeyValuePair<string, string>("HLRSTR", strength.ToString("0.###"))
        });
        _logger.LogInformation("Highlight recovery: {Src} ({W}×{H} ch={Ch}) knee={K} strength={S} → {Out}",
            sourcePath, w, h, ch, knee, strength, outPath);
        return new TonalResult(outPath, w, h, ch);
    }

    private static (BaseImageData src, int w, int h, int ch, ushort[] pixels) Load(string sourcePath) {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("sourcePath is required", nameof(sourcePath));
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Source FITS not found", sourcePath);
        BaseImageData src;
        using (var fs = File.OpenRead(sourcePath)) src = FITSReader.Read(fs);
        int ch = src.Properties.Channels == 3 ? 3 : 1;
        return (src, src.Properties.Width, src.Properties.Height, ch, (ushort[])src.Data.Clone());
    }

    private static string Write(BaseImageData src, string sourcePath, ushort[] pixels,
                                string suffix, KeyValuePair<string, string>[] kw) {
        var dst = new BaseImageData(pixels, src.Properties, src.MetaData);
        var dir = Path.GetDirectoryName(sourcePath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var outPath = Path.Combine(dir, stem + suffix + ".fits");
        FITSWriter.Write(dst, outPath, customKeywords: kw);
        return outPath;
    }
}
