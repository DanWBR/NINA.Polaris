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

using NINA.Image.FileFormat.FITS;

namespace NINA.Polaris.Services.Mosaic;

/// <summary>
/// Geometry of a MOSAIC run: how many panels a requested field needs, where
/// each one points, and the canvas the panels are composited onto.
///
/// <para>Everything is laid out in the camera's own frame, taken from a solved
/// reference exposure: panel offsets are measured along the sensor's X and Y
/// axes, so the grid follows the camera angle instead of the RA/Dec axes. A
/// grid on RA/Dec with a rotated camera leaves wedge-shaped gaps at the panel
/// corners; one on the sensor axes keeps the overlap even everywhere.</para>
///
/// <para>The canvas is the reference frame scaled up: the same tangent point
/// (the target), the same CD matrix orientation and parity, at a coarser pixel
/// scale so the whole field fits in <see cref="MaxCanvasSide"/> pixels.</para>
/// </summary>
public static class MosaicLayout {
    /// <summary>Longest side of the composited canvas, in pixels. It is the
    /// on-screen view, not the science product; a full-resolution 3x3 of a
    /// 26 MP sensor is over 200 MP and does not belong in memory on the
    /// boards Polaris runs on.</summary>
    public const int MaxCanvasSide = 2400;

    public const double MinZoom = 1.0;
    public const double MaxZoom = 8.0;
    public const double MinOverlapPercent = 5;
    public const double MaxOverlapPercent = 50;

    /// <summary>Panels along one axis for a field <paramref name="zoom"/>
    /// panels wide with at least <paramref name="overlapFraction"/> overlap.
    /// N panels with overlap o cover N(1-o)+o panel widths.</summary>
    public static int PanelCount(double zoom, double overlapFraction) {
        if (zoom <= 1 + 1e-9) return 1;
        return (int)Math.Ceiling((zoom - overlapFraction) / (1 - overlapFraction) - 1e-9);
    }

    /// <summary>Distance between neighbouring panel centres, in panel widths.
    /// Spreads the panels evenly across the requested field, so the actual
    /// overlap is the requested one or a little more.</summary>
    public static double Step(double zoom, int count) => count > 1 ? (zoom - 1) / (count - 1) : 0;

    /// <summary>Actual overlap between neighbours, as a fraction of a panel.</summary>
    public static double ActualOverlap(double zoom, int count) => count > 1 ? 1 - Step(zoom, count) : 0;

    /// <summary>Build the plan from a solved reference frame.</summary>
    /// <param name="reference">WCS of the reference frame. Its CD matrix gives
    /// the orientation; its centre is ignored, the grid centres on the
    /// target.</param>
    /// <param name="frameWidth">Reference frame width, pixels.</param>
    /// <param name="frameHeight">Reference frame height, pixels.</param>
    public static MosaicGeometry Plan(double targetRaHours, double targetDecDeg,
            WcsInfo reference, int frameWidth, int frameHeight,
            double zoomX, double zoomY, double overlapPercent) {
        if (frameWidth <= 0 || frameHeight <= 0)
            throw new ArgumentException("Frame size must be positive");
        zoomX = Math.Clamp(zoomX, MinZoom, MaxZoom);
        zoomY = Math.Clamp(zoomY, MinZoom, MaxZoom);
        var overlap = Math.Clamp(overlapPercent, MinOverlapPercent, MaxOverlapPercent) / 100.0;

        int cols = PanelCount(zoomX, overlap), rows = PanelCount(zoomY, overlap);
        double stepX = Step(zoomX, cols), stepY = Step(zoomY, rows);

        // The target frame: the reference orientation, centred on the target.
        var target = new WcsInfo {
            RaDeg = targetRaHours * 15, DecDeg = targetDecDeg,
            RefPixelX = (frameWidth + 1) / 2.0, RefPixelY = (frameHeight + 1) / 2.0,
            CD11 = reference.CD11, CD12 = reference.CD12,
            CD21 = reference.CD21, CD22 = reference.CD22,
        };

        // Canvas pixels per reference pixel. Never above 1: upsampling the
        // view would only cost memory.
        double fieldW = zoomX * frameWidth, fieldH = zoomY * frameHeight;
        double k = Math.Min(1.0, MaxCanvasSide / Math.Max(fieldW, fieldH));
        int cw = Math.Max(1, (int)Math.Round(fieldW * k));
        int ch = Math.Max(1, (int)Math.Round(fieldH * k));
        var canvas = new WcsInfo {
            RaDeg = target.RaDeg, DecDeg = target.DecDeg,
            RefPixelX = (cw + 1) / 2.0, RefPixelY = (ch + 1) / 2.0,
            CD11 = reference.CD11 / k, CD12 = reference.CD12 / k,
            CD21 = reference.CD21 / k, CD22 = reference.CD22 / k,
        };

        // Serpentine from the top row (smallest Y, the top of the picture),
        // so the mount never makes a long move back to the start of a row.
        var panels = new List<MosaicPanelPlan>(cols * rows);
        for (int r = 0; r < rows; r++) {
            bool reversed = (r & 1) == 1;
            for (int i = 0; i < cols; i++) {
                int c = reversed ? cols - 1 - i : i;
                double offX = (c - (cols - 1) / 2.0) * stepX * frameWidth;
                double offY = (r - (rows - 1) / 2.0) * stepY * frameHeight;
                var (ra, dec) = target.PixelToRaDec(target.RefPixelX + offX, target.RefPixelY + offY);
                // Planned footprint on the canvas, 0-based pixel edges.
                double cx = cw / 2.0 + offX * k, cy = ch / 2.0 + offY * k;
                double hw = frameWidth * k / 2.0, hh = frameHeight * k / 2.0;
                panels.Add(new MosaicPanelPlan(panels.Count, r, c, ra / 15.0, dec,
                    cx - hw, cy - hh, cx + hw, cy + hh));
            }
        }

        return new MosaicGeometry(cols, rows, ActualOverlap(zoomX, cols), ActualOverlap(zoomY, rows),
            frameWidth, frameHeight, cw, ch, canvas, panels);
    }

    /// <summary>Angular size of a frame, in degrees, from its CD matrix.</summary>
    public static (double widthDeg, double heightDeg) FrameSizeDeg(WcsInfo wcs, int width, int height) {
        double sx = Math.Sqrt(wcs.CD11 * wcs.CD11 + wcs.CD21 * wcs.CD21);
        double sy = Math.Sqrt(wcs.CD12 * wcs.CD12 + wcs.CD22 * wcs.CD22);
        return (sx * width, sy * height);
    }
}

/// <summary>One planned panel: where to point, and its footprint on the
/// canvas (0-based pixel edges, before any pointing error).</summary>
public sealed record MosaicPanelPlan(int Index, int Row, int Col, double RaHours, double DecDeg,
    double X0, double Y0, double X1, double Y1);

public sealed record MosaicGeometry(int Cols, int Rows, double OverlapX, double OverlapY,
    int FrameWidth, int FrameHeight, int CanvasWidth, int CanvasHeight,
    WcsInfo CanvasWcs, IReadOnlyList<MosaicPanelPlan> Panels);
