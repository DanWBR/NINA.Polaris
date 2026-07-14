// Copyright (C) 2016-2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This file is derived from N.I.N.A. - Nighttime Imaging 'N' Astronomy.
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
//
// As part of N.I.N.A. Polaris this file is additionally available under the
// GNU Affero General Public License v3.0 (see LICENSE.txt and NOTICE), at the
// recipient's option, pursuant to MPL-2.0 section 3.3.

// Copyright (C) 2016-2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This file is derived from N.I.N.A. - Nighttime Imaging 'N' Astronomy.
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
//
// As part of N.I.N.A. Polaris this file is additionally available under the
// GNU Affero General Public License v3.0 (see LICENSE.txt and NOTICE), at the
// recipient's option, pursuant to MPL-2.0 section 3.3.

using System.Collections.Concurrent;
using System.Threading.Tasks;
using NINA.Core.Enum;
using NINA.Image.ImageData;

namespace NINA.Image.FileFormat.FITS;

public static class FITSReader {
    private const int BLOCK_SIZE = 2880;
    private const int CARD_SIZE = 80;

    public static BaseImageData Read(Stream stream) {
        var headers = ReadHeaders(stream);

        int bitpix = GetIntHeader(headers, "BITPIX", 16);
        int naxis = GetIntHeader(headers, "NAXIS", 2);
        int width = GetIntHeader(headers, "NAXIS1", 0);
        int height = GetIntHeader(headers, "NAXIS2", 0);
        // RGB cubes use NAXIS=3 + NAXIS3=3 (R/G/B planes). Anything
        // else collapses to a single plane (grayscale or just the
        // first plane of a multi-frame cube, close enough for v1).
        int planes = (naxis >= 3) ? GetIntHeader(headers, "NAXIS3", 1) : 1;
        if (planes != 1 && planes != 3) planes = 1;
        int bzero = GetIntHeader(headers, "BZERO", 0);
        double bscale = GetDoubleHeader(headers, "BSCALE", 1.0);
        string bayerPat = GetStringHeader(headers, "BAYERPAT", "");

        var bayerPattern = bayerPat.ToUpperInvariant() switch {
            "RGGB" => BayerPatternEnum.RGGB,
            "BGGR" => BayerPatternEnum.BGGR,
            "GBRG" => BayerPatternEnum.GBRG,
            "GRBG" => BayerPatternEnum.GRBG,
            _ => BayerPatternEnum.None
        };

        // Read the full buffer covering every plane. For grayscale this
        // is the existing width*height; for RGB it's 3× larger and
        // stored plane-sequentially (R first, then G, then B), the
        // FITS convention also used by PixInsight, Siril, and astropy.
        var pixels = ReadPixelData(stream, width, height * planes, bitpix, bzero, bscale);

        var props = new ImageProperties {
            Width = width,
            Height = height,
            // 8-bit sources are promoted into the 16-bit range in
            // ReadPixelData (see case 8), so report 16 here too — otherwise
            // a RAW8 frame saved as a 16-bit FITS reloads as BitDepth=8 and
            // the 16-bit stretch renders it near-black.
            BitDepth = Math.Abs(bitpix) <= 8 ? 16 : (Math.Abs(bitpix) > 16 ? 16 : Math.Abs(bitpix)),
            IsBayered = bayerPattern != BayerPatternEnum.None,
            BayerPattern = bayerPattern,
            Channels = planes,
            // CCALB-0a: pick up WCS if the FITS was plate-solved and
            // re-stamped (AstapSolver does this after a successful
            // solve). Null when the source has no WCS block, which
            // is the common case for raw lights or un-solved masters.
            Wcs = WcsHeaders.Read(headers),
        };

        var metaData = ExtractMetaData(headers);
        return new BaseImageData(pixels, props, metaData);
    }

    public static BaseImageData Read(byte[] data) {
        using var ms = new MemoryStream(data);
        return Read(ms);
    }

    /// <summary>
    /// Read just the FITS header block, leaving the stream positioned
    /// at the start of the pixel data (which the caller is free to
    /// ignore). Used by the STUDIO frame index, parsing a 64 MB pixel
    /// block of every file just to read keywords is wasteful.
    /// </summary>
    public static Dictionary<string, FITSHeaderCard> ReadHeadersOnly(Stream stream) {
        return ReadHeaders(stream);
    }

