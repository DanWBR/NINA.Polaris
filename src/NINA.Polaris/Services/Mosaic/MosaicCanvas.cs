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
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageAnalysis;

namespace NINA.Polaris.Services.Mosaic;

/// <summary>
/// One captured panel, reduced to roughly the canvas resolution and with its
/// sky background removed, ready to be composited.
///
/// <para>The WCS stays in the ORIGINAL frame's pixel coordinates (what the
/// solver reported); <see cref="Bin"/> converts to the reduced grid at
/// sampling time, so nothing has to rewrite the solve.</para>
/// </summary>
public sealed class MosaicTile {
    public int Width { get; }
    public int Height { get; }
    public int Bin { get; }
    public float[] R { get; }
    public float[] G { get; }
    public float[] B { get; }
    public WcsInfo Wcs { get; }
    public bool IsColour { get; }

    private MosaicTile(int w, int h, int bin, float[] r, float[] g, float[] b, WcsInfo wcs, bool colour) {
        Width = w; Height = h; Bin = bin; R = r; G = g; B = b; Wcs = wcs; IsColour = colour;
    }

    /// <summary>Reduce a raw frame for the canvas.</summary>
    /// <param name="pattern">Bayer pattern of a colour sensor, or None for mono.</param>
    /// <param name="reduction">Wanted frame pixels per canvas pixel. A colour
    /// frame is binned by an even factor of at least 2 (a superpixel
    /// debayer: each 2x2 cell gives one RGB pixel, no interpolation).</param>
    public static MosaicTile FromFrame(ushort[] data, int width, int height,
            BayerPatternEnum pattern, WcsInfo wcs, double reduction) {
        bool colour = pattern is not (BayerPatternEnum.None or BayerPatternEnum.Auto);
        reduction += 1e-6;   // a 2.0 ratio computed from CD matrices comes out as 1.9999...
        int bin = colour
            ? Math.Max(2, 2 * (int)Math.Floor(reduction / 2))
            : Math.Max(1, (int)Math.Floor(reduction));
        int w = width / bin, h = height / bin;
        if (w < 2 || h < 2) throw new ArgumentException("Frame too small for the canvas");

        var r = new float[w * h];
        var g = colour ? new float[w * h] : r;
        var b = colour ? new float[w * h] : r;
        int[] block = colour ? ColourBlock(pattern) : new[] { 0, 0, 0, 0 };

        Parallel.For(0, h, y => {
            for (int x = 0; x < w; x++) {
                double sr = 0, sg = 0, sb = 0;
                int nr = 0, ng = 0, nb = 0;
                for (int dy = 0; dy < bin; dy++) {
                    int sy = y * bin + dy;
                    int row = sy * width;
                    int rowBase = (sy & 1) << 1;
                    for (int dx = 0; dx < bin; dx++) {
                        int sx = x * bin + dx;
                        ushort v = data[row + sx];
                        switch (block[rowBase + (sx & 1)]) {
                            case 0: sr += v; nr++; break;
                            case 1: sg += v; ng++; break;
                            default: sb += v; nb++; break;
                        }
                    }
                }
                int i = y * w + x;
                r[i] = (float)(sr / Math.Max(1, nr));
                if (colour) {
                    g[i] = (float)(sg / Math.Max(1, ng));
                    b[i] = (float)(sb / Math.Max(1, nb));
                }
            }
        });

        // Sky to zero per channel. Each panel then lands at the same level as
        // its neighbours whatever the sky glow was where it pointed, which is
        // what keeps the seams from showing as steps.
        SubtractBackground(r);
        if (colour) { SubtractBackground(g); SubtractBackground(b); }

        return new MosaicTile(w, h, bin, r, g, b, wcs, colour);
    }

    // Layout: index 0 = (x0,y0), 1 = (x1,y0), 2 = (x0,y1), 3 = (x1,y1);
    // 0 = R, 1 = G, 2 = B. Same table as BayerDebayer.
    private static int[] ColourBlock(BayerPatternEnum pattern) => pattern switch {
        BayerPatternEnum.RGGB => new[] { 0, 1, 1, 2 },
        BayerPatternEnum.GRBG => new[] { 1, 0, 2, 1 },
        BayerPatternEnum.GBRG => new[] { 1, 2, 0, 1 },
        BayerPatternEnum.BGGR => new[] { 2, 1, 1, 0 },
        _ => throw new ArgumentException($"Unsupported pattern {pattern}")
    };

    private static void SubtractBackground(float[] plane) {
        float median = MosaicCanvas.SampledMedian(plane, null);
        for (int i = 0; i < plane.Length; i++) plane[i] -= median;
    }

