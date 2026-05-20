using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SharpJpegEncoder = SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder;

namespace NINA.Image.ImageAnalysis;

public static class JpegHelper {
    public static byte[] EncodeGrayscale(byte[] grayscalePixels, int width, int height, int quality = 85) {
        using var image = SixLabors.ImageSharp.Image.LoadPixelData<L8>(grayscalePixels, width, height);
        using var ms = new MemoryStream();
        image.Save(ms, new SharpJpegEncoder { Quality = quality });
        return ms.ToArray();
    }

    public static byte[] EncodeRgb(byte[] rgbPixels, int width, int height, int quality = 85) {
        using var image = SixLabors.ImageSharp.Image.LoadPixelData<Rgb24>(rgbPixels, width, height);
        using var ms = new MemoryStream();
        image.Save(ms, new SharpJpegEncoder { Quality = quality });
        return ms.ToArray();
    }
}