    private static Dictionary<string, FITSHeaderCard> ReadHeaders(Stream stream) {
        var headers = new Dictionary<string, FITSHeaderCard>(StringComparer.OrdinalIgnoreCase);
        var block = new byte[BLOCK_SIZE];
        bool endFound = false;

        while (!endFound) {
            int bytesRead = stream.Read(block, 0, BLOCK_SIZE);
            if (bytesRead < BLOCK_SIZE) break;

            for (int i = 0; i < BLOCK_SIZE; i += CARD_SIZE) {
                var card = FITSHeaderCard.Parse(block.AsSpan(i, CARD_SIZE));
                if (card == null) continue;
                if (card.Keyword == "END") {
                    endFound = true;
                    break;
                }
                headers[card.Keyword] = card;
            }
        }

        return headers;
    }

    private static ushort[] ReadPixelData(Stream stream, int width, int height, int bitpix, int bzero, double bscale) {
        long pixelCount = (long)width * height;
        // A 0x0 (or negative-dimension) image means the source FITS had no
        // NAXIS1/NAXIS2 — e.g. an empty or malformed BLOB handed back by an
        // INDI driver during a video->still transition. Bail out with a
        // clear, catchable error instead of letting Partitioner.Create(0, 0)
        // throw the cryptic "toExclusive ('0') must be greater than '0'".
        if (pixelCount <= 0) {
            throw new InvalidDataException(
                $"FITS image has no pixels (NAXIS1={width}, NAXIS2={height}); " +
                "the camera returned an empty or malformed frame.");
        }
        var pixels = new ushort[pixelCount];

        int bytesPerPixel = Math.Abs(bitpix) / 8;
        var rawData = new byte[pixelCount * bytesPerPixel];

        int totalRead = 0;
        while (totalRead < rawData.Length) {
            int read = stream.Read(rawData, totalRead, rawData.Length - totalRead);
            if (read == 0) break;
            totalRead += read;
        }

        switch (bitpix) {
            case 8:
            case 16:
            case 32:
                DecodeIntegerPixels(rawData, pixels, pixelCount, bitpix, bzero, bscale);
                break;
            case -32: // IEEE single-precision float
                ReadFloatPixels(rawData, pixels, pixelCount, bzero, bscale, bytesPerSample: 4);
                break;
            case -64: // IEEE double-precision float
                ReadFloatPixels(rawData, pixels, pixelCount, bzero, bscale, bytesPerSample: 8);
                break;
        }

        return pixels;
    }

    /// <summary>
    /// Decode <paramref name="pixelCount"/> integer pixels (BITPIX 8/16/32)
    /// from <paramref name="rawData"/> into <paramref name="pixels"/>. The
    /// raw bytes start at index 0 and the decode writes to indices
    /// <c>[0, pixelCount)</c>, so the same routine serves the full-frame read
    /// and the strip reader (which hands it one horizontal slice at a time).
    /// <para>BENCH-PERF: the per-pixel loops are pure maps (each output pixel
    /// depends only on its own raw bytes), so they fan out across cores. On a
    /// Pi this is the bottleneck when opening masters / batch stacking. Output
    /// is byte-identical to the old serial loops.</para>
    /// </summary>
    internal static void DecodeIntegerPixels(byte[] rawData, ushort[] pixels, long pixelCount,
                                             int bitpix, int bzero, double bscale) {
        switch (bitpix) {
            case 8:
                // 8-bit source (e.g. an INDI driver left in RAW8 — the
                // SVBONY SV405CC defaults to it). Promote into the TOP 8
                // bits of the 16-bit range (value << 8) so the rest of the
                // 16-bit pipeline (auto-stretch, stacking, FITS save) sees a
                // normal full-range frame instead of a near-black 0..255
                // image. Mirrors the native SVBONY SDK's own RAW8 handling.
                Parallel.ForEach(Partitioner.Create(0L, pixelCount), range => {
                    for (long i = range.Item1; i < range.Item2; i++) {
                        int v = (int)(rawData[i] * bscale + bzero);
                        pixels[i] = (ushort)(Math.Clamp(v, 0, 255) << 8);
                    }
                });
                break;
            case 16:
                // FITS BITPIX=16 is a SIGNED sample; the unsigned-camera
                // convention is BZERO=32768 (physical = signed + 32768). Two
                // real-world encodings must both decode to a correct 0..65535
                // unsigned pixel:
                //   (a) standard unsigned: signed sample + BZERO(32768).
                //   (b) some drivers write raw *unsigned* samples with BZERO=0
                //       (or omit it). Interpreting those as signed pushes every
                //       value > 32767 negative, and a (ushort) cast of a
                //       negative double is 0 — so saturated star cores render
                //       BLACK. Treat BZERO=0,BSCALE=1 as unsigned instead.
                // Clamp on every path (case 8 and case 32 already do); the old
                // 16-bit branch was the only one relying on integer wraparound,
                // which the double-typed cast doesn't provide.
                bool unsignedRaw = bzero == 0 && bscale == 1.0;
                Parallel.ForEach(Partitioner.Create(0L, pixelCount), range => {
                    for (long i = range.Item1; i < range.Item2; i++) {
                        int raw = (rawData[i * 2] << 8) | rawData[i * 2 + 1]; // big-endian, 0..65535
                        double phys = unsignedRaw ? raw : ((short)raw * bscale + bzero);
                        pixels[i] = (ushort)Math.Clamp(phys, 0.0, 65535.0);
                    }
                });
                break;
            case 32:
                Parallel.ForEach(Partitioner.Create(0L, pixelCount), range => {
                    for (long i = range.Item1; i < range.Item2; i++) {
                        int val = (rawData[i * 4] << 24) | (rawData[i * 4 + 1] << 16) |
                                  (rawData[i * 4 + 2] << 8) | rawData[i * 4 + 3];
                        double scaled = val * bscale + bzero;
                        pixels[i] = (ushort)Math.Clamp(scaled, 0, 65535);
                    }
                });
                break;
        }
    }