    /// <summary>Bilinear sample at a 0-based position on the reduced grid.</summary>
    internal void Sample(double x, double y, out float r, out float g, out float b) {
        int x0 = (int)x, y0 = (int)y;
        if (x0 >= Width - 1) x0 = Width - 2;
        if (y0 >= Height - 1) y0 = Height - 2;
        float fx = (float)(x - x0), fy = (float)(y - y0);
        int i = y0 * Width + x0;
        r = Lerp(R, i, fx, fy);
        if (IsColour) { g = Lerp(G, i, fx, fy); b = Lerp(B, i, fx, fy); } else { g = r; b = r; }
    }

    private float Lerp(float[] p, int i, float fx, float fy) {
        float top = p[i] + (p[i + 1] - p[i]) * fx;
        float bot = p[i + Width] + (p[i + Width + 1] - p[i + Width]) * fx;
        return top + (bot - top) * fy;
    }
}

/// <summary>
/// The composited mosaic at screen resolution: a weighted sum of every panel,
/// each placed by its own plate solve.
///
/// <para>Placement comes from each panel's WCS, never from matching stars
/// between panels: neighbours share only the overlap strip, too little for a
/// reliable star match, while the solve pins every pixel to the sky on its
/// own. It also absorbs field rotation (alt-az mounts) and a meridian flip,
/// because the rotation is in each panel's CD matrix.</para>
///
/// <para>Overlaps are blended with a feather: a panel's weight ramps from 0 at
/// its edge to 1 a short way in, so the transition from one panel to the next
/// is gradual instead of a hard line.</para>
/// </summary>
public sealed class MosaicCanvas {
    public int Width { get; }
    public int Height { get; }
    public WcsInfo Wcs { get; }
    public int TileCount { get; private set; }

    /// <summary>Feather width as a fraction of the panel's shorter side.</summary>
    public const double FeatherFraction = 0.08;

    private readonly float[] _r, _g, _b, _w;
    private readonly object _gate = new();

    public MosaicCanvas(int width, int height, WcsInfo wcs) {
        Width = width; Height = height; Wcs = wcs;
        int n = width * height;
        _r = new float[n]; _g = new float[n]; _b = new float[n]; _w = new float[n];
    }

    /// <summary>Canvas pixels per tile pixel would be this many original frame
    /// pixels: the reduction to ask <see cref="MosaicTile.FromFrame"/> for.</summary>
    public double ReductionFor(WcsInfo frameWcs) {
        double canvasScale = Math.Sqrt(Math.Abs(Wcs.CD11 * Wcs.CD22 - Wcs.CD12 * Wcs.CD21));
        double frameScale = Math.Sqrt(Math.Abs(frameWcs.CD11 * frameWcs.CD22 - frameWcs.CD12 * frameWcs.CD21));
        return frameScale > 0 ? canvasScale / frameScale : 1;
    }

    /// <summary>Where a tile's corners fall on the canvas (0-based pixels),
    /// in order top-left, top-right, bottom-right, bottom-left of the frame.
    /// Null when the tile is not in front of the canvas's tangent plane.</summary>
    public (double x, double y)[]? Footprint(MosaicTile tile) {
        double fw = tile.Width * tile.Bin, fh = tile.Height * tile.Bin;
        var corners = new[] { (0.5, 0.5), (fw + 0.5, 0.5), (fw + 0.5, fh + 0.5), (0.5, fh + 0.5) };
        var result = new (double, double)[4];
        for (int i = 0; i < 4; i++) {
            var (ra, dec) = tile.Wcs.PixelToRaDec(corners[i].Item1, corners[i].Item2);
            var (px, py) = Wcs.RaDecToPixel(ra, dec);
            if (!double.IsFinite(px) || !double.IsFinite(py)) return null;
            result[i] = (px - 1, py - 1);
        }
        return result;
    }

    /// <summary>Composite a tile. Returns its footprint, or null when no part
    /// of it lands on the canvas.</summary>
    public (double x, double y)[]? Add(MosaicTile tile) {
        var quad = Footprint(tile);
        if (quad == null) return null;
        int x0 = Math.Max(0, (int)Math.Floor(quad.Min(p => p.x)));
        int x1 = Math.Min(Width - 1, (int)Math.Ceiling(quad.Max(p => p.x)));
        int y0 = Math.Max(0, (int)Math.Floor(quad.Min(p => p.y)));
        int y1 = Math.Min(Height - 1, (int)Math.Ceiling(quad.Max(p => p.y)));
        if (x0 > x1 || y0 > y1) return null;

        double feather = Math.Max(2, FeatherFraction * Math.Min(tile.Width, tile.Height));
        double half = (tile.Bin - 1) / 2.0;
        int tw = tile.Width, th = tile.Height;

        lock (_gate) {
            Parallel.For(y0, y1 + 1, y => {
                for (int x = x0; x <= x1; x++) {
                    // Canvas pixel -> sky -> original frame pixel (1-based)
                    // -> reduced grid (0-based).
                    var (ra, dec) = Wcs.PixelToRaDec(x + 1, y + 1);
                    var (fx, fy) = tile.Wcs.RaDecToPixel(ra, dec);
                    if (!double.IsFinite(fx) || !double.IsFinite(fy)) continue;
                    double tx = (fx + half) / tile.Bin - 1;
                    double ty = (fy + half) / tile.Bin - 1;
                    if (tx < 0 || ty < 0 || tx > tw - 1 || ty > th - 1) continue;

                    double edge = Math.Min(Math.Min(tx, tw - 1 - tx), Math.Min(ty, th - 1 - ty));
                    double t = Math.Min(1, edge / feather);
                    float wgt = (float)(t * t * (3 - 2 * t)) + 1e-4f;

                    tile.Sample(tx, ty, out var r, out var g, out var b);
                    int i = y * Width + x;
                    _r[i] += wgt * r; _g[i] += wgt * g; _b[i] += wgt * b; _w[i] += wgt;
                }
            });
            TileCount++;
        }
        return quad;
    }

    /// <summary>Linear value at a canvas pixel (NaN where nothing landed). For tests.</summary>
    internal (float r, float g, float b) ValueAt(int x, int y) {
        int i = y * Width + x;
        if (_w[i] <= 0) return (float.NaN, float.NaN, float.NaN);
        return (_r[i] / _w[i], _g[i] / _w[i], _b[i] / _w[i]);
    }

    /// <summary>Stretched 8-bit RGB, interleaved. Each channel is stretched on
    /// its own (unlinked), which neutralises the sky colour the same way the
    /// LIVE auto-stretch does; pixels no panel has covered yet stay a flat
    /// dark grey so the unfilled part of the field reads as empty.</summary>
    public byte[] RenderRgb() {
        int n = Width * Height;
        var rgb = new byte[n * 3];
        lock (_gate) {
            var lr = Stretch(_r);
            var lg = Stretch(_g);
            var lb = Stretch(_b);
            Parallel.For(0, Height, y => {
                for (int x = 0; x < Width; x++) {
                    int i = y * Width + x, o = i * 3;
                    float w = _w[i];
                    if (w <= 0) { rgb[o] = 18; rgb[o + 1] = 18; rgb[o + 2] = 22; continue; }
                    rgb[o] = lr(_r[i] / w);
                    rgb[o + 1] = lg(_g[i] / w);
                    rgb[o + 2] = lb(_b[i] / w);
                }
            });
        }
        return rgb;
    }

    public byte[] RenderJpeg(int quality = 85) => JpegHelper.EncodeRgb(RenderRgb(), Width, Height, quality);

    /// <summary>Screen stretch for one channel: black point a little under
    /// the sky, white point near the brightest stars, then a midtones
    /// transfer that puts the sky at 25% grey (the PixInsight auto-stretch
    /// recipe).</summary>
    private Func<float, byte> Stretch(float[] sum) {
        var values = new List<float>(64_000);
        int step = Math.Max(1, sum.Length / 64_000);
        for (int i = 0; i < sum.Length; i += step)
            if (_w[i] > 0) values.Add(sum[i] / _w[i]);
        if (values.Count < 16) return _ => 0;
        values.Sort();
        float median = values[values.Count / 2];
        var dev = values.Select(v => Math.Abs(v - median)).OrderBy(v => v).ToList();
        float madn = 1.4826f * dev[dev.Count / 2];
        float black = median - 2.8f * madn;
        float white = values[Math.Min(values.Count - 1, (int)(values.Count * 0.9995))];
        if (white <= black) white = black + 1;
        double medNorm = Math.Clamp((median - black) / (white - black), 1e-6, 1 - 1e-6);
        const double target = 0.25;
        double m = medNorm * (target - 1) / (2 * target * medNorm - target - medNorm);
        float range = white - black;
        return v => {
            double x = Math.Clamp((v - black) / range, 0, 1);
            double y = (m - 1) * x / ((2 * m - 1) * x - m);
            return (byte)Math.Clamp((int)(y * 255 + 0.5), 0, 255);
        };
    }

    /// <summary>Median of a plane from a sample of about 50 000 values, with
    /// an optional mask of the pixels to consider.</summary>
    internal static float SampledMedian(float[] plane, float[]? weights) {
        int step = Math.Max(1, plane.Length / 50_000);
        var s = new List<float>(plane.Length / step + 1);
        for (int i = 0; i < plane.Length; i += step)
            if (weights == null || weights[i] > 0) s.Add(plane[i]);
        if (s.Count == 0) return 0;
        s.Sort();
        return s[s.Count / 2];
    }
}
