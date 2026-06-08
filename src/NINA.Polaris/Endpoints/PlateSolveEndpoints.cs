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
using NINA.Polaris.Services;
using NINA.Polaris.Services.PlateSolving;

namespace NINA.Polaris.Endpoints;

/// <summary>
/// FIELD4-4: PREVIEW-tab plate-solve endpoint group. Exposes a
/// single <c>POST /api/platesolve/solve-latest</c> that takes the
/// most recently relayed frame from <see cref="ImageRelayService"/>,
/// writes it to a temp FITS, hands it to <see cref="PlateSolveService"/>,
/// and returns the resolved RA / Dec / scale / rotation inline so
/// the browser can offer one-click <em>sync mount</em>, <em>set target
/// rotation</em> and <em>use as mount rotation</em> buttons.
///
/// Long-poll style (no WebSocket progress events). A solve typically
/// completes in 3-30 s depending on backend + image quality; the
/// activity-bar chip on the client side gives the user feedback
/// during the wait, no need to stream every solver log line.
///
/// Distinct from <see cref="SkyEndpoints"/> <c>slew-and-center</c>:
/// that one orchestrates Slew + Capture + Solve + Sync iteratively
/// and is async-job-shaped. This one is a single-frame, single-shot
/// solve over an already-on-screen image.
/// </summary>
public static class PlateSolveEndpoints {
    public static void MapPlateSolveEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/platesolve");

        // List every plate-solving backend with its install status, so the
        // Settings card can show what's available and let the user pick one.
        group.MapGet("/solvers", (PlateSolveService solver) => {
            return Results.Ok(solver.AllSolvers.Select(s => new {
                id = s.Id,
                name = s.DisplayName,
                available = s.IsAvailable,
                supportsBlind = s.SupportsBlindSolve,
                path = s.SolverPath
            }));
        });

        // Current plate-solve settings (from the active profile).
        group.MapGet("/config", (ProfileService profiles) => {
            var p = profiles.Active;
            return Results.Ok(new {
                primary = p.PlateSolvePrimary,
                downsample = p.PlateSolveDownsample,
                searchRadiusDeg = p.PlateSolveSearchRadiusDeg,
                useBlindFallback = p.PlateSolveUseBlindFallback,
                astapPath = p.AstapPath,
                astapDataDir = p.AstapDataDir
            });
        });

        // Update plate-solve settings. Any field left null is unchanged.
        group.MapPut("/config", (PlateSolveConfigUpdate update, ProfileService profiles) => {
            profiles.UpdateSettings(p => {
                if (!string.IsNullOrWhiteSpace(update.Primary)) p.PlateSolvePrimary = update.Primary!.Trim();
                if (update.Downsample.HasValue) p.PlateSolveDownsample = Math.Clamp(update.Downsample.Value, 0, 4);
                if (update.SearchRadiusDeg.HasValue) p.PlateSolveSearchRadiusDeg = Math.Clamp(update.SearchRadiusDeg.Value, 1, 180);
                if (update.UseBlindFallback.HasValue) p.PlateSolveUseBlindFallback = update.UseBlindFallback.Value;
                // Empty string clears the override (back to auto-detect); null leaves as-is.
                if (update.AstapPath != null) p.AstapPath = string.IsNullOrWhiteSpace(update.AstapPath) ? null : update.AstapPath.Trim();
                if (update.AstapDataDir != null) p.AstapDataDir = string.IsNullOrWhiteSpace(update.AstapDataDir) ? null : update.AstapDataDir.Trim();
            });
            return Results.Ok(new { message = "Plate solve settings saved" });
        });

