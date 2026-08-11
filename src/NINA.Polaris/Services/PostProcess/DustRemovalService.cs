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

using System.Globalization;
using NINA.Core.Enum;
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Polaris.Services.Studio;

namespace NINA.Polaris.Services.PostProcess;

/// <summary>
/// Dust-mote removal on a FITS file: detect the soft circular shadows a dust
/// speck casts on the sky and divide them out with a local synthetic flat
/// (<see cref="DustMoteRemoval"/>). Mirrors <see cref="WaveletService"/>'s
/// path-in/path-out shape: a debounced <see cref="Preview"/> renders a
/// downscaled before/after JPEG plus the detected mote geometry, and
/// <see cref="Remove"/> writes `{stem}_dustfix.fits` next to the source with
/// the original headers preserved.
/// </summary>
public class DustRemovalService {
    private readonly ILogger<DustRemovalService> _logger;

    public DustRemovalService(ILogger<DustRemovalService> logger) {
        _logger = logger;
    }

    public sealed record DustResult(string OutputPath, int Width, int Height, int Channels, int Count);
    public sealed record MoteDto(double X, double Y, double R);
    /// <summary>Preview payload: two same-stretch JPEGs (base64, no data-URI
    /// prefix) at working size, the detected motes in working-pixel coords, and
    /// the working dimensions so the client can overlay circles.</summary>
    public sealed record DustPreview(string Jpeg, string Original, int Width, int Height,
                                     int Count, IReadOnlyList<MoteDto> Motes);

    /// <summary>Detect + correct at preview scale and return before/after JPEGs
    /// pinned to the SAME auto-stretch. The dust dip is only ~1–2%, so letting
    /// each side auto-stretch independently would hide the very change the
    /// operator is judging.</summary>
    public DustPreview Preview(string sourcePath, DustMoteRemoval.Params p,
                               int maxDim = 1024, int quality = 85) {
        var (src, w, h, ch, pixels) = Load(sourcePath);
        // Keep the working size the preview computes on and the JPEG size in
        // lockstep so the mote coordinates land on the rendered pixels.
        var pp = p with { WorkingLongSide = System.Math.Clamp(maxDim, 256, 1600) };
        var plan = DustMoteRemoval.Analyze(pixels, w, h, ch, pp);

        int pw = plan.WorkWidth, ph = plan.WorkHeight;
        int longSide = System.Math.Max(pw, ph);
        int bits = src.Properties.BitDepth;
        var orig = DustMoteRemoval.PackWorkingOriginal(plan);
        var corr = DustMoteRemoval.PackWorkingCorrected(plan);

        string origB64, corrB64;
        if (ch == 3) {
            // Pin both renders to the ORIGINAL's per-channel stretch.
            var sp = new AutoStretch.StretchParams[3];
            var one = new ushort[pw * ph];
            for (int c = 0; c < 3; c++) {
                System.Array.Copy(orig, c * pw * ph, one, 0, pw * ph);
                sp[c] = AutoStretch.ComputeAutoStretchParams(one, pw, ph, bits);
            }
            origB64 = System.Convert.ToBase64String(
                FitsThumbnailer.RenderJpegFromRgbPlanes(orig, pw, ph, bits, longSide, quality, sp));
            corrB64 = System.Convert.ToBase64String(
                FitsThumbnailer.RenderJpegFromRgbPlanes(corr, pw, ph, bits, longSide, quality, sp));
        } else {
            var sp = AutoStretch.ComputeAutoStretchParams(orig, pw, ph, bits);
            origB64 = System.Convert.ToBase64String(
                FitsThumbnailer.RenderJpegFromBuffer(orig, pw, ph, bits, longSide, quality, sp));
            corrB64 = System.Convert.ToBase64String(
                FitsThumbnailer.RenderJpegFromBuffer(corr, pw, ph, bits, longSide, quality, sp));
        }

        var motes = new List<MoteDto>(plan.Motes.Count);
        foreach (var m in plan.Motes) motes.Add(new MoteDto(m.X, m.Y, m.R));
        return new DustPreview(corrB64, origB64, pw, ph, motes.Count, motes);
    }

    /// <summary>Full-resolution correction, written as a sibling FITS with the
    /// source headers preserved and the settings stamped in.</summary>
    public DustResult Remove(string sourcePath, DustMoteRemoval.Params p) {
        var (src, w, h, ch, pixels) = Load(sourcePath);
        var plan = DustMoteRemoval.Analyze(pixels, w, h, ch, p);
        var outData = DustMoteRemoval.ApplyFull(pixels, w, h, ch, plan);

        var inv = CultureInfo.InvariantCulture;
        var kw = new List<KeyValuePair<string, string>> {
            new("DUSTMOT", "T"),
            new("DMCOUNT", plan.Motes.Count.ToString(inv)),
            new("DMSENS",  p.SensitivityPct.ToString("0.###", inv)),
            new("DMMINSZ", p.MinSizePct.ToString("0.###", inv)),
            new("DMFEATH", p.FeatherPct.ToString("0.###", inv)),
            new("DMSTREN", p.StrengthPct.ToString("0.###", inv)),
        };
        var outPath = Write(src, sourcePath, outData, "_dustfix", kw.ToArray());
        _logger.LogInformation("Dust removal: {Src} ({W}×{H} ch={Ch}) removed {N} mote(s) → {Out}",
            sourcePath, w, h, ch, plan.Motes.Count, outPath);
        return new DustResult(outPath, w, h, ch, plan.Motes.Count);
    }

    /// <summary>Read the FITS, debayering a CFA mosaic to RGB first (a mote is a
    /// smooth shadow, but detecting it over a raw Bayer grid would fold the
    /// pattern into the background estimate). Returns plane-sequential pixels.</summary>
    private static (BaseImageData src, int w, int h, int ch, ushort[] pixels) Load(string sourcePath) {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("sourcePath is required", nameof(sourcePath));
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Source FITS not found", sourcePath);
        BaseImageData src;
        using (var fs = File.OpenRead(sourcePath)) src = FITSReader.Read(fs);

        int w = src.Properties.Width, h = src.Properties.Height;
        int ch = src.Properties.Channels == 3 ? 3 : 1;

        var bayer = src.Properties.BayerPattern != BayerPatternEnum.None
            ? src.Properties.BayerPattern
            : src.MetaData.Camera.BayerPattern;
        if (ch == 1 && bayer != BayerPatternEnum.None && bayer != BayerPatternEnum.Auto) {
            var c = BayerDebayer.Bilinear(src.Data, w, h, bayer);
            int n = w * h;
            var planar = new ushort[n * 3];
            Array.Copy(c.R, 0, planar, 0, n);
            Array.Copy(c.G, 0, planar, n, n);
            Array.Copy(c.B, 0, planar, n * 2, n);
            var props = new ImageProperties {
                Width = w, Height = h, BitDepth = src.Properties.BitDepth,
                Channels = 3, IsBayered = false, BayerPattern = BayerPatternEnum.None
            };
            var meta = src.MetaData;
            meta.Camera.BayerPattern = BayerPatternEnum.None;
            return (new BaseImageData(planar, props, meta), w, h, 3, planar);
        }

        return (src, w, h, ch, (ushort[])src.Data.Clone());
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
