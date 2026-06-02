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

        group.MapPost("/solve-latest", async (
                SolveLatestRequest? request,
                ImageRelayService relay,
                PlateSolveService solver,
                EquipmentManager equip,
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
                    SearchRadiusDeg = request?.SearchRadiusDeg ?? 30
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

            double? hintRa = request.HintRa;
            double? hintDec = request.HintDec;
            if (!hintRa.HasValue || !hintDec.HasValue) {
                var tel = equip.Telescope;
                if (tel != null && tel.IsConnected
                        && !double.IsNaN(tel.RightAscension)
                        && !double.IsNaN(tel.Declination)) {
                    hintRa ??= tel.RightAscension;
                    hintDec ??= tel.Declination;
                }
            }

            // Compute FOV from rig focal length + camera pixel size.
            // If the camera isn't connected, try to read pixel count
            // from the FITS header (NAXIS1) and assume a default pixel
            // size of 3.76um (common for IMX sensor family).
            double fovDeg = 0;
            var rig = profiles.ActiveEquipmentProfile;
            double fl = rig?.FocalLengthMm ?? 0;
            if (fl > 0) {
                double pixSize = equip.Camera?.PixelSizeX ?? 3.76;
                // Guess width from filename -- just read FITS header
                // NAXIS1 if we can, otherwise use a safe default.
                int imgWidth = 3008; // fallback
                try {
                    using var fs = new FileStream(request.Path, FileMode.Open, FileAccess.Read);
                    using var br = new BinaryReader(fs);
                    var headerBytes = br.ReadBytes(2880);
                    var headerStr = System.Text.Encoding.ASCII.GetString(headerBytes);
                    var match = System.Text.RegularExpressions.Regex.Match(
                        headerStr, @"NAXIS1\s*=\s*(\d+)");
                    if (match.Success) imgWidth = int.Parse(match.Groups[1].Value);
                } catch { /* keep fallback */ }
                double sensorMm = pixSize * imgWidth / 1000.0;
                fovDeg = 2.0 * Math.Atan(sensorMm / (2.0 * fl)) * (180.0 / Math.PI);
            }

            try {
                var options = new PlateSolveOptions {
                    HintRa = hintRa,
                    HintDec = hintDec,
                    FovDeg = fovDeg,
                    SearchRadiusDeg = request.SearchRadiusDeg ?? 30
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

    /// <summary>Marker type for the ILogger&lt;T&gt; category --
    /// the static endpoint class itself can't be used as a generic
    /// type parameter.</summary>
    public sealed class PlateSolveStatusMarker { }
}