        group.MapPost("/solve-latest", async (
                SolveLatestRequest? request,
                ImageRelayService relay,
                PlateSolveService solver,
                EquipmentManager equip,
                ProfileService profiles,
                ILogger<PlateSolveStatusMarker> logger,
                CancellationToken ct) => {
            var image = relay.LatestImageData;
            if (image == null) {
                return Results.BadRequest(new {
                    error = "No image available; capture a frame first."
                });
            }

            if (!solver.IsAvailable) {
                return Results.BadRequest(new {
                    error = "No plate solver configured / installed."
                });
            }

            // Hint defaults: prefer the operator's explicit RA/Dec
            // hint in the body; fall back to the mount's current
            // pointing when connected (covers the common case of
            // "I just slewed there, solve where I'm pointing"); and
            // finally to a wide blind solve when neither is
            // available.
            double? hintRa = request?.HintRa;
            double? hintDec = request?.HintDec;
            if (!hintRa.HasValue || !hintDec.HasValue) {
                var tel = equip.Telescope;
                if (tel != null && tel.IsConnected
                        && !double.IsNaN(tel.RightAscension)
                        && !double.IsNaN(tel.Declination)) {
                    hintRa ??= tel.RightAscension;
                    hintDec ??= tel.Declination;
                }
            }

            var tempFits = Path.Combine(Path.GetTempPath(),
                $"polaris_preview_solve_{Guid.NewGuid():N}.fits");

            try {
                FITSWriter.Write(image, tempFits);

                var options = new PlateSolveOptions {
                    HintRa = hintRa,
                    HintDec = hintDec,
                    SearchRadiusDeg = request?.SearchRadiusDeg ?? profiles.Active.PlateSolveSearchRadiusDeg
                };
                logger.LogInformation(
                    "PREVIEW plate solve: hint RA={Ra} Dec={Dec} radius={Rad}°",
                    hintRa, hintDec, options.SearchRadiusDeg);

                var result = await solver.SolveAsync(tempFits, options, ct);

                if (!result.Success) {
                    return Results.Ok(new {
                        success = false,
                        error = result.Error,
                        solverUsed = result.SolverUsed
                    });
                }

                return Results.Ok(new {
                    success = true,
                    raHours = result.RaHours,
                    decDeg = result.DecDeg,
                    raDeg = result.RaDeg,
                    rotationDeg = result.RotationDeg,
                    scaleArcsecPerPixel = result.ScaleArcsecPerPixel,
                    solverUsed = result.SolverUsed
                });
            } catch (OperationCanceledException) {
                return Results.StatusCode(499);  // client closed request
            } catch (Exception ex) {
                logger.LogError(ex, "PREVIEW plate solve failed");
                return Results.Ok(new {
                    success = false,
                    error = ex.Message
                });
            } finally {
                // Clean up the temp FITS, swallow IO races (file
                // may have been deleted by a parallel cleanup or
                // never written if FITSWriter threw before
                // finishing).
                try { File.Delete(tempFits); } catch { }
            }
        });

        // ---- Solve from file (FILES tab) ----
        // The file is already a FITS on disk; no temp-write needed.
        // Body carries the absolute path + optional RA/Dec hints.
        // On success, returns the same shape as solve-latest so the
        // JS client can reuse the same result card + action buttons.
        group.MapPost("/solve-file", async (
                SolveFileRequest request,
                PlateSolveService solver,
                EquipmentManager equip,
                ProfileService profiles,
                ILogger<PlateSolveStatusMarker> logger,
                CancellationToken ct) => {
            if (string.IsNullOrWhiteSpace(request.Path))
                return Results.BadRequest(new { error = "Path is required." });

            if (!File.Exists(request.Path))
                return Results.BadRequest(new { error = $"File not found: {request.Path}" });

            if (!solver.IsAvailable)
                return Results.BadRequest(new { error = "No plate solver configured / installed." });

            // Read FITS headers for hints (RA/Dec, pixel size, focal
            // length, image dimensions). This is the primary source
            // for hint coordinates when solving files from a previous
            // session -- the mount may not be connected or may be
            // pointing somewhere else entirely. ReadHeadersOnly skips
            // the pixel block so it's essentially free on a Pi.
            Dictionary<string, NINA.Image.FileFormat.FITS.FITSHeaderCard>? fitsHeaders = null;
            try {
                using var hdrFs = File.OpenRead(request.Path);
                fitsHeaders = FITSReader.ReadHeadersOnly(hdrFs);
            } catch { /* non-FITS or unreadable, proceed with other hints */ }

            double? hintRa = request.HintRa;
            double? hintDec = request.HintDec;

            // Priority 1: explicit hint from the request body
            // Priority 2: RA/DEC from the FITS header (original
            //   pointing when the frame was captured)
            // Priority 3: current mount position (if connected)
            // Priority 4: no hint (ASTAP blind-solves, slower)
            if (!hintRa.HasValue && fitsHeaders != null) {
                if (fitsHeaders.TryGetValue("RA", out var raCard)
                        && double.TryParse(raCard.Value,
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var raVal) && raVal != 0) {
                    // RA header is in degrees; convert to hours
                    hintRa = raVal / 15.0;
                }
            }
            if (!hintDec.HasValue && fitsHeaders != null) {
                if (fitsHeaders.TryGetValue("DEC", out var decCard)
                        && double.TryParse(decCard.Value,
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var decVal) && Math.Abs(decVal) <= 90) {
                    hintDec = decVal;
                }
            }
            if (!hintRa.HasValue || !hintDec.HasValue) {
                var tel = equip.Telescope;
                if (tel != null && tel.IsConnected
                        && !double.IsNaN(tel.RightAscension)
                        && !double.IsNaN(tel.Declination)) {
                    hintRa ??= tel.RightAscension;
                    hintDec ??= tel.Declination;
                }
            }

            // Compute FOV from the FITS header first (FOCALLEN +
            // XPIXSZ + NAXIS1), falling back to the active rig's
            // focal length + connected camera pixel size.
            double fovDeg = 0;
            double headerFl = 0, headerPix = 0;
            int imgWidth = 0;
            if (fitsHeaders != null) {
                if (fitsHeaders.TryGetValue("FOCALLEN", out var flCard))
                    double.TryParse(flCard.Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out headerFl);
                if (fitsHeaders.TryGetValue("XPIXSZ", out var pxCard))
                    double.TryParse(pxCard.Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out headerPix);
                if (fitsHeaders.TryGetValue("NAXIS1", out var n1Card))
                    int.TryParse(n1Card.Value, out imgWidth);
            }
            double fl = headerFl > 0 ? headerFl
                : (profiles.ActiveEquipmentProfile?.FocalLengthMm ?? 0);
            double pixSize = headerPix > 0 ? headerPix
                : (equip.Camera?.PixelSizeX ?? 3.76);
            if (imgWidth <= 0) imgWidth = 3008;
            if (fl > 0) {
                double sensorMm = pixSize * imgWidth / 1000.0;
                fovDeg = 2.0 * Math.Atan(sensorMm / (2.0 * fl)) * (180.0 / Math.PI);
            }

            try {
                var options = new PlateSolveOptions {
                    HintRa = hintRa,
                    HintDec = hintDec,
                    FovDeg = fovDeg,
                    SearchRadiusDeg = request.SearchRadiusDeg ?? profiles.Active.PlateSolveSearchRadiusDeg
                };
                logger.LogInformation(
                    "FILES plate solve: {Path} hint RA={Ra} Dec={Dec} fov={Fov:F2}° radius={Rad}°",
                    request.Path, hintRa, hintDec, fovDeg, options.SearchRadiusDeg);

                var result = await solver.SolveAsync(request.Path, options, ct);

                if (!result.Success) {
                    return Results.Ok(new {
                        success = false,
                        error = result.Error,
                        solverUsed = result.SolverUsed
                    });
                }
                return Results.Ok(new {
                    success = true,
                    raHours = result.RaHours,
                    decDeg = result.DecDeg,
                    raDeg = result.RaDeg,
                    rotationDeg = result.RotationDeg,
                    scaleArcsecPerPixel = result.ScaleArcsecPerPixel,
                    solverUsed = result.SolverUsed
                });
            } catch (OperationCanceledException) {
                return Results.StatusCode(499);
            } catch (Exception ex) {
                logger.LogError(ex, "FILES plate solve failed for {Path}", request.Path);
                return Results.Ok(new {
                    success = false,
                    error = ex.Message
                });
            }
        });
    }

    /// <summary>POST body for <c>/api/platesolve/solve-latest</c>.
    /// Every field is optional, the endpoint falls back to mount
    /// pointing for the RA/Dec hint and a 30° default for the
    /// search radius.</summary>
    public record SolveLatestRequest(
        double? HintRa,
        double? HintDec,
        double? SearchRadiusDeg);

    /// <summary>POST body for <c>/api/platesolve/solve-file</c>.
    /// Path is the absolute server-side path to the FITS file.</summary>
    public record SolveFileRequest(
        string Path,
        double? HintRa = null,
        double? HintDec = null,
        double? SearchRadiusDeg = null);

    /// <summary>PUT body for <c>/api/platesolve/config</c>. Every field is
    /// optional; null leaves the corresponding setting unchanged. Empty string
    /// on the path fields clears the override (back to auto-detect).</summary>
    public record PlateSolveConfigUpdate(
        string? Primary = null,
        int? Downsample = null,
        double? SearchRadiusDeg = null,
        bool? UseBlindFallback = null,
        string? AstapPath = null,
        string? AstapDataDir = null);

    /// <summary>Marker type for the ILogger&lt;T&gt; category --
    /// the static endpoint class itself can't be used as a generic
    /// type parameter.</summary>
    public sealed class PlateSolveStatusMarker { }
}