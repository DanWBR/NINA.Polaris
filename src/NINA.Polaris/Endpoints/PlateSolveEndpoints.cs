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
using NINA.Image.Interfaces;
using NINA.Polaris.Services;
using NINA.Polaris.Services.PlateSolving;
using System.Text.Json;

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

        // What the rig's field of view needs, what is already installed, and
        // what a download would cost. The recommendation is computed from the
        // publishers' own FOV tables (see SolverDatabaseAdvisor).
        group.MapGet("/databases", async (SolverDatabaseService dbs, ProfileService profiles,
                                          EquipmentManager equip,
                                          CancellationToken ct) => {
            using var cat = await dbs.LoadCatalogueAsync(ct);
            var root = cat.RootElement;

            var astap = root.GetProperty("astapDatabases").EnumerateArray().Select(e =>
                new SolverDatabaseAdvisor.AstapDatabase(
                    e.GetProperty("id").GetString() ?? "",
                    e.GetProperty("name").GetString() ?? "",
                    e.GetProperty("minFovDeg").GetDouble(),
                    e.GetProperty("maxFovDeg").GetDouble(),
                    e.GetProperty("approxBytes").GetInt64(),
                    e.TryGetProperty("url", out var u) ? u.GetString() : null,
                    e.TryGetProperty("notes", out var n) ? n.GetString() : null)).ToList();

            var scales = root.GetProperty("astrometryScales").EnumerateArray().Select(e =>
                new SolverDatabaseAdvisor.AstrometryScale(
                    e.GetProperty("scale").GetInt32(),
                    e.GetProperty("minArcmin").GetDouble(),
                    e.GetProperty("maxArcmin").GetDouble())).ToList();

            // FOV from the rig's optics, falling back to what the CONNECTED
            // camera reports.
            //
            // Reading the stored fields alone meant a rig whose camera reports
            // its own pixel size and sensor size, and whose operator therefore
            // never typed them, got fovDeg = 0: the card said "index bands for
            // this field: unknown" and disabled its own Install button while
            // the camera sitting right there was reporting 2.9um and 3840x2160
            // (field, Orange Pi 5 Pro, 2026-08-10). Focal length has no such
            // source, so it still has to come from the profile.
            var rig = profiles.ActiveEquipmentProfile;
            var cam = equip.Camera;
            var pixelUm = rig.CameraPixelSizeUm > 0
                ? rig.CameraPixelSizeUm
                : (cam?.PixelSizeY > 0 ? cam.PixelSizeY : (cam?.PixelSizeX ?? 0));
            var sensorX = rig.CameraMaxX > 0 ? rig.CameraMaxX : (cam?.MaxX ?? 0);
            var sensorY = rig.CameraMaxY > 0 ? rig.CameraMaxY : (cam?.MaxY ?? 0);

            var pixelScale = SolverDatabaseAdvisor.PixelScale(pixelUm, rig.FocalLengthMm);
            var fov = SolverDatabaseAdvisor.FovDegrees(pixelScale, sensorX, sensorY);

            var seriesList = ReadAstrometrySeries(root);
            var recAstap = SolverDatabaseAdvisor.RecommendAstap(astap, fov);
            var usableScales = SolverDatabaseAdvisor.RecommendAstrometryScales(scales, fov);
            var recScales = SolverDatabaseAdvisor.DefaultAstrometryPicks(usableScales);

            return Results.Ok(new {
                fovDeg = Math.Round(fov, 3),
                pixelScaleArcsecPerPixel = Math.Round(pixelScale, 3),
                knownOptics = fov > 0,
                astap = new {
                    catalogue = astap,
                    installed = dbs.InstalledAstapDatabases(),
                    recommended = recAstap?.Id
                },
                astrometry = new {
                    // Every band, with the series that owns it and what it
                    // costs, so the picker can price a selection before the
                    // operator commits to it.
                    scales = scales.Select(s => {
                        var owner = seriesList.FirstOrDefault(x => x.Bands.Any(b => b.Scale == s.Scale));
                        var band = owner?.Bands.First(b => b.Scale == s.Scale);
                        return new {
                            s.Scale, s.MinArcmin, s.MaxArcmin,
                            series = owner?.Id,
                            seriesName = owner?.Name,
                            files = band == null ? 0 : Math.Max(1, band.Tiles),
                            bytes = band?.Bytes ?? 0
                        };
                    }).ToList(),
                    series = seriesList.Select(s => new {
                        id = s.Id, name = s.Name, baseUrl = s.BaseUrl, notes = s.Notes,
                        minScale = s.Bands.Count == 0 ? 0 : s.Bands.Min(b => b.Scale),
                        maxScale = s.Bands.Count == 0 ? 0 : s.Bands.Max(b => b.Scale)
                    }).ToList(),
                    installed = dbs.InstalledAstrometryScales(),
                    // "usable" is astrometry.net's own 10%-to-100% rule;
                    // "recommended" is the subset worth ticking by default.
                    usable = usableScales.Select(s => s.Scale).ToList(),
                    recommended = recScales,
                    recommendedBytes = SolverDatabaseAdvisor.BytesFor(seriesList, recScales)
                },
                job = dbs.State
            });
        });

        // Download + install. ASTAP takes one database id; astrometry.net takes
        // the scale bands to fetch (the recommendation, or the operator's pick).
        group.MapPost("/databases/install", async (SolverDbInstallRequest req,
                                                   SolverDatabaseService dbs,
                                                   CancellationToken ct) => {
            using var cat = await dbs.LoadCatalogueAsync(ct);
            var root = cat.RootElement;
            var target = (req.Target ?? "").Trim().ToLowerInvariant();
            var urls = new List<string>();
            string id;

            if (target == "astap") {
                var entry = root.GetProperty("astapDatabases").EnumerateArray()
                    .FirstOrDefault(e => string.Equals(e.GetProperty("id").GetString(),
                        req.Id, StringComparison.OrdinalIgnoreCase));
                if (entry.ValueKind == JsonValueKind.Undefined)
                    return Results.BadRequest(new { error = $"Unknown ASTAP database '{req.Id}'." });
                var url = entry.TryGetProperty("url", out var u) ? u.GetString() : null;
                if (string.IsNullOrWhiteSpace(url))
                    return Results.BadRequest(new { error = "That database has no download URL." });
                urls.Add(url!);
                id = entry.GetProperty("id").GetString() ?? req.Id ?? "";
                var bytes = entry.GetProperty("approxBytes").GetInt64();
                return dbs.StartInstall(id, target, urls, bytes)
                    ? Results.Ok(new { started = true, id, target })
                    : Results.Conflict(new { error = "Another database install is already running." });
            }

            if (target == "astrometry") {
                var wanted = (req.Scales ?? new List<int>()).Distinct().OrderBy(x => x).ToList();
                if (wanted.Count == 0)
                    return Results.BadRequest(new { error = "No index scales requested." });
                var plan = SolverDatabaseAdvisor.PlanAstrometryDownload(ReadAstrometrySeries(root), wanted);
                urls = plan.Urls.ToList();
                if (urls.Count == 0)
                    return Results.BadRequest(new { error = "No published index files for those scales." });
                id = "scales " + string.Join(",", wanted);
                return dbs.StartInstall(id, target, urls, plan.Bytes)
                    ? Results.Ok(new { started = true, id, target, files = urls.Count, bytes = plan.Bytes })
                    : Results.Conflict(new { error = "Another database install is already running." });
            }

            return Results.BadRequest(new { error = "target must be 'astap' or 'astrometry'." });
        });

        // Progress of the in-flight install, for the card's bar.
        group.MapGet("/databases/status", (SolverDatabaseService dbs) => Results.Ok(dbs.State));

        // Stop a download in flight. Returns cancelled=false once the payload
        // has been handed to the privileged unit: interrupting an unpack would
        // leave a half-written database that the solver would load and fail on.
        group.MapPost("/databases/cancel", (SolverDatabaseService dbs) =>
            Results.Ok(new { cancelled = dbs.Cancel() }));

        // Abort the in-flight solve. The HTTP request that started the solve
        // stays open for its whole duration, so its abort token alone gave the
        // operator no way out: a doomed blind solve-field run had to be waited
        // out or killed by hand (field report). This trips the run's token,
        // which makes the solver kill its process tree and return promptly.
        group.MapPost("/cancel", (PlateSolveProgressService progress) => {
            var cancelled = progress.Cancel();
            return Results.Ok(new { cancelled });
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
                astapDataDir = p.AstapDataDir,
                astrometryApiKey = p.AstrometryApiKey,
                solveFieldPath = p.SolveFieldPath,
                useWsl = p.PlateSolveUseWsl,
                wslDistro = p.PlateSolveWslDistro
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
                if (update.AstrometryApiKey != null) p.AstrometryApiKey = string.IsNullOrWhiteSpace(update.AstrometryApiKey) ? null : update.AstrometryApiKey.Trim();
                if (update.SolveFieldPath != null) p.SolveFieldPath = string.IsNullOrWhiteSpace(update.SolveFieldPath) ? null : update.SolveFieldPath.Trim();
                if (update.UseWsl.HasValue) p.PlateSolveUseWsl = update.UseWsl.Value;
                if (update.WslDistro != null) p.PlateSolveWslDistro = string.IsNullOrWhiteSpace(update.WslDistro) ? null : update.WslDistro.Trim();
            });
            return Results.Ok(new { message = "Plate solve settings saved" });
        });

        group.MapPost("/solve-latest", async (
                SolveLatestRequest? request,
                ImageRelayService relay,
                PlateSolveService solver,
                PlateSolveProgressService progress,
                EquipmentManager equip,
                ProfileService profiles,
                LiveStackingService liveStack,
                ILogger<PlateSolveStatusMarker> logger,
                CancellationToken ct) => {
            // Prefer a sensor-resolution sub over the relayed image.
            //
            // The relayed image is the stacked one, and a live stack at a
            // reduced stacking resolution only exists at that resolution. Half
            // the sampling means half the star size in pixels, and on a rig that
            // is already undersampled the stars drop below one pixel: field
            // report 2026-08-07, every solve failed at 1:2 for an hour and the
            // same sky solved instantly at 1:1. The reduction is there to bound
            // the accumulator's memory, and the solver gains nothing from it.
            //
            // LastFullResolutionFrame is null unless reduction is in force, so
            // at 1:1 (and with no live stack at all) this changes nothing.
            var image = liveStack.LastFullResolutionFrame ?? relay.LatestImageData;
            if (image == null) {
                return Results.BadRequest(new {
                    error = "No image available; capture a frame first."
                });
            }
            if (!ReferenceEquals(image, relay.LatestImageData)) {
                logger.LogInformation(
                    "PREVIEW plate solve: using the {W}x{H} sub instead of the reduced live stack",
                    image.Properties.Width, image.Properties.Height);
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
                // Geometry BEFORE writing: the stamp has to be in the file the
                // solver opens, and the hints have to be on the options. Without
                // either, ASTAP searches every plate scale it knows.
                var geom = PlateSolveHints.From(
                    profiles.ActiveEquipmentProfile?.FocalLengthMm ?? 0,
                    equip.Camera?.PixelSizeY > 0 ? equip.Camera.PixelSizeY : (equip.Camera?.PixelSizeX ?? 0),
                    image.Properties.Height,
                    equip.Camera?.MaxY ?? 0);
                PlateSolveHints.StampFocalLength(image, profiles.ActiveEquipmentProfile?.FocalLengthMm ?? 0);
                FITSWriter.Write(image, tempFits);

                var options = new PlateSolveOptions {
                    HintRa = hintRa,
                    HintDec = hintDec,
                    SearchRadiusDeg = request?.SearchRadiusDeg ?? profiles.Active.PlateSolveSearchRadiusDeg
                };
                PlateSolveHints.Apply(options, geom);
                if (geom.FovDeg > 0) {
                    logger.LogInformation(
                        "PREVIEW plate solve: fov={Fov:F2}° scale={Scale:F2}\"/px"
                        + " (focal {Fl}mm, pixel {Pix:F2}um{Bin})",
                        geom.FovDeg, geom.ScaleArcsecPerPixel,
                        profiles.ActiveEquipmentProfile?.FocalLengthMm ?? 0, geom.EffectivePixelUm,
                        geom.NativeBinning > 1 ? $", native x{geom.NativeBinning}" : "");
                }
                bool silent = request?.Silent ?? false;
                logger.LogInformation(
                    "PREVIEW plate solve: hint RA={Ra} Dec={Dec} radius={Rad}°{Silent}",
                    hintRa, hintDec, options.SearchRadiusDeg, silent ? " (silent)" : "");

                if (!silent) progress.Begin("PREVIEW", ct);
                PlateSolveResult result;
                try {
                    // A silent solve is an internal step with no UI to cancel
                    // from, so it stays on the raw request token.
                    result = await solver.SolveAsync(tempFits, options,
                        silent ? ct : progress.Token,
                        silent ? null : progress.Append);
                } finally { if (!silent) progress.End(); }

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
                    solverUsed = result.SolverUsed,
                    // CD matrix (deg/px): carries the frame's true orientation
                    // INCLUDING parity (mirror), which the scalar rotation
                    // cannot. The SKY FOV rectangles use it to draw the real
                    // sensor footprint. Nullable — some solvers (a.net online)
                    // may not provide it.
                    cd11 = result.CD11, cd12 = result.CD12,
                    cd21 = result.CD21, cd22 = result.CD22
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

        // ---- Aux-camera plate solve (parallel to the main solve) ----
        // Captures one frame from the connected AUX camera, solves it, and
        // returns RA/Dec/rotation/scale. The aux rides the same mount, so
        // the value is the actual sky ROTATION + scale the aux frame will
        // come out with — the operator sees the real pink aux FOV on SKY
        // rather than an assumed angle. Independent hardware from the main
        // camera, so it can run concurrently with the main solve.
        group.MapPost("/solve-aux", async (
                SolveAuxRequest? request,
                PlateSolveService solver,
                PlateSolveProgressService progress,
                EquipmentManager equip,
                ProfileService profiles,
                ILogger<PlateSolveStatusMarker> logger,
                CancellationToken ct) => {
            var cam = equip.AuxCamera;
            if (cam == null || !cam.IsConnected) {
                return Results.BadRequest(new { error = "Aux camera not connected." });
            }
            if (!solver.IsAvailable) {
                return Results.BadRequest(new { error = "No plate solver configured / installed." });
            }

            double? hintRa = request?.HintRa, hintDec = request?.HintDec;
            if (!hintRa.HasValue || !hintDec.HasValue) {
                var tel = equip.Telescope;
                if (tel != null && tel.IsConnected
                        && !double.IsNaN(tel.RightAscension) && !double.IsNaN(tel.Declination)) {
                    hintRa ??= tel.RightAscension;
                    hintDec ??= tel.Declination;
                }
            }

            var tempFits = Path.Combine(Path.GetTempPath(),
                $"polaris_aux_solve_{Guid.NewGuid():N}.fits");
            try {
                var exposure = request?.Exposure is > 0 ? request!.Exposure!.Value : 3.0;
                var opts = new CaptureOptions(
                    Gain: request?.Gain,
                    BinX: request?.Binning, BinY: request?.Binning,
                    ImageType: "Light");
                logger.LogInformation(
                    "AUX plate solve: capture {Exp}s gain={Gain} bin={Bin}, hint RA={Ra} Dec={Dec}",
                    exposure, request?.Gain, request?.Binning, hintRa, hintDec);

                // Serialize with the aux capture loop: both share the aux
                // camera's single in-flight exposure TCS, so an ungated solve
                // here racing an aux frame would clobber it and one capture
                // would time out (~exposure+60s "no BLOB"). Same gate the aux
                // loop + aux focus use.
                var image = await AuxCameraCaptureGate.RunAsync(
                    () => cam.CaptureAsync(exposure, opts, ct), ct);

                // The aux train has its own focal length; using the main one
                // here would hand the solver a hint that is confidently wrong,
                // which is worse than none.
                var auxFl = profiles.ActiveEquipmentProfile?.AuxFocalLengthMm ?? 0;
                var geom = PlateSolveHints.From(
                    auxFl,
                    cam.PixelSizeY > 0 ? cam.PixelSizeY : cam.PixelSizeX,
                    image.Properties.Height,
                    cam.MaxY);
                PlateSolveHints.StampFocalLength(image, auxFl);
                FITSWriter.Write(image, tempFits);

                var options = new PlateSolveOptions {
                    HintRa = hintRa, HintDec = hintDec,
                    SearchRadiusDeg = request?.SearchRadiusDeg ?? profiles.Active.PlateSolveSearchRadiusDeg
                };
                PlateSolveHints.Apply(options, geom);
                progress.Begin("AUX", ct);
                PlateSolveResult result;
                try { result = await solver.SolveAsync(tempFits, options, progress.Token, progress.Append); }
                finally { progress.End(); }

                if (!result.Success) {
                    return Results.Ok(new {
                        success = false, error = result.Error, solverUsed = result.SolverUsed
                    });
                }
                return Results.Ok(new {
                    success = true,
                    raHours = result.RaHours, decDeg = result.DecDeg, raDeg = result.RaDeg,
                    rotationDeg = result.RotationDeg,
                    scaleArcsecPerPixel = result.ScaleArcsecPerPixel,
                    solverUsed = result.SolverUsed
                });
            } catch (OperationCanceledException) {
                return Results.StatusCode(499);
            } catch (Exception ex) {
                logger.LogError(ex, "AUX plate solve failed");
                return Results.Ok(new { success = false, error = ex.Message });
            } finally {
                try { File.Delete(tempFits); } catch { }
            }
        });

        // ---- Annotate the latest frame (LIVE / PREVIEW) ----
        // Solve the most recent frame, then cone-search the DSO catalog over
        // the resulting field and project each object to image pixels so the
        // client can label what's in the frame.
        group.MapPost("/annotate-latest", async (
                AnnotateLatestRequest? request,
                ImageRelayService relay,
                PlateSolveService solver,
                PlateSolveProgressService progress,
                EquipmentManager equip,
                ProfileService profiles,
                NINA.Polaris.Services.Sky.DsoCatalog dso,
                ILogger<PlateSolveStatusMarker> logger,
                CancellationToken ct) => {
            var image = relay.LatestImageData;
            if (image == null)
                return Results.BadRequest(new { error = "No image available; capture a frame first." });
            if (!solver.IsAvailable)
                return Results.BadRequest(new { error = "No plate solver configured / installed." });

            int width = image.Properties.Width, height = image.Properties.Height;

            double? hintRa = request?.HintRa, hintDec = request?.HintDec;
            if (!hintRa.HasValue || !hintDec.HasValue) {
                var tel = equip.Telescope;
                if (tel != null && tel.IsConnected
                        && !double.IsNaN(tel.RightAscension) && !double.IsNaN(tel.Declination)) {
                    hintRa ??= tel.RightAscension;
                    hintDec ??= tel.Declination;
                }
            }

            var tempFits = Path.Combine(Path.GetTempPath(), $"polaris_annotate_{Guid.NewGuid():N}.fits");
            try {
                var geom = PlateSolveHints.From(
                    profiles.ActiveEquipmentProfile?.FocalLengthMm ?? 0,
                    equip.Camera?.PixelSizeY > 0 ? equip.Camera.PixelSizeY : (equip.Camera?.PixelSizeX ?? 0),
                    image.Properties.Height,
                    equip.Camera?.MaxY ?? 0);
                PlateSolveHints.StampFocalLength(image, profiles.ActiveEquipmentProfile?.FocalLengthMm ?? 0);
                FITSWriter.Write(image, tempFits);
                var options = new PlateSolveOptions {
                    HintRa = hintRa, HintDec = hintDec,
                    SearchRadiusDeg = request?.SearchRadiusDeg ?? profiles.Active.PlateSolveSearchRadiusDeg
                };
                PlateSolveHints.Apply(options, geom);
                progress.Begin("ANNOTATE", ct);
                PlateSolveResult result;
                try { result = await solver.SolveAsync(tempFits, options, progress.Token, progress.Append); }
                finally { progress.End(); }

                if (!result.Success)
                    return Results.Ok(new { success = false, error = result.Error, solverUsed = result.SolverUsed });

                var objects = await ProjectAnnotationsAsync(dso, result, width, height,
                    request?.Flip ?? false, request?.MagLimit ?? 14.0,
                    request?.ExtraRotationDeg ?? 0.0);

                return Results.Ok(new {
                    success = true, width, height,
                    raHours = result.RaHours, decDeg = result.DecDeg,
                    rotationDeg = result.RotationDeg, scaleArcsecPerPixel = result.ScaleArcsecPerPixel,
                    solverUsed = result.SolverUsed,
                    count = objects.Count, objects
                });
            } catch (OperationCanceledException) {
                return Results.StatusCode(499);
            } catch (Exception ex) {
                logger.LogError(ex, "Annotate failed");
                return Results.Ok(new { success = false, error = ex.Message });
            } finally {
                try { File.Delete(tempFits); } catch { }
            }
        });

        // ---- Annotate a file on disk (STUDIO / FILES viewer) ----
        // Solve a saved FITS, then project the DSO catalog onto its full-res
        // pixel grid so the FILES image viewer can overlay labels. Mirrors
        // annotate-latest but sources the frame + its dimensions from the file
        // (NAXIS1/NAXIS2) instead of the live relay, and supports the same
        // flip + extraRotationDeg test knobs.
        group.MapPost("/annotate-file", async (
                AnnotateFileRequest request,
                PlateSolveService solver,
                ProfileService profiles,
                EquipmentManager equip,
                NINA.Polaris.Services.Sky.DsoCatalog dso,
                PlateSolveProgressService progress,
                ILogger<PlateSolveStatusMarker> logger,
                CancellationToken ct) => {
            if (string.IsNullOrWhiteSpace(request.Path))
                return Results.BadRequest(new { error = "Path is required." });
            if (!File.Exists(request.Path))
                return Results.BadRequest(new { error = $"File not found: {request.Path}" });
            if (!solver.IsAvailable)
                return Results.BadRequest(new { error = "No plate solver configured / installed." });

            // Headers give the RA/Dec hint and the full-resolution dimensions
            // the projection must use (the viewer maps these onto its own
            // possibly-downscaled preview).
            int width = 0, height = 0;
            double? hintRa = request.HintRa, hintDec = request.HintDec;
            // If the FITS already carries a full WCS (CRVAL/CRPIX/CD), reuse it
            // and skip re-solving entirely — re-running ASTAP on a full-res frame
            // is slow and was timing out; the embedded WCS is exactly what a
            // prior solve produced and is enough to place the annotations.
            NINA.Image.FileFormat.FITS.WcsInfo? headerWcs = null;
            try {
                using var hdrFs = File.OpenRead(request.Path);
                var hdr = FITSReader.ReadHeadersOnly(hdrFs);
                if (hdr != null) {
                    if (hdr.TryGetValue("NAXIS1", out var n1)) int.TryParse(n1.Value, out width);
                    if (hdr.TryGetValue("NAXIS2", out var n2)) int.TryParse(n2.Value, out height);
                    if (!hintRa.HasValue && hdr.TryGetValue("RA", out var raC)
                        && double.TryParse(raC.Value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var raV) && raV != 0)
                        hintRa = raV / 15.0;
                    if (!hintDec.HasValue && hdr.TryGetValue("DEC", out var decC)
                        && double.TryParse(decC.Value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var decV) && Math.Abs(decV) <= 90)
                        hintDec = decV;
                    headerWcs = NINA.Image.FileFormat.FITS.WcsHeaders.Read(hdr);
                }
            } catch { /* non-FITS or unreadable header block */ }

            // Dimensions are essential for projection; if the header lacked them
            // (or it's a non-FITS image), read the pixel block to get them.
            if (width <= 0 || height <= 0) {
                try {
                    using var fs = File.OpenRead(request.Path);
                    var img = FITSReader.Read(fs);
                    width = img.Properties.Width; height = img.Properties.Height;
                } catch { return Results.Ok(new { success = false, error = "Could not read image dimensions." }); }
            }

            if (!hintRa.HasValue || !hintDec.HasValue) {
                var tel = equip.Telescope;
                if (tel != null && tel.IsConnected
                        && !double.IsNaN(tel.RightAscension) && !double.IsNaN(tel.Declination)) {
                    hintRa ??= tel.RightAscension; hintDec ??= tel.Declination;
                }
            }

            // Fast path: the FITS already has a usable WCS — project straight from
            // it, no solver call. This is instant and avoids the re-solve timeout.
            if (headerWcs != null
                && (headerWcs.CD11 * headerWcs.CD22 - headerWcs.CD12 * headerWcs.CD21) != 0) {
                double scale = Math.Sqrt(headerWcs.CD11 * headerWcs.CD11
                                         + headerWcs.CD21 * headerWcs.CD21) * 3600.0;
                double rotDeg = Math.Atan2(headerWcs.CD21, headerWcs.CD11) * 180.0 / Math.PI;
                var hdrResult = new PlateSolveResult {
                    Success = true, SolverUsed = "WCS (header)",
                    RaDeg = headerWcs.RaDeg, RaHours = headerWcs.RaDeg / 15.0,
                    DecDeg = headerWcs.DecDeg,
                    ScaleArcsecPerPixel = scale, RotationDeg = rotDeg,
                    CD11 = headerWcs.CD11, CD12 = headerWcs.CD12,
                    CD21 = headerWcs.CD21, CD22 = headerWcs.CD22,
                    CrPix1 = headerWcs.RefPixelX, CrPix2 = headerWcs.RefPixelY,
                };
                var hdrObjects = await ProjectAnnotationsAsync(dso, hdrResult, width, height,
                    request.Flip ?? false, request.MagLimit ?? 14.0, request.ExtraRotationDeg ?? 0.0);
                return Results.Ok(new {
                    success = true, width, height,
                    raHours = hdrResult.RaHours, decDeg = hdrResult.DecDeg,
                    rotationDeg = hdrResult.RotationDeg, scaleArcsecPerPixel = hdrResult.ScaleArcsecPerPixel,
                    solverUsed = hdrResult.SolverUsed,
                    count = hdrObjects.Count, objects = hdrObjects
                });
            }

            try {
                var options = new PlateSolveOptions {
                    HintRa = hintRa, HintDec = hintDec,
                    SearchRadiusDeg = request.SearchRadiusDeg ?? profiles.Active.PlateSolveSearchRadiusDeg
                };
                progress.Begin("ANNOTATE-FILE", ct);
                PlateSolveResult result;
                try { result = await solver.SolveAsync(request.Path, options, progress.Token, progress.Append); }
                finally { progress.End(); }

                if (!result.Success)
                    return Results.Ok(new { success = false, error = result.Error, solverUsed = result.SolverUsed });

                var objects = await ProjectAnnotationsAsync(dso, result, width, height,
                    request.Flip ?? false, request.MagLimit ?? 14.0, request.ExtraRotationDeg ?? 0.0);

                return Results.Ok(new {
                    success = true, width, height,
                    raHours = result.RaHours, decDeg = result.DecDeg,
                    rotationDeg = result.RotationDeg, scaleArcsecPerPixel = result.ScaleArcsecPerPixel,
                    solverUsed = result.SolverUsed,
                    count = objects.Count, objects
                });
            } catch (OperationCanceledException) {
                return Results.StatusCode(499);
            } catch (Exception ex) {
                logger.LogError(ex, "Annotate-file failed for {Path}", request.Path);
                return Results.Ok(new { success = false, error = ex.Message });
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
                PlateSolveProgressService progress,
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
            // YPIXSZ + NAXIS2), falling back to the active rig's
            // focal length + connected camera pixel size. ASTAP's -fov
            // is the field *height* (vertical), so derive it from the
            // image HEIGHT + Y pixel size — using the width over-states
            // the FOV on any non-square sensor and makes a hinted solve
            // fail at the wrong scale (N.I.N.A. desktop passes FoVH too).
            double fovDeg = 0;
            double headerFl = 0, headerPix = 0;
            int imgHeight = 0;
            if (fitsHeaders != null) {
                if (fitsHeaders.TryGetValue("FOCALLEN", out var flCard))
                    double.TryParse(flCard.Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out headerFl);
                // Prefer YPIXSZ for the vertical scale; fall back to XPIXSZ
                // (square pixels in practice) when the header omits it.
                if (fitsHeaders.TryGetValue("YPIXSZ", out var pyCard))
                    double.TryParse(pyCard.Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out headerPix);
                if (headerPix <= 0 && fitsHeaders.TryGetValue("XPIXSZ", out var pxCard))
                    double.TryParse(pxCard.Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out headerPix);
                if (fitsHeaders.TryGetValue("NAXIS2", out var n2Card))
                    int.TryParse(n2Card.Value, out imgHeight);
            }
            double fl = headerFl > 0 ? headerFl
                : (profiles.ActiveEquipmentProfile?.FocalLengthMm ?? 0);
            double pixSize = headerPix > 0 ? headerPix
                : (equip.Camera?.PixelSizeY ?? equip.Camera?.PixelSizeX ?? 3.76);
            if (imgHeight <= 0) imgHeight = 3008;

            // A reduced frame has a BIGGER effective pixel, and nothing in the
            // header says so: XPIXSZ/YPIXSZ keep reporting the sensor's native
            // pitch. Mixing that pitch with the reduced NAXIS2 put both hints
            // out by exactly the reduction factor, so a live stack at 1:2 told
            // ASTAP the field was half its real size and every solve failed on
            // frames that were fine. Field report 2026-08-06: 100% failure at
            // 1:2, instant solve at 1:1, same sky, same mount, same session.
            //
            // Recover the factor from what the frame covers against what the
            // sensor has, and only trust it near a whole number: a cropped or
            // ROI frame is smaller for a different reason and must not be
            // rescaled.
            long sensorHeight = equip.Camera?.MaxY ?? 0;
            if (sensorHeight > imgHeight && imgHeight > 0) {
                double factor = (double)sensorHeight / imgHeight;
                double nearest = Math.Round(factor);
                if (nearest >= 2 && Math.Abs(factor - nearest) < 0.02) {
                    pixSize *= nearest;
                    logger.LogInformation(
                        "Plate solve: {H}px frame from a {S}px sensor, so the effective "
                        + "pixel is {Pix:F2}um (native x{N}) for the scale hints",
                        imgHeight, sensorHeight, pixSize, nearest);
                }
            }

            double scaleArcsec = 0;
            if (fl > 0) {
                // Pixel scale (arcsec/pixel) is dimension-independent and the
                // tightest hint for Astrometry.net / PlateSolve3, keeping
                // solve-field from scanning index files across every scale.
                // ASTAP still uses fovDeg (field height) below.
                scaleArcsec = 206.2648 * pixSize / fl;
                double sensorMm = pixSize * imgHeight / 1000.0;
                fovDeg = 2.0 * Math.Atan(sensorMm / (2.0 * fl)) * (180.0 / Math.PI);
            }

            try {
                var options = new PlateSolveOptions {
                    HintRa = hintRa,
                    HintDec = hintDec,
                    FovDeg = fovDeg,
                    ScaleArcsecPerPixel = scaleArcsec,
                    SearchRadiusDeg = request.SearchRadiusDeg ?? profiles.Active.PlateSolveSearchRadiusDeg
                };
                logger.LogInformation(
                    "FILES plate solve: {Path} hint RA={Ra} Dec={Dec} fov={Fov:F2}° scale={Scale:F2}\"/px radius={Rad}°",
                    request.Path, hintRa, hintDec, fovDeg, scaleArcsec, options.SearchRadiusDeg);

                progress.Begin("FILES", ct);
                PlateSolveResult result;
                try {
                    result = await solver.SolveAsync(request.Path, options, progress.Token, progress.Append);
                } finally { progress.End(); }

                if (!result.Success) {
                    return Results.Ok(new {
                        success = false,
                        error = result.Error,
                        solverUsed = result.SolverUsed
                    });
                }

                // Persist the solution into the FITS headers (WCS) so the file
                // is plate-solved on disk for PCC, re-solve, external tools, etc.
                // ASTAP already writes WCS in place (-update / proxy re-stamp);
                // for the other backends we synthesise a TAN WCS from the result
                // and rewrite the file. Opt out with writeWcsHeaders=false.
                bool wcsWritten = string.Equals(result.SolverUsed, "astap", StringComparison.OrdinalIgnoreCase);
                if ((request.WriteWcsHeaders ?? true) && !wcsWritten
                        && result.ScaleArcsecPerPixel > 0) {
                    try {
                        var raDeg = result.RaDeg != 0 ? result.RaDeg : result.RaHours * 15.0;
                        NINA.Image.ImageData.BaseImageData img;
                        using (var fs = File.OpenRead(request.Path)) img = FITSReader.Read(fs);
                        // Prefer the solver's real CD matrix (carries parity /
                        // mirror); only reconstruct from (scale, rotation) — which
                        // assumes a fixed handedness and can mirror the RA axis — as
                        // a last resort when no CD matrix was reported.
                        var wcs = result.HasCdMatrix
                            ? WcsHeaders.FromCdMatrix(
                                raDeg, result.DecDeg, result.CrPix1, result.CrPix2,
                                result.CD11!.Value, result.CD12!.Value,
                                result.CD21!.Value, result.CD22!.Value,
                                img.Properties.Width, img.Properties.Height)
                            : WcsHeaders.FromSolveResult(
                                raDeg, result.DecDeg, result.ScaleArcsecPerPixel, result.RotationDeg,
                                img.Properties.Width, img.Properties.Height);
                        // ImageProperties is an init-only record; copy with the WCS set.
                        var stamped = new NINA.Image.ImageData.BaseImageData(
                            img.Data, img.Properties with { Wcs = wcs }, img.MetaData);
                        FITSWriter.Write(stamped, request.Path);
                        wcsWritten = true;
                    } catch (Exception wex) {
                        logger.LogWarning(wex, "WCS header write failed for {Path}", request.Path);
                    }
                }

                return Results.Ok(new {
                    success = true,
                    raHours = result.RaHours,
                    decDeg = result.DecDeg,
                    raDeg = result.RaDeg,
                    rotationDeg = result.RotationDeg,
                    scaleArcsecPerPixel = result.ScaleArcsecPerPixel,
                    solverUsed = result.SolverUsed,
                    cd11 = result.CD11, cd12 = result.CD12,
                    cd21 = result.CD21, cd22 = result.CD22,
                    wcsWritten
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

    /// <summary>The astrometry.net series as the catalogue declares them.
    ///
    /// <para>Read in one place because the listing endpoint and the install
    /// endpoint have to agree exactly on which files a band is made of. They
    /// did not: the lister showed bands the installer then asked the mirror for
    /// under names that do not exist.</para></summary>
    private static List<SolverDatabaseAdvisor.AstrometrySeries> ReadAstrometrySeries(JsonElement root) {
        if (!root.TryGetProperty("astrometrySeries", out var arr)
            || arr.ValueKind != JsonValueKind.Array)
            return new List<SolverDatabaseAdvisor.AstrometrySeries>();

        return arr.EnumerateArray().Select(e => new SolverDatabaseAdvisor.AstrometrySeries(
            e.GetProperty("id").GetString() ?? "",
            e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
            e.TryGetProperty("prefix", out var p) ? p.GetString() ?? "" : "",
            e.TryGetProperty("baseUrl", out var b) ? b.GetString() ?? "" : "",
            e.TryGetProperty("notes", out var no) ? no.GetString() : null,
            e.TryGetProperty("bands", out var bands) && bands.ValueKind == JsonValueKind.Array
                ? bands.EnumerateArray().Select(x => new SolverDatabaseAdvisor.AstrometryBand(
                      x.GetProperty("scale").GetInt32(),
                      x.TryGetProperty("tiles", out var t) ? t.GetInt32() : 0,
                      x.TryGetProperty("bytes", out var by) ? by.GetInt64() : 0)).ToList()
                : new List<SolverDatabaseAdvisor.AstrometryBand>()
        )).ToList();
    }

    /// <summary>Cone-search the DSO catalog over a solved field and project each
    /// hit onto the image's pixel grid. Shared by annotate-latest (live frame)
    /// and annotate-file (saved frame). <paramref name="extraRotationDeg"/> is the
    /// convention/test offset added to the solver rotation in the projector.</summary>
    private static async Task<List<object>> ProjectAnnotationsAsync(
            NINA.Polaris.Services.Sky.DsoCatalog dso, PlateSolveResult result,
            int width, int height, bool flip, double magLimit, double extraRotationDeg) {
        var objects = new List<object>();
        if (!dso.IsAvailable || result.ScaleArcsecPerPixel <= 0) return objects;

        // Cone radius = half the frame diagonal + 10% margin.
        double diagPx = Math.Sqrt((double)width * width + (double)height * height);
        double radiusDeg = diagPx * result.ScaleArcsecPerPixel / 3600.0 / 2.0 * 1.10;
        double margin = 0.02 * Math.Max(width, height);

        var hits = await dso.QueryRegionAsync(result.RaHours, result.DecDeg,
            Math.Max(0.05, radiusDeg), magLimit, 300);

        // Prefer the full WCS CD matrix when the solver gave us one: it encodes
        // rotation AND parity (mirror/flip), so it lands on the right objects for
        // mirrored optical trains where the scalar-rotation projector (which
        // assumes north-up/east-left) is off by a flip. The solver's pixel grid
        // is the FITS convention (1-based, CRPIX-centred); the viewer draws those
        // pixels directly (no Y flip), so we map 1-based → 0-based display with a
        // -0.5 shift and otherwise pass the pixel straight through. Verified
        // numerically against a real mirrored SV605CC frame.
        NINA.Image.FileFormat.FITS.WcsInfo? wcs = null;
        if (result.HasCdMatrix) {
            wcs = new NINA.Image.FileFormat.FITS.WcsInfo {
                RaDeg = result.RaDeg != 0 ? result.RaDeg : result.RaHours * 15.0,
                DecDeg = result.DecDeg,
                RefPixelX = result.CrPix1 > 0 ? result.CrPix1 : (width + 1) / 2.0,
                RefPixelY = result.CrPix2 > 0 ? result.CrPix2 : (height + 1) / 2.0,
                CD11 = result.CD11!.Value, CD12 = result.CD12!.Value,
                CD21 = result.CD21!.Value, CD22 = result.CD22!.Value,
            };
        }

        foreach (var o in hits) {
            double x, y;
            if (wcs != null) {
                var (px, py) = wcs.RaDecToPixel(o.RaHours * 15.0, o.DecDeg);
                if (double.IsNaN(px) || double.IsNaN(py)) continue;
                x = px - 0.5; y = py - 0.5;   // 1-based FITS pixel → 0-based display, no Y flip
            } else {
                var p = AnnotationProjector.Project(result.RaHours, result.DecDeg,
                    result.ScaleArcsecPerPixel, result.RotationDeg, width, height, flip,
                    o.RaHours, o.DecDeg, extraRotationDeg);
                if (p == null) continue;
                (x, y) = p.Value;
            }
            if (x < -margin || y < -margin || x > width + margin || y > height + margin) continue;
            // Marker radius in image pixels = half the object's angular size,
            // converted through the solved plate scale, so the circle hugs the
            // object instead of being a fixed dot. sizeArcmin is the major axis
            // (diameter); 0/unknown → 0 (UI falls back to a small default).
            double sizeArcmin = o.SizeArcmin ?? 0;
            double radiusPx = sizeArcmin > 0
                ? (sizeArcmin * 60.0 / 2.0) / result.ScaleArcsecPerPixel
                : 0;
            objects.Add(new {
                name = o.Name, commonName = o.CommonName,
                x, y, type = o.Type, magnitude = o.Magnitude,
                sizeArcmin = o.SizeArcmin, radiusPx
            });
        }
        return objects;
    }

    /// <summary>POST body for <c>/api/platesolve/solve-latest</c>.
    /// Every field is optional, the endpoint falls back to mount
    /// pointing for the RA/Dec hint and a 30° default for the
    /// search radius. <c>Silent</c> suppresses the live progress
    /// console stream (used by the background per-frame solve that
    /// keeps the red FOV rectangle glued to the solved sky position
    /// during live-stacking / autorun / plan — the operator never
    /// asked for it, so it must not flood the solver-log panel).</summary>
    public record SolveLatestRequest(
        double? HintRa,
        double? HintDec,
        double? SearchRadiusDeg,
        bool? Silent = null);

    /// <summary>POST body for <c>/api/platesolve/solve-aux</c>. Captures one
    /// frame from the aux camera with these settings, then solves it. Hints
    /// default to the mount's current pointing when omitted.</summary>
    public record SolveAuxRequest(
        double? HintRa = null,
        double? HintDec = null,
        double? SearchRadiusDeg = null,
        double? Exposure = null,
        int? Gain = null,
        int? Binning = null);

    /// <summary>POST body for <c>/api/platesolve/annotate-latest</c>. Adds the
    /// DSO label options on top of the solve hints: a magnitude floor and a
    /// horizontal-flip toggle for mirrored optical trains.</summary>
    public record AnnotateLatestRequest(
        double? HintRa = null,
        double? HintDec = null,
        double? SearchRadiusDeg = null,
        double? MagLimit = null,
        bool? Flip = null,
        double? ExtraRotationDeg = null);

    /// <summary>POST body for <c>/api/platesolve/annotate-file</c>. Same options
    /// as annotate-latest plus the absolute server-side path of the FITS to
    /// solve and label.</summary>
    public record AnnotateFileRequest(
        string Path,
        double? HintRa = null,
        double? HintDec = null,
        double? SearchRadiusDeg = null,
        double? MagLimit = null,
        bool? Flip = null,
        double? ExtraRotationDeg = null);

    /// <summary>POST body for <c>/api/platesolve/solve-file</c>.
    /// Path is the absolute server-side path to the FITS file.</summary>
    public record SolveFileRequest(
        string Path,
        double? HintRa = null,
        double? HintDec = null,
        double? SearchRadiusDeg = null,
        bool? WriteWcsHeaders = null);

    /// <summary>PUT body for <c>/api/platesolve/config</c>. Every field is
    /// optional; null leaves the corresponding setting unchanged. Empty string
    /// on the path fields clears the override (back to auto-detect).</summary>
    public record PlateSolveConfigUpdate(
        string? Primary = null,
        int? Downsample = null,
        double? SearchRadiusDeg = null,
        bool? UseBlindFallback = null,
        string? AstapPath = null,
        string? AstapDataDir = null,
        string? AstrometryApiKey = null,
        string? SolveFieldPath = null,
        bool? UseWsl = null,
        string? WslDistro = null);

    /// <summary>POST body for <c>/api/platesolve/databases/install</c>.
    /// <c>Target</c> is "astap" (then <c>Id</c> names the database) or
    /// "astrometry" (then <c>Scales</c> lists the index bands).</summary>
    public record SolverDbInstallRequest(string? Target, string? Id = null, List<int>? Scales = null);

    /// <summary>Marker type for the ILogger&lt;T&gt; category --
    /// the static endpoint class itself can't be used as a generic
    /// type parameter.</summary>
    public sealed class PlateSolveStatusMarker { }
}