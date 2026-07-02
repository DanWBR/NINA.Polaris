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
/// Generalized Hyperbolic (GHS) / asinh stretch on a linear FITS file. Reads
/// the source, applies <see cref="HyperbolicStretch"/> linked across channels
/// (colour balance preserved), and writes a sibling FITS named
/// `{stem}_ghs.fits` or `{stem}_asinh.fits`.
///
/// When <c>auto</c> is set, the stretch amount D is estimated from the image
/// median so the background lands near a target level -- the "run the sequence
/// and get a good result automatically" path. Mirrors the CropService I/O
/// pattern.
/// </summary>
public class StretchService {
    private readonly ILogger<StretchService> _logger;

    public StretchService(ILogger<StretchService> logger) {
        _logger = logger;
    }

    public sealed record StretchResult(
        string OutputPath, int Width, int Height, int Channels, double AppliedD);

    /// <summary>
    /// Read <paramref name="sourcePath"/>, apply the GHS/asinh stretch, write
    /// the sibling FITS. <paramref name="mode"/> is "ghs" or "asinh".
    /// </summary>
    public StretchResult RunFits(string sourcePath, string mode,
                                 double d = 1.0, double b = 0.0,
                                 double lp = 0.0, double sp = 0.0, double hp = 1.0, double bp = 0.0,
                                 bool auto = false, double targetBackground = 0.25) {
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
        var type = HyperbolicStretch.ParseType(mode);

        var pixels = (ushort[])src.Data.Clone();

        double appliedD = d;
        if (auto) {
            double median01 = Median01(pixels);
            appliedD = HyperbolicStretch.EstimateD(median01, targetBackground, type, b, lp, sp, hp, bp);
        }

        HyperbolicStretch.ApplyToUshort(pixels, type, b, appliedD, lp, sp, hp, bp);

        var dst = new BaseImageData(pixels, src.Properties, src.MetaData);

        var dir = Path.GetDirectoryName(sourcePath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var suffix = type == HyperbolicStretch.StretchType.Asinh ? "_asinh" : "_ghs";
        var outPath = Path.Combine(dir, stem + suffix + ".fits");

        FITSWriter.Write(dst, outPath, customKeywords: new[] {
            new KeyValuePair<string, string>("STRCHTYP", type.ToString()),
            new KeyValuePair<string, string>("STRCHD", appliedD.ToString("0.####")),
            new KeyValuePair<string, string>("STRCHB", b.ToString("0.###")),
            new KeyValuePair<string, string>("STRCHSP", sp.ToString("0.###"))
        });

        _logger.LogInformation(
            "Stretch: {Src} ({W}×{H} ch={Ch}) type={Type} D={D} B={B} auto={Auto} → {Out}",
            sourcePath, w, h, channels, type, appliedD, b, auto, outPath);

        return new StretchResult(outPath, w, h, channels, appliedD);
    }

    /// <summary>
    /// Median intensity in [0,1] via a 16-bit histogram (exact, O(n)). Used
    /// to seed the auto-D estimate.
    /// </summary>
    private static double Median01(ushort[] data) {
        if (data.Length == 0) return 0.0;
        var hist = new long[65536];
        foreach (var v in data) hist[v]++;
        long half = data.Length / 2;
        long acc = 0;
        for (int v = 0; v < 65536; v++) {
            acc += hist[v];
            if (acc >= half) return v / 65535.0;
        }
        return 0.0;
    }
}
