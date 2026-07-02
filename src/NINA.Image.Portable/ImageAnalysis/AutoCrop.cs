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

using System;
using System.Collections.Generic;

namespace NINA.Image.ImageAnalysis;

/// <summary>
/// Auto-crop: find the largest inner rectangle that contains only fully
/// stacked ("valid") pixels, so the ragged / black registration borders that
/// stacking leaves on slightly misaligned subs are removed automatically.
///
/// A registered + integrated stack fills the areas no sub covered with 0
/// (or a value at/below a small pedestal). This treats a pixel as INVALID
/// when every channel is &lt;= <c>threshold</c>, builds a valid mask, and
/// returns the maximum-area axis-aligned all-valid rectangle via the standard
/// "largest rectangle in a binary matrix" histogram algorithm (O(W·H)). For a
/// slightly-rotated overlap that is the biggest centred crop that clears the
/// diagonal corners.
///
/// <c>margin</c> shrinks the result inward by N px, useful because the
/// outermost covered rows can still be partial-coverage (fewer subs = lower
/// SNR) even though they are not exactly black.
/// </summary>
public static class AutoCrop {
    public readonly record struct Rect(int X, int Y, int Width, int Height);

    /// <summary>
    /// Largest all-valid rectangle in a plane-sequential ushort buffer. A
    /// pixel is valid when ANY channel is &gt; <paramref name="threshold"/>.
    /// Returns the full frame when the image has no black border (or is all
    /// black, a degenerate case the caller can treat as "nothing to crop").
    /// </summary>
    public static Rect FindContentRect(ushort[] data, int width, int height, int channels,
                                       int threshold = 0, int margin = 0) {
        if (width <= 0 || height <= 0) return new Rect(0, 0, Math.Max(0, width), Math.Max(0, height));
        int ch = channels == 3 ? 3 : 1;
        long plane = (long)width * height;

        // Valid mask: a pixel counts if any channel clears the threshold.
        var valid = new bool[plane];
        bool anyInvalid = false;
        for (long i = 0; i < plane; i++) {
            bool v = false;
            for (int c = 0; c < ch; c++) {
                if (data[c * plane + i] > threshold) { v = true; break; }
            }
            valid[i] = v;
            if (!v) anyInvalid = true;
        }
        // No border at all → nothing to trim.
        if (!anyInvalid) return Shrink(new Rect(0, 0, width, height), margin, width, height);

        // Largest rectangle of `true` cells. Row-by-row histogram + stack.
        var heights = new int[width];
        int bestArea = 0;
        var best = new Rect(0, 0, width, height);
        var stack = new Stack<(int start, int h)>();

        for (int y = 0; y < height; y++) {
            long row = (long)y * width;
            for (int x = 0; x < width; x++)
                heights[x] = valid[row + x] ? heights[x] + 1 : 0;

            stack.Clear();
            for (int x = 0; x <= width; x++) {
                int h = x < width ? heights[x] : 0;
                int start = x;
                while (stack.Count > 0 && stack.Peek().h > h) {
                    var top = stack.Pop();
                    int area = top.h * (x - top.start);
                    if (area > bestArea) {
                        bestArea = area;
                        // Rectangle spans columns [top.start, x-1] and rows
                        // [y-top.h+1, y] (inclusive).
                        best = new Rect(top.start, y - top.h + 1, x - top.start, top.h);
                    }
                    start = top.start;
                }
                stack.Push((start, h));
            }
        }

        if (bestArea <= 0) return Shrink(new Rect(0, 0, width, height), margin, width, height);
        return Shrink(best, margin, width, height);
    }

    private static Rect Shrink(Rect r, int margin, int imgW, int imgH) {
        if (margin <= 0) return r;
        int x = r.X + margin, y = r.Y + margin;
        int w = r.Width - 2 * margin, h = r.Height - 2 * margin;
        if (w < 1) { w = 1; x = Math.Min(x, imgW - 1); }
        if (h < 1) { h = 1; y = Math.Min(y, imgH - 1); }
        x = Math.Clamp(x, 0, Math.Max(0, imgW - 1));
        y = Math.Clamp(y, 0, Math.Max(0, imgH - 1));
        w = Math.Clamp(w, 1, imgW - x);
        h = Math.Clamp(h, 1, imgH - y);
        return new Rect(x, y, w, h);
    }
}