    /// <summary>
    /// Read float pixel data and auto-scale to the ushort range. Float
    /// FITS files arrive in two distinct conventions and we don't get
    /// to know which up front:
    ///   - Normalised stacks (PixInsight, Siril) store values in
    ///     [0.0, 1.0]. A naive `(ushort)val` clamps every pixel to 0
    ///     and renders the whole image black, the regression that
    ///     surfaced first when opening a stacked master from the
    ///     FILES tab.
    ///   - Unscaled integer-to-float conversions store values in
    ///     roughly [0, 65535] and the naive cast happens to work.
    /// The fix is to scan the observed min/max in a first pass and
    /// linearly remap to [0, 65535] in a second pass. AutoStretch later
    /// applies the usual MTF on top, so non-linear curves in the source
    /// (HDR composites with a long tail) still display correctly.
    /// NaN / infinity pixels (very common in stacks where the rejection
    /// killed every contributing frame) are treated as zero, both for
    /// the range scan and the final write.
    /// </summary>
    private static void ReadFloatPixels(byte[] rawData, ushort[] pixels, long pixelCount,
                                        int bzero, double bscale, int bytesPerSample) {
        // BENCH-PERF: both passes are full-frame and dominate float-FITS
        // load (normalized masters from PixInsight/Siril). Parallelize the
        // min/max scan with per-partition reduction and the rescale write
        // per-pixel. Identical result to the old serial two-pass form.
        // First pass: gather a tight min/max over finite samples only.
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        var mmLock = new object();
        Parallel.ForEach(Partitioner.Create(0L, pixelCount),
            () => (lmin: double.PositiveInfinity, lmax: double.NegativeInfinity),
            (range, _, local) => {
                var (lmin, lmax) = local;
                for (long i = range.Item1; i < range.Item2; i++) {
                    double val = ReadFloatAt(rawData, i * bytesPerSample, bytesPerSample) * bscale + bzero;
                    if (!double.IsFinite(val)) continue;
                    if (val < lmin) lmin = val;
                    if (val > lmax) lmax = val;
                }
                return (lmin, lmax);
            },
            local => {
                lock (mmLock) {
                    if (local.lmin < min) min = local.lmin;
                    if (local.lmax > max) max = local.lmax;
                }
            });
        if (!double.IsFinite(min) || !double.IsFinite(max) || max <= min) {
            // Degenerate input: constant or all-NaN buffer. Output stays
            // zero, there's nothing meaningful to show anyway.
            Array.Clear(pixels, 0, pixels.Length);
            return;
        }

        double range = max - min;
        double scale65k = 65535.0 / range;
        double minLocal = min;

        // Second pass: rescale + write (parallel; each output pixel is
        // independent).
        Parallel.ForEach(Partitioner.Create(0L, pixelCount), prange => {
            for (long i = prange.Item1; i < prange.Item2; i++) {
                double val = ReadFloatAt(rawData, i * bytesPerSample, bytesPerSample) * bscale + bzero;
                if (!double.IsFinite(val)) { pixels[i] = 0; continue; }
                double mapped = (val - minLocal) * scale65k;
                // Clamp guards against floating-point noise that nudges the
                // top end one ULP past max.
                pixels[i] = (ushort)Math.Clamp(mapped, 0.0, 65535.0);
            }
        });
    }

