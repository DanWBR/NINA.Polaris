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

using SkiaSharp;

namespace NINA.Image.ImageAnalysis;

public static class JpegHelper {
    public static byte[] EncodeGrayscale(byte[] grayscalePixels, int width, int height, int quality = 85) {
        // Sanity bail-outs, the previous code would silently produce
        // a 0×0 / pixel-less bitmap and then explode inside
        // canvas.DrawBitmap with "ArgumentNullException: image".
        if (grayscalePixels == null) {
            throw new System.ArgumentNullException(nameof(grayscalePixels));
        }
        if (width <= 0 || height <= 0) {
            throw new System.ArgumentException(
                $"Bad dimensions {width}x{height}", nameof(width));
        }
        if (grayscalePixels.Length < width * height) {
            throw new System.ArgumentException(
                $"Pixel buffer too small ({grayscalePixels.Length} bytes) for {width}x{height} = {width * height} bytes",
                nameof(grayscalePixels));
        }

        // Copy pixels into a Gray8 SKBitmap. InstallPixels takes ownership
        // of an SKImageInfo + the pinned buffer so SkiaSharp doesn't have
        // to free it, safer than the fixed-pointer + SetPixels pattern,
        // which left the bitmap with a pixel pointer SkiaSharp might
        // dereference after our fixed{} block ended.
        var info = new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque);
        using var bitmap = new SKBitmap(info);
        // SKBitmap-owned managed copy: allocate + Marshal-copy, no
        // unsafe pointer escape across the bitmap's lifetime.
        var pixelsPtr = bitmap.GetPixels(out var pixelsLen);
        if (pixelsPtr == System.IntPtr.Zero || pixelsLen.ToInt64() < width * height) {
            throw new System.InvalidOperationException(
                "SKBitmap allocation failed for " + width + "x" + height);
        }
        System.Runtime.InteropServices.Marshal.Copy(
            grayscalePixels, 0, pixelsPtr,
            (int)System.Math.Min(grayscalePixels.Length, width * height));

        // Encode Gray8 directly, modern SkiaSharp's JPEG encoder
        // handles single-channel input; the old "Gray8 → RGBA →
        // encode" detour caused the null-image crash because the
        // intermediate DrawBitmap path required an SKImage that
        // SkiaSharp couldn't materialise from the badly-initialised
        // Gray8 bitmap.
        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, quality);
        if (data == null) {
            throw new System.InvalidOperationException(
                "Skia returned null when encoding " + width + "x" + height + " Gray8 to JPEG");
        }
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