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
using NINA.Polaris.Services.Studio;

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

    /// <summary>WAVE-2: per-layer sharpen, the RegiStax model. One gain per
    /// wavelet scale (finest first) plus an optional denoise threshold per
    /// scale, in units of that layer's own noise sigma.</summary>
    public WaveletResult SharpenLayers(string sourcePath, double[] gains, double[]? denoise) {
        if (gains == null || gains.Length == 0)
            throw new ArgumentException("gains is required (one value per wavelet scale)", nameof(gains));
        var (src, w, h, ch, pixels) = Load(sourcePath);
        WaveletSharpen.ApplyLayers(pixels, w, h, ch, gains, denoise);
        // Stamp the sliders into the header. A wavelet result cannot be
        // reverse-engineered from the pixels, and the operator will want the
        // settings that worked on last month's Jupiter.
        var kw = new List<KeyValuePair<string, string>> {
            new("WSHLAY", gains.Length.ToString())
        };
        for (int j = 0; j < gains.Length && j < 8; j++) {
            kw.Add(new($"WSHG{j + 1}", gains[j].ToString("0.###")));
            if (denoise != null && j < denoise.Length && denoise[j] > 0)
                kw.Add(new($"WSHN{j + 1}", denoise[j].ToString("0.###")));
        }
        var outPath = Write(src, sourcePath, pixels, "_wsharp", kw.ToArray());
        _logger.LogInformation("Wavelet layers: {Src} ({W}×{H} ch={Ch}) gains=[{G}] → {Out}",
            sourcePath, w, h, ch, string.Join(", ", gains.Select(g => g.ToString("0.##"))), outPath);
        return new WaveletResult(outPath, w, h, ch);
    }

    /// <summary>WAVE-3: the same maths rendered straight to a JPEG at preview
    /// size instead of written as a FITS.
    ///
    /// <para>Wavelet tuning is a dialogue: drag, look, drag again. A
    /// full-resolution round trip per drag is unusable on a 20 MP master, so
    /// the modal previews on a DOWNSCALED copy and only Apply writes a file.
    /// The transform runs before the downscale so the preview shows the real
    /// per-layer effect rather than the effect of resampling it.</para>
    /// </summary>
    public byte[] PreviewLayers(string sourcePath, double[] gains, double[]? denoise,
                                int maxDim = 900, int quality = 85) {
        var (src, w, h, ch, pixels) = Load(sourcePath);
        if (gains is { Length: > 0 })
            WaveletSharpen.ApplyLayers(pixels, w, h, ch, gains, denoise);
        return ch == 3
            ? FitsThumbnailer.RenderJpegFromRgbPlanes(pixels, w, h, src.Properties.BitDepth,
                                                      maxDim, quality)
            : FitsThumbnailer.RenderJpegFromBuffer(pixels, w, h, src.Properties.BitDepth,
                                                   maxDim, quality);
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
