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

using NINA.Core.Enum;
using NINA.Image.ImageAnalysis;
using NINA.Polaris.Services.Planetary;
using NINA.Polaris.Services.Studio;
using SkiaSharp;

namespace NINA.Polaris.Services.Timelapse;

/// <summary>Ordered supply of movie frames for <see cref="MediaEncodeService"/>.
/// A frame is delivered as a stretched, downscaled JPEG — the common currency
/// that both the ffmpeg MP4 path (frames written to disk verbatim) and the GIF
/// path (decoded back to an <c>SKBitmap</c>) consume, so decoding/stretching
/// happens exactly once per frame regardless of the target format.</summary>
public interface IFrameSource : IDisposable {
    int Count { get; }
    string? Instrument { get; }
    /// <summary>Render frame <paramref name="index"/> to a JPEG at most
    /// <paramref name="maxDim"/> px on its long side.</summary>
    byte[] RenderJpeg(int index, int maxDim, int quality);
}

/// <summary>A folder of still frames: FITS (auto-stretched, pinned to ONE
/// reference frame so the movie doesn't flicker) or raster (JPG/PNG/TIFF/…,
/// used as-is). The caller supplies the already-filtered, natural-sorted list
/// and the frame stride.</summary>
public sealed class FolderFrameSource : IFrameSource {
    private static readonly string[] FitsExt = { ".fits", ".fit", ".fts" };
    private readonly List<string> _files;
    private readonly string? _stretchRef;   // a FITS reference file, pins the stretch

    public FolderFrameSource(IReadOnlyList<string> files, int everyNth) {
        everyNth = Math.Max(1, everyNth);
        _files = new List<string>();
        for (int i = 0; i < files.Count; i += everyNth) _files.Add(files[i]);
        // Pin the FITS auto-stretch to a mid-sequence FITS frame: a per-file
        // stretch would rescale every frame independently and flicker.
        _stretchRef = _files.Where(IsFits)
            .Skip(_files.Count(IsFits) / 2).FirstOrDefault()
            ?? _files.FirstOrDefault(IsFits);
    }

    public int Count => _files.Count;
    public string? Instrument => null;

    private static bool IsFits(string p) => FitsExt.Contains(Path.GetExtension(p).ToLowerInvariant());

    public byte[] RenderJpeg(int index, int maxDim, int quality) {
        var f = _files[index];
        if (IsFits(f))
            return FitsThumbnailer.RenderJpegFromPath(f, maxDim, quality, stretchFromPath: _stretchRef);
        return RasterToJpeg(f, maxDim, quality);
    }

    internal static byte[] RasterToJpeg(string path, int maxDim, int quality) {
        using var src = SKBitmap.Decode(path)
            ?? throw new InvalidOperationException("Could not decode image: " + Path.GetFileName(path));
        var (w, h) = Fit(src.Width, src.Height, maxDim);
        SKBitmap scaled = (w == src.Width && h == src.Height) ? src
            : src.Resize(new SKImageInfo(w, h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        try {
            using var img = SKImage.FromBitmap(scaled);
            using var data = img.Encode(SKEncodedImageFormat.Jpeg, quality);
            return data.ToArray();
        } finally { if (!ReferenceEquals(scaled, src)) scaled.Dispose(); }
    }

    private static (int w, int h) Fit(int w, int h, int maxDim) {
        int longest = Math.Max(w, h);
        if (longest <= maxDim || maxDim <= 0) return (w, h);
        double s = (double)maxDim / longest;
        return (Math.Max(1, (int)Math.Round(w * s)), Math.Max(1, (int)Math.Round(h * s)));
    }

    public void Dispose() { }
}

/// <summary>A recorded planetary SER clip. Mono frames render grayscale, Bayer
/// frames are debayered to RGB — the same decode the planetary stacker uses.</summary>
public sealed class SerFrameSource : IFrameSource {
    private readonly SerFileReader _reader;
    private readonly BayerPatternEnum _bayer;

    public SerFrameSource(SerFileReader reader) {
        _reader = reader;
        _bayer = SerColorToBayer(reader.ColorMode);
    }

    public int Count => _reader.FrameCount;
    public string? Instrument => _reader.Instrument;

    public byte[] RenderJpeg(int index, int maxDim, int quality) {
        // ReadFrameAsUshort normalises 8/16-bit sources to the 16-bit scale.
        var px = _reader.ReadFrameAsUshort(index);
        int w = _reader.Width, h = _reader.Height;
        if (_bayer == BayerPatternEnum.None)
            return FitsThumbnailer.RenderJpegFromBuffer(px, w, h, bitDepth: 16, maxDim, quality);
        var ch = BayerDebayer.Bilinear(px, w, h, _bayer);
        int n = w * h;
        var planar = new ushort[n * 3];
        Array.Copy(ch.R, 0, planar, 0, n);
        Array.Copy(ch.G, 0, planar, n, n);
        Array.Copy(ch.B, 0, planar, n * 2, n);
        return FitsThumbnailer.RenderJpegFromRgbPlanes(planar, w, h, bitDepth: 16, maxDim, quality);
    }

    private static BayerPatternEnum SerColorToBayer(SerColorMode m) => m switch {
        SerColorMode.BayerRGGB => BayerPatternEnum.RGGB,
        SerColorMode.BayerGRBG => BayerPatternEnum.GRBG,
        SerColorMode.BayerGBRG => BayerPatternEnum.GBRG,
        SerColorMode.BayerBGGR => BayerPatternEnum.BGGR,
        _ => BayerPatternEnum.None
    };

    public void Dispose() => _reader.Dispose();
}
