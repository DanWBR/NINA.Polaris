using SkiaSharp;

namespace NINA.Image.ImageAnalysis;

public static class JpegHelper {
    public static byte[] EncodeGrayscale(byte[] grayscalePixels, int width, int height, int quality = 85) {
        using var bitmap = new SKBitmap(width, height, SKColorType.Gray8, SKAlphaType.Opaque);

        unsafe {
            fixed (byte* ptr = grayscalePixels) {
                bitmap.SetPixels((IntPtr)ptr);
            }
        }

        // Convert Gray8 → RGB for JPEG (JPEG doesn't support Gray8 natively in all decoders)
        using var rgbBitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(rgbBitmap)) {
            canvas.DrawBitmap(bitmap, 0, 0);
        }

        using var data = rgbBitmap.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data.ToArray();
    }

    public static byte[] EncodeRgb(byte[] rgbPixels, int width, int height, int quality = 85) {
        // Convert RGB24 (3 bytes/pixel) to RGBA32 (4 bytes/pixel) for SkiaSharp
        var rgba = new byte[width * height * 4];
        for (int i = 0, j = 0; i < rgbPixels.Length; i += 3, j += 4) {
            rgba[j] = rgbPixels[i];       // R
            rgba[j + 1] = rgbPixels[i + 1]; // G
            rgba[j + 2] = rgbPixels[i + 2]; // B
            rgba[j + 3] = 255;              // A
        }

        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);

        unsafe {
            fixed (byte* ptr = rgba) {
                bitmap.SetPixels((IntPtr)ptr);
            }
        }

        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data.ToArray();
    }
}