    /// <summary>
    /// FITS stores floats and doubles in big-endian byte order. .NET's
    /// BitConverter is host-endian, so reverse the bytes before decoding.
    /// </summary>
    private static double ReadFloatAt(byte[] data, long offset, int bytesPerSample) {
        if (bytesPerSample == 4) {
            Span<byte> bytes = stackalloc byte[4] {
                data[offset + 3], data[offset + 2], data[offset + 1], data[offset]
            };
            return BitConverter.ToSingle(bytes);
        } else {
            Span<byte> bytes = stackalloc byte[8] {
                data[offset + 7], data[offset + 6], data[offset + 5], data[offset + 4],
                data[offset + 3], data[offset + 2], data[offset + 1], data[offset]
            };
            return BitConverter.ToDouble(bytes);
        }
    }

    internal static ImageMetaData ExtractMetaData(Dictionary<string, FITSHeaderCard> headers) {
        var meta = new ImageMetaData();

        meta.Camera.Name = GetStringHeader(headers, "INSTRUME", "");
        meta.Camera.Temperature = GetDoubleHeader(headers, "CCD-TEMP", 0);
        meta.Camera.Gain = GetIntHeader(headers, "GAIN", 0);
        meta.Camera.Offset = GetIntHeader(headers, "OFFSET", 0);
        meta.Camera.BinX = (short)GetIntHeader(headers, "XBINNING", 1);
        meta.Camera.BinY = (short)GetIntHeader(headers, "YBINNING", 1);
        meta.Camera.PixelSizeX = GetDoubleHeader(headers, "XPIXSZ", 0);
        meta.Camera.PixelSizeY = GetDoubleHeader(headers, "YPIXSZ", 0);

        meta.Telescope.Name = GetStringHeader(headers, "TELESCOP", "");
        meta.Telescope.OpticalTube = GetStringHeader(headers, "OTA", "");
        meta.Telescope.FocalLength = GetDoubleHeader(headers, "FOCALLEN", 0);
        meta.Telescope.RightAscension = GetDoubleHeader(headers, "RA", 0);
        meta.Telescope.Declination = GetDoubleHeader(headers, "DEC", 0);

        meta.Observer.Latitude = GetDoubleHeader(headers, "SITELAT", 0);
        meta.Observer.Longitude = GetDoubleHeader(headers, "SITELONG", 0);
        meta.Observer.Elevation = GetDoubleHeader(headers, "SITEELEV", 0);

        meta.Target.Name = GetStringHeader(headers, "OBJECT", "");
        meta.Exposure.ExposureTime = GetDoubleHeader(headers, "EXPTIME", 0);
        meta.Exposure.Filter = GetStringHeader(headers, "FILTER", "");
        meta.Exposure.ImageType = GetStringHeader(headers, "IMAGETYP", "LIGHT");

        // FITSWriter reads FILTER from meta.FilterWheel.Filter (live
        // capture path populates that field), so mirror the FILTER
        // header into both buckets here. Without this the read/write
        // roundtrip used by CalibrationService / MasterFrameService /
        // BatchStackingService silently drops the filter tag.
        meta.FilterWheel.Filter = meta.Exposure.Filter;
        meta.FilterWheel.Name = GetStringHeader(headers, "FWHEEL",
            meta.FilterWheel.Name);

        return meta;
    }

    internal static int GetIntHeader(Dictionary<string, FITSHeaderCard> headers, string key, int defaultValue) {
        if (headers.TryGetValue(key, out var card) && int.TryParse(card.Value, out int val)) return val;
        return defaultValue;
    }

    internal static double GetDoubleHeader(Dictionary<string, FITSHeaderCard> headers, string key, double defaultValue) {
        if (headers.TryGetValue(key, out var card) && double.TryParse(card.Value,
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double val))
            return val;
        return defaultValue;
    }

    internal static string GetStringHeader(Dictionary<string, FITSHeaderCard> headers, string key, string defaultValue) {
        if (headers.TryGetValue(key, out var card)) return card.Value;
        return defaultValue;
    }
}