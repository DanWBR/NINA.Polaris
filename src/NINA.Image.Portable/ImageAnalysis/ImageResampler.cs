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

using System.Threading.Tasks;

namespace NINA.Image.ImageAnalysis;

public static class ImageResampler {
    public static ushort[] ApplyTransform(ushort[] source, int width, int height, AffineTransform transform) {
        return ApplyTransform(source, width, height, transform, new ushort[width * height]);
    }

    /// <summary>Destination-buffer overload (MEMOPT): resamples into a
    /// caller-owned buffer so per-frame consumers (the live stacker) can
    /// reuse session scratch instead of allocating W*H ushort[] per warped
    /// plane per frame. Out-of-canvas pixels are explicitly zeroed (a
    /// reused buffer carries the previous frame). Returns the destination
    /// — or the SOURCE unchanged when the transform is degenerate, so
    /// callers must use the return value, not assume dest was filled.
    /// dest must be at least W*H long and must not alias the source.</summary>
    public static ushort[] ApplyTransform(ushort[] source, int width, int height,
            AffineTransform transform, ushort[] result) {
        if (result.Length < width * height)
            throw new ArgumentException("Destination buffer too small for declared dimensions.", nameof(result));
        if (ReferenceEquals(result, source))
            throw new ArgumentException("In-place resampling is not supported.", nameof(result));

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

        // BENCH-PERF: rows are fully independent (each output pixel reads
        // source and writes its own result cell), so resampling the full
        // frame parallelizes cleanly across cores. The per-pixel math is
        // bit-for-bit identical to the old serial loop, only the row
        // iteration is fanned out. On WASM (single-threaded mono) this
        // degrades to a sequential run with no behavioural change.
        Parallel.For(0, height, y => {
            int rowOut = y * width;
            for (int x = 0; x < width; x++) {
                double srcX = iM00 * x + iM01 * y + iTx;
                double srcY = iM10 * x + iM11 * y + iTy;

                // Bilinear interpolation
                int x0 = (int)Math.Floor(srcX);
                int y0 = (int)Math.Floor(srcY);

                // Off-canvas: write 0 explicitly (a fresh array is already
                // zero, but a reused destination buffer carries the previous
                // frame's pixels).
                if (x0 < 0 || x0 >= width - 1 || y0 < 0 || y0 >= height - 1) {
                    result[rowOut + x] = 0;
                    continue;
                }

                double fx = srcX - x0;
                double fy = srcY - y0;

                int b = y0 * width + x0;
                double v00 = source[b];
                double v10 = source[b + 1];
                double v01 = source[b + width];
                double v11 = source[b + width + 1];

                double val = v00 * (1 - fx) * (1 - fy) + v10 * fx * (1 - fy)
                           + v01 * (1 - fx) * fy + v11 * fx * fy;

                result[rowOut + x] = (ushort)Math.Clamp(val, 0, 65535);
            }
        });

        return result;
    }
}