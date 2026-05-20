namespace NINA.Image.ImageAnalysis;

public static class ImageResampler {
    public static ushort[] ApplyTransform(ushort[] source, int width, int height, AffineTransform transform) {
        var result = new ushort[width * height];

        // Invert the transform: for each output pixel, find source pixel
        // output = M * input + T => input = M^-1 * (output - T)
        double det = transform.M00 * transform.M11 - transform.M01 * transform.M10;
        if (Math.Abs(det) < 1e-12) return source;

        double invDet = 1.0 / det;
        double iM00 = transform.M11 * invDet;
        double iM01 = -transform.M01 * invDet;
        double iM10 = -transform.M10 * invDet;
        double iM11 = transform.M00 * invDet;
        double iTx = -(iM00 * transform.Tx + iM01 * transform.Ty);
        double iTy = -(iM10 * transform.Tx + iM11 * transform.Ty);

        for (int y = 0; y < height; y++) {
            for (int x = 0; x < width; x++) {
                double srcX = iM00 * x + iM01 * y + iTx;
                double srcY = iM10 * x + iM11 * y + iTy;

                // Bilinear interpolation
                int x0 = (int)Math.Floor(srcX);
                int y0 = (int)Math.Floor(srcY);
                double fx = srcX - x0;
                double fy = srcY - y0;

                if (x0 < 0 || x0 >= width - 1 || y0 < 0 || y0 >= height - 1) continue;

                double v00 = source[y0 * width + x0];
                double v10 = source[y0 * width + x0 + 1];
                double v01 = source[(y0 + 1) * width + x0];
                double v11 = source[(y0 + 1) * width + x0 + 1];

                double val = v00 * (1 - fx) * (1 - fy) + v10 * fx * (1 - fy)
                           + v01 * (1 - fx) * fy + v11 * fx * fy;

                result[y * width + x] = (ushort)Math.Clamp(val, 0, 65535);
            }
        }

        return result;
    }
}
