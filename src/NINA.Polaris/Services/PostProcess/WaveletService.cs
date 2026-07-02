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
/// Multiscale (à-trous wavelet) post-processing on a FITS file: wavelet
/// sharpen/denoise (<see cref="WaveletSharpen"/>) and multiscale HDR core
/// recovery (<see cref="WaveScaleHdr"/>). Mirrors the CropService
/// path-in/path-out pattern; writes `{stem}_wsharp.fits` / `{stem}_wshdr.fits`.
/// </summary>
public class WaveletService {
    private readonly ILogger<WaveletService> _logger;

    public WaveletService(ILogger<WaveletService> logger) {
        _logger = logger;
    }

    public sealed record WaveletResult(string OutputPath, int Width, int Height, int Channels);

    public WaveletResult Sharpen(string sourcePath, double detail, double denoise, int scales) {
        var (src, w, h, ch, pixels) = Load(sourcePath);
        WaveletSharpen.Apply(pixels, w, h, ch, detail, denoise, scales);
        var outPath = Write(src, sourcePath, pixels, "_wsharp", new[] {
            new KeyValuePair<string, string>("WSHDET", detail.ToString("0.###")),
            new KeyValuePair<string, string>("WSHDEN", denoise.ToString("0.###")),
            new KeyValuePair<string, string>("WSHSC", scales.ToString())
        });
        _logger.LogInformation("Wavelet sharpen: {Src} ({W}×{H} ch={Ch}) detail={D} denoise={N} scales={S} → {Out}",
            sourcePath, w, h, ch, detail, denoise, scales, outPath);
        return new WaveletResult(outPath, w, h, ch);
    }

    public WaveletResult Hdr(string sourcePath, double amount, int scales) {
        var (src, w, h, ch, pixels) = Load(sourcePath);
        WaveScaleHdr.Apply(pixels, w, h, ch, amount, scales);
        var outPath = Write(src, sourcePath, pixels, "_wshdr", new[] {
            new KeyValuePair<string, string>("WHDAMT", amount.ToString("0.###")),
            new KeyValuePair<string, string>("WHDSC", scales.ToString())
        });
        _logger.LogInformation("WaveScale HDR: {Src} ({W}×{H} ch={Ch}) amount={A} scales={S} → {Out}",
            sourcePath, w, h, ch, amount, scales, outPath);
        return new WaveletResult(outPath, w, h, ch);
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
