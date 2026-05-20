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

        var pixels = ReadPixelData(stream, width, height, bitpix, bzero, bscale);

        var props = new ImageProperties {
            Width = width,
            Height = height,
            BitDepth = Math.Abs(bitpix) > 16 ? 16 : Math.Abs(bitpix),
            IsBayered = bayerPattern != BayerPatternEnum.None,
            BayerPattern = bayerPattern
        };

        var metaData = ExtractMetaData(headers);
        return new BaseImageData(pixels, props, metaData);
    }

    public static BaseImageData Read(byte[] data) {
        using var ms = new MemoryStream(data);
        return Read(ms);
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
                for (int i = 0; i < pixelCount; i++) {
                    pixels[i] = (ushort)(rawData[i] * bscale + bzero);
                }
                break;
            case 16:
                for (int i = 0; i < pixelCount; i++) {
                    short val = (short)((rawData[i * 2] << 8) | rawData[i * 2 + 1]); // big-endian
                    pixels[i] = (ushort)(val * bscale + bzero);
                }
                break;
            case 32:
                for (int i = 0; i < pixelCount; i++) {
                    int val = (rawData[i * 4] << 24) | (rawData[i * 4 + 1] << 16) |
                              (rawData[i * 4 + 2] << 8) | rawData[i * 4 + 3];
                    double scaled = val * bscale + bzero;
                    pixels[i] = (ushort)Math.Clamp(scaled, 0, 65535);
                }
                break;
            case -32: // IEEE float
                for (int i = 0; i < pixelCount; i++) {
                    // Big-endian float
                    byte[] floatBytes = [rawData[i * 4 + 3], rawData[i * 4 + 2], rawData[i * 4 + 1], rawData[i * 4]];
                    float val = BitConverter.ToSingle(floatBytes);
                    double scaled = val * bscale + bzero;
                    pixels[i] = (ushort)Math.Clamp(scaled, 0, 65535);
                }
                break;
        }

        return pixels;
    }

    private static ImageMetaData ExtractMetaData(Dictionary<string, FITSHeaderCard> headers) {
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

        return meta;
    }

    private static int GetIntHeader(Dictionary<string, FITSHeaderCard> headers, string key, int defaultValue) {
        if (headers.TryGetValue(key, out var card) && int.TryParse(card.Value, out int val)) return val;
        return defaultValue;
    }

    private static double GetDoubleHeader(Dictionary<string, FITSHeaderCard> headers, string key, double defaultValue) {
        if (headers.TryGetValue(key, out var card) && double.TryParse(card.Value,
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double val))
            return val;
        return defaultValue;
    }

    private static string GetStringHeader(Dictionary<string, FITSHeaderCard> headers, string key, string defaultValue) {
        if (headers.TryGetValue(key, out var card)) return card.Value;
        return defaultValue;
    }
}
