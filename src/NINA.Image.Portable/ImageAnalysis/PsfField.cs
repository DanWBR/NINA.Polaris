// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
//
// As part of N.I.N.A. Polaris this file is additionally available under the
// GNU Affero General Public License v3.0 (see LICENSE.txt and NOTICE), at the
// recipient's option, pursuant to MPL-2.0 section 3.3.

using System;

namespace NINA.Image.ImageAnalysis;

/// <summary>
/// A spatially-varying PSF: the frame is split into a GridX×GridY grid and each
/// cell carries its own measured <see cref="PsfModel"/> (cells too star-poor to
/// fit reuse <see cref="Global"/>). Produced by
/// <see cref="PsfExtractor.ExtractField"/> and consumed by the field-varying
/// Richardson-Lucy deconvolution, which lets corners (coma / field curvature /
/// tilt) be sharpened with their own kernel rather than one global FWHM.
/// </summary>
public class PsfField {
    public int GridX { get; }
    public int GridY { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>Row-major cell PSFs, length GridX·GridY.</summary>
    public PsfModel[] Cells { get; }

    /// <summary>Whole-frame PSF; also the fallback for star-poor cells.</summary>
    public PsfModel Global { get; }

    public PsfField(int gridX, int gridY, int width, int height,
                    PsfModel[] cells, PsfModel global) {
        if (cells == null || cells.Length != gridX * gridY)
            throw new ArgumentException("cells length must be gridX*gridY", nameof(cells));
        GridX = gridX; GridY = gridY; Width = width; Height = height;
        Cells = cells; Global = global ?? throw new ArgumentNullException(nameof(global));
    }

    /// <summary>The PSF whose cell contains pixel (x, y).</summary>
    public PsfModel At(int x, int y) {
        int gx = (int)Math.Clamp((long)x * GridX / Math.Max(1, Width), 0, GridX - 1);
        int gy = (int)Math.Clamp((long)y * GridY / Math.Max(1, Height), 0, GridY - 1);
        return Cells[gy * GridX + gx] ?? Global;
    }

    /// <summary>Cell PSF by grid index.</summary>
    public PsfModel Cell(int gx, int gy) =>
        Cells[Math.Clamp(gy, 0, GridY - 1) * GridX + Math.Clamp(gx, 0, GridX - 1)] ?? Global;

    /// <summary>How many cells got their own (non-fallback) PSF — a quick
    /// quality indicator for the UI.</summary>
    public int MeasuredCellCount {
        get {
            int n = 0;
            foreach (var c in Cells) if (c != null && !ReferenceEquals(c, Global)) n++;
            return n;
        }
    }
}
