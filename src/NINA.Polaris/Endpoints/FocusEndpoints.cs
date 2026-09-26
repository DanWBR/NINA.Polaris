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

using NINA.Polaris.Services;
using NINA.Polaris.Services.Focus;

namespace NINA.Polaris.Endpoints;

/// <summary>
/// FOCUS tab's Manual Assist subtab endpoints. Currently only
/// hosts the Bahtinov mask analyser; future sub-features (donut
/// metric, FWHM gaussian fit) land here too.
/// </summary>
public static class FocusEndpoints {
    public static void MapFocusEndpoints(this IEndpointRouteBuilder app) {
        var group = app.MapGroup("/api/focus");

        // POST /api/focus/bahtinov [{ starX, starY, roiHalf }]
        //
        // Analyses the last frame ImageRelayService cached, whoever put it
        // there. Two producers do: the Manual Assist loop, which captures a
        // frame per tick, and the video stream, which caches every frame it
        // relays at FULL resolution (only the JPEG on the wire is reduced).
        // That is why the same endpoint serves Bahtinov on the live stream
        // without taking an exposure of its own: taking one would be refused
        // anyway, since the stream owns the camera while it runs.
        //
        // starX / starY pin the star instead of taking the brightest one in
        // the frame. On a rich field at video SNR the brightest changes
        // between frames and the offset jitters, so the client posts the
        // pixel the operator clicked. roiHalf sizes the analysis box in frame
        // pixels: long focal length or a large defocus pushes the spikes past
        // the default 100 px.
        group.MapPost("/bahtinov", (BahtinovRequest? req, ImageRelayService relay) => {
            var img = relay.LatestImage;
            if (img == null) {
                return Results.Json(new {
                    ok = false,
                    error = "no recent frame; start the video stream, or the Manual Assist loop, first"
                });
            }

            var pixels = img.PixelData.ToArray();
            var width = img.Width;
            var height = img.Height;
            var roiHalf = BahtinovInput.ClampRoiHalf(req?.RoiHalf);
            var starX = req?.StarX;
            var starY = req?.StarY;

            // A colour mosaic is reduced to pseudo-luminance first; then
            // everything the analyser says is put back into frame pixels, so
            // the client works in one coordinate system either way.
            var mosaic = BahtinovInput.IsMosaic(img.BayerPattern);
            var scale = 1;
            if (mosaic) {
                var (lum, lw, lh) = BahtinovInput.PseudoLuminance2x2(pixels, width, height);
                if (lum.Length > 0) {
                    pixels = lum; width = lw; height = lh; scale = 2;
                    roiHalf = Math.Max(BahtinovInput.MinRoiHalf, roiHalf / 2);
                    if (starX.HasValue) starX = starX.Value / 2;
                    if (starY.HasValue) starY = starY.Value / 2;
                }
            }

            var result = BahtinovAnalyzer.Analyze(
                pixels, width, height, starX: starX, starY: starY, roiHalf: roiHalf);
            var scaled = BahtinovInput.ToFrameScale(result, scale);

            return Results.Ok(new {
                ok = scaled.Ok,
                error = scaled.Error,
                starX = scaled.StarX,
                starY = scaled.StarY,
                roiHalf = scaled.RoiHalf,
                spike1Angle = scaled.Spike1Angle,
                spike1Rho = scaled.Spike1Rho,
                spike2Angle = scaled.Spike2Angle,
                spike2Rho = scaled.Spike2Rho,
                spike3Angle = scaled.Spike3Angle,
                spike3Rho = scaled.Spike3Rho,
                centreSpikeIndex = scaled.CentreSpikeIndex,
                offsetPx = scaled.OffsetPx,
                inFocusThresholdPx = scaled.InFocusThresholdPx,
                intersectionX = scaled.IntersectionX,
                intersectionY = scaled.IntersectionY,
                // Context the UI reports so a surprising number can be read:
                // which grid the sweep ran on, and how big the frame is.
                frameWidth = img.Width,
                frameHeight = img.Height,
                mosaic,
                scale
            });
        });
    }

    public record BahtinovRequest(int? StarX, int? StarY, int? RoiHalf);
}