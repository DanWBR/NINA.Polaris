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

using NINA.Core.Enum;
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;

namespace NINA.Polaris.Services;

/// <summary>
/// Geometry of a main-camera solve, captured before the solver runs. Only live
/// main-camera solve callers supply this; solving an imported or auxiliary
/// image must not change the astrometry used by ImageWriterService.
/// </summary>
internal sealed record CaptureWcsContext(
    ICamera Camera, ITelescope Telescope, string? RigId, double FocalLengthMm,
    double PixelSizeX, double PixelSizeY, int SensorWidth, int SensorHeight,
    int Width, int Height, int BinX, int BinY, PierSide PierSide, IRotator? Rotator, double? RotatorAngle,
    bool VerticallyFlipped) {

    internal static CaptureWcsContext? Create(EquipmentManager equipment,
            ProfileService profiles, IImageData image, bool verticallyFlipped = false) {
        if (equipment.Camera is not { IsConnected: true } camera
                || equipment.Telescope is not { IsConnected: true } telescope
                || telescope.IsSlewing
                || (!string.IsNullOrEmpty(image.MetaData.Camera.Name)
                    && image.MetaData.Camera.Name != camera.DeviceName)
                || !IsFullFrame(image.Properties.Width, camera.MaxX, image.MetaData.Camera.BinX)
                || !IsFullFrame(image.Properties.Height, camera.MaxY, image.MetaData.Camera.BinY)) return null;
        var rotator = equipment.Rotator;
        double? angle = rotator is { IsConnected: true } ? rotator.Position : null;
        if (angle.HasValue && !double.IsFinite(angle.Value)) return null;
        return new(camera, telescope, profiles.ActiveEquipmentProfile?.Id,
            profiles.ActiveEquipmentProfile?.FocalLengthMm ?? profiles.Active.FocalLengthMm,
            camera.PixelSizeX, camera.PixelSizeY, camera.MaxX, camera.MaxY,
            image.Properties.Width, image.Properties.Height, image.MetaData.Camera.BinX,
            image.MetaData.Camera.BinY, telescope.SideOfPier,
            rotator, angle, verticallyFlipped);
    }

    // A cropped frame has an unknown origin. Do not infer a centred WCS for it.
    private static bool IsFullFrame(int size, int sensorSize, int bin)
        => size > 0 && sensorSize > 0 && bin > 0 && sensorSize / bin == size;
}

/// <summary>
/// Immutable plate-solved geometry reused by ImageWriterService for subsequent
/// light frames. The reference sky position comes from the J2000 mount, as in
/// Ekos/INDI capture WCS; a new solve is required after an orientation change.
/// </summary>
internal sealed record CaptureWcsCalibration(CaptureWcsContext Context,
        double CD11, double CD12, double CD21, double CD22) {
    internal static CaptureWcsCalibration? FromSolve(CaptureWcsContext context, PlateSolveResult result) {
        if (!result.Success || !double.IsFinite(result.RaHours)
                || !double.IsFinite(result.DecDeg) || Math.Abs(result.DecDeg) > 90) return null;
        WcsInfo wcs;
        if (result.HasCdMatrix) {
            wcs = WcsHeaders.FromCdMatrix(result.RaHours * 15, result.DecDeg,
                result.CrPix1, result.CrPix2,
                result.CD11!.Value, result.CD12!.Value, result.CD21!.Value, result.CD22!.Value,
                context.Width, context.Height);
        } else {
            if (!double.IsFinite(result.ScaleArcsecPerPixel) || result.ScaleArcsecPerPixel <= 0
                    || !double.IsFinite(result.RotationDeg)) return null;
            wcs = WcsHeaders.FromSolveResult(result.RaHours * 15, result.DecDeg,
                result.ScaleArcsecPerPixel, result.RotationDeg, context.Width, context.Height);
        }
        double det = wcs.CD11 * wcs.CD22 - wcs.CD12 * wcs.CD21;
        if (!double.IsFinite(wcs.CD11) || !double.IsFinite(wcs.CD12)
                || !double.IsFinite(wcs.CD21) || !double.IsFinite(wcs.CD22)
                || !double.IsFinite(det) || det == 0) return null;
        // PREVIEW solves may use the relay's vertically flipped pixels. Saved
        // exposures use the original camera buffer, so undo that Y reflection.
        double ySign = context.VerticallyFlipped ? -1 : 1;
        return new(context, wcs.CD11, wcs.CD12 * ySign, wcs.CD21, wcs.CD22 * ySign);
    }

    internal WcsInfo? ForFrame(CaptureWcsContext current, double raHours, double decDeg) {
        var previous = Context;
        if (!ReferenceEquals(previous.Camera, current.Camera)
                || !ReferenceEquals(previous.Telescope, current.Telescope)
                || previous.RigId != current.RigId
                || previous.FocalLengthMm != current.FocalLengthMm
                || previous.PixelSizeX != current.PixelSizeX || previous.PixelSizeY != current.PixelSizeY
                || previous.SensorWidth != current.SensorWidth || previous.SensorHeight != current.SensorHeight
                || previous.PierSide != current.PierSide
                || !ReferenceEquals(previous.Rotator, current.Rotator)
                || previous.RotatorAngle != current.RotatorAngle
                || !double.IsFinite(raHours) || raHours < 0 || raHours >= 24
                || !double.IsFinite(decDeg) || Math.Abs(decDeg) > 90) return null;
        // Rescale each column independently for hardware binning. Solver-side
        // downsampling has already been undone by the solver adapter.
        double x = (double)current.BinX / previous.BinX;
        double y = (double)current.BinY / previous.BinY;
        return WcsHeaders.FromCdMatrix(raHours * 15, decDeg,
            (current.Width + 1) / 2.0, (current.Height + 1) / 2.0,
            CD11 * x, CD12 * y, CD21 * x, CD22 * y, current.Width, current.Height);
    }
}
