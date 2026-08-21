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

using CosineKitty;
using NINA.Polaris.Services;

namespace NINA.Polaris.Endpoints;

public static class SkyEndpoints {
    public static void MapSkyEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/sky");

        group.MapPost("/slew-and-center", (SlewAndCenterRequest request,
            SlewCenterService slewCenter, MountSafetyGuardService guard,
            EquipmentManager equip, ProfileService profiles) => {
            // Safety gate #4: after a mount-safety trip, require a re-home first.
            if (!request.Force && guard.RequireHomeAfterTrip) {
                return Results.Json(new {
                    needsConfirm = true,
                    kind = "safety-stop",
                    reason = "The mount safety guard has tripped. Find Home to re-establish a "
                        + "clean pointing model before slewing, or confirm to override.",
                }, statusCode: 409);
            }

            // Safety gate #2: flag a large / near-meridian slew, or a target below
            // the altitude floor, for confirmation before the mount moves. Compute
            // server-side (has the site + LST). Skip when the caller already
            // confirmed (Force).
            if (!request.Force) {
                var scope = equip.Telescope;
                double mra = double.NaN, mdec = double.NaN;
                if (scope is { IsConnected: true }) { mra = scope.RightAscension; mdec = scope.Declination; }
                // Per-rig thresholds (null ⇒ built-in defaults). The floor still
                // flows through the guard so the global SafetyStopEnabled master
                // switch can zero it out.
                var rig = profiles.ActiveEquipmentProfile;
                double largeMoveDeg = rig?.SlewConfirmDeg ?? MountSlewSafety.LargeMoveDeg;
                var v = MountSlewSafety.Evaluate(
                    mra, mdec, request.Ra, request.Dec,
                    profiles.Active.Latitude, profiles.Active.Longitude, DateTime.UtcNow,
                    guard.AltitudeFloorDeg, largeMoveDeg);
                if (v.Warn) {
                    return Results.Json(new {
                        needsConfirm = true,
                        kind = "slew-warning",
                        reason = v.Reason,
                        moveDeg = double.IsNaN(v.MoveDeg) ? (double?)null : v.MoveDeg,
                        targetAltDeg = v.TargetAltDeg,
                        haMinutes = v.HaMinutes,
                    }, statusCode: 409);
                }
            }

            var job = slewCenter.StartJob(request.Ra, request.Dec, request.ToleranceArcsec,
                request.CenterOnly, request.Force);
            return Results.Accepted(value: new {
                jobId = job.Id,
                target = new { request.Ra, request.Dec },
                toleranceArcsec = request.ToleranceArcsec,
                state = job.State.ToString().ToLowerInvariant()
            });
        });

        group.MapGet("/slew-and-center/{jobId}/status", (string jobId,
            SlewCenterService slewCenter) => {
            var job = slewCenter.GetJob(jobId);
            if (job == null)
                return Results.NotFound(new { error = "Job not found" });

            return Results.Ok(new {
                jobId = job.Id,
                state = job.State.ToString().ToLowerInvariant(),
                iteration = job.Iteration,
                targetRa = job.TargetRa,
                targetDec = job.TargetDec,
                actualRa = job.ActualRa,
                actualDec = job.ActualDec,
                errorArcsec = job.ErrorArcsec,
                rotation = job.Rotation,
                scale = job.Scale,
                error = job.Error
            });
        });

        group.MapPost("/slew-and-center/{jobId}/cancel", (string jobId,
            SlewCenterService slewCenter) => {
            slewCenter.CancelJob(jobId);
            return Results.Ok(new { jobId, state = "cancelled" });
        });

        // ---- Center on a solar-system object (solve-near-and-offset) ----
        // Plate solving can't lock onto the Sun/Moon/planets (washed-out disk
        // or no background stars), so this orchestrates: ephemeris → solve a
        // nearby star field → precise relative GoTo onto the object. Job-shaped
        // like slew-and-center; the client polls /status.

        group.MapGet("/center-body/list", () =>
            Results.Ok(new { bodies = SolarSystemCenterService.SupportedBodies }));

        group.MapPost("/center-body", (CenterBodyRequest request,
            SolarSystemCenterService svc) => {
            if (!SolarSystemCenterService.TryParseBody(request.Body, out _))
                return Results.BadRequest(new { error = $"Unsupported body '{request.Body}'" });
            var job = svc.StartJob(request.Body,
                request.OffsetDeg ?? 4.0, request.ToleranceArcsec ?? 30.0);
            return Results.Accepted(value: new {
                jobId = job.Id,
                body = job.Body,
                state = job.State.ToString().ToLowerInvariant()
            });
        });

        group.MapGet("/center-body/{jobId}/status", (string jobId,
            SolarSystemCenterService svc) => {
            var job = svc.GetJob(jobId);
            if (job == null) return Results.NotFound(new { error = "Job not found" });
            return Results.Ok(new {
                jobId = job.Id,
                body = job.Body,
                state = job.State.ToString().ToLowerInvariant(),
                targetRa = job.TargetRa,
                targetDec = job.TargetDec,
                offsetRa = job.OffsetRa,
                offsetDec = job.OffsetDec,
                solveErrorArcsec = job.SolveErrorArcsec,
                trackingMode = job.TrackingMode,
                error = job.Error
            });
        });

        group.MapPost("/center-body/{jobId}/cancel", (string jobId,
            SolarSystemCenterService svc) => {
            svc.CancelJob(jobId);
            return Results.Ok(new { jobId, state = "cancelled" });
        });

        group.MapGet("/catalog/search", (string query, SkyCatalogService catalog) => {
            var results = catalog.Search(query);
            return Results.Ok(new {
                query,
                results = results.Select(o => new {
                    name = o.Name,
                    ra = o.Ra,
                    dec = o.Dec,
                    raFormatted = o.RaFormatted,
                    decFormatted = o.DecFormatted,
                    magnitude = o.Magnitude,
                    type = o.Type,
                    commonName = o.CommonName,
                    sizeArcmin = o.SizeArcmin,
                    aliases = o.Aliases
                })
            });
        });

        group.MapGet("/catalog/{name}", (string name, SkyCatalogService catalog) => {
            var obj = catalog.GetByName(name);
            if (obj == null)
                return Results.NotFound(new { error = "Object not found" });

            return Results.Ok(new {
                name = obj.Name,
                ra = obj.Ra,
                dec = obj.Dec,
                raFormatted = obj.RaFormatted,
                decFormatted = obj.DecFormatted,
                magnitude = obj.Magnitude,
                type = obj.Type,
                commonName = obj.CommonName,
                aliases = obj.Aliases
            });
        });

        group.MapGet("/fov", (IConfiguration config) => {
            var sw = config.GetValue("Optics:SensorWidthMm", 23.5);
            var sh = config.GetValue("Optics:SensorHeightMm", 15.7);
            var fl = config.GetValue("Optics:FocalLengthMm", 478.0);

            double fovW = 0, fovH = 0, scale = 0;
            if (fl > 0) {
                fovW = 2 * Math.Atan(sw / (2 * fl)) * (180.0 / Math.PI);
                fovH = 2 * Math.Atan(sh / (2 * fl)) * (180.0 / Math.PI);
                scale = (sw / 1000.0) / (fl / 1000.0) * 206.265;
            }

            return Results.Ok(new {
                sensorWidthMm = sw,
                sensorHeightMm = sh,
                focalLengthMm = fl,
                fovWidthDeg = Math.Round(fovW, 4),
                fovHeightDeg = Math.Round(fovH, 4),
                scaleArcsecPerPixel = Math.Round(scale, 3)
            });
        });

        group.MapGet("/solver/status", (PlateSolveService solver) => {
            return Results.Ok(new {
                available = solver.IsAvailable,
                primaryId = solver.PrimarySolver.Id,
                primaryName = solver.PrimarySolver.DisplayName,
                primaryAvailable = solver.PrimarySolver.IsAvailable,
                blindId = solver.BlindSolver?.Id,
                blindName = solver.BlindSolver?.DisplayName,
                blindAvailable = solver.BlindSolver?.IsAvailable ?? false,
                path = solver.SolverPath
            });
        });

        group.MapGet("/solver/list", (PlateSolveService solver) => {
            return Results.Ok(solver.AllSolvers.Select(s => new {
                id = s.Id,
                name = s.DisplayName,
                available = s.IsAvailable,
                blind = s.SupportsBlindSolve
            }));
        });

        // ---- Catalog filters (Sky Atlas) ----

        group.MapGet("/catalog/types", (SkyCatalogService catalog) => {
            return Results.Ok(catalog.GetObjectTypes());
        });

        // CAT-4: list of distinct catalog sources present in the DB
        // (NGC / IC / M / C / Arp / Sh2 / HCG / AGC). Feeds the Atlas
        // panel's catalog dropdown. Empty list when running on the
        // legacy hardcoded fallback (no DsoCatalog).
        group.MapGet("/catalog/catalogs", (SkyCatalogService catalog) => {
            return Results.Ok(catalog.GetCatalogs());
        });

        group.MapGet("/catalog/filter", (string? query, string? type,
            string? catalogId, string? constellation,
            double? minMag, double? maxMag, double? minDec, double? maxDec,
            int? limit, SkyCatalogService catalog) => {
            var results = catalog.Filter(new CatalogFilter {
                Query = query, Type = type,
                // CAT-4: new optional filters. Old clients omit these
                // and behaviour matches the pre-CAT-4 endpoint.
                Catalog = catalogId, Constellation = constellation,
                MinMagnitude = minMag, MaxMagnitude = maxMag,
                MinDec = minDec, MaxDec = maxDec
            }, Math.Clamp(limit ?? 50, 1, 500));

            return Results.Ok(new {
                count = results.Count,
                results = results.Select(o => new {
                    name = o.Name, ra = o.Ra, dec = o.Dec,
                    raFormatted = o.RaFormatted, decFormatted = o.DecFormatted,
                    magnitude = o.Magnitude, type = o.Type,
                    commonName = o.CommonName, aliases = o.Aliases,
                    // CAT-4: new optional fields. Old UIs ignore them.
                    catalog = o.Catalog, catalogId = o.CatalogId,
                    constellation = o.Constellation, sizeArcmin = o.SizeArcmin
                })
            });
        });

        // CAT-4: cone search. Returns DSO catalog entries within
        // `radius` degrees of (`ra` hours, `dec` deg), optionally
        // mag-limited. Used for "what's in my FOV" overlays + future
        // Mosaic auto-suggest. Returns 503 when the expanded DB
        // isn't available (legacy fallback can't answer this).
        group.MapGet("/catalog/near", async (double ra, double dec, double radius,
            double? maxMag, int? limit,
            NINA.Polaris.Services.Sky.DsoCatalog dso) => {
            if (!dso.IsAvailable) {
                return Results.Json(new {
                    error = "Expanded DSO catalog not available. " +
                            "Run `python scripts/build-dso-catalog.py` to populate it."
                }, statusCode: 503);
            }
            // PERF #365: await the query instead of blocking the Kestrel
            // request thread with .GetAwaiter().GetResult() (which starved
            // the thread pool on large cone searches).
            var hits = await dso.QueryRegionAsync(ra, dec,
                Math.Max(0.01, radius), maxMag,
                Math.Clamp(limit ?? 200, 1, 1000));
            return Results.Ok(new {
                count = hits.Count,
                results = hits.Select(o => new {
                    name = o.Name, ra = o.RaHours, dec = o.DecDeg,
                    magnitude = o.Magnitude, type = o.Type,
                    commonName = o.CommonName,
                    catalog = o.Catalog, catalogId = o.CatalogId,
                    constellation = o.Constellation,
                    sizeArcmin = o.SizeArcmin, aliases = o.Aliases
                })
            });
        });

        // GET /api/sky/identify?ra={raHours}&dec={decDeg}&fov={radiusDeg}
        // ASIAIR-style "what am I looking at": given the mount's (ideally
        // plate-solved) pointing, return the most relevant catalog object in
        // the field. The frontend calls this after Slew & Center and from the
        // Identify button next to the PREVIEW / LIVE target field. `fov` is the
        // field half-size in degrees (optional; defaults to 1°).
        group.MapGet("/identify", (double ra, double dec, double? fov,
            SkyCatalogService catalog) => {
            var hit = catalog.Identify(ra, dec, fov ?? 1.0);
            if (hit == null) return Results.Ok(new { found = false });
            var o = hit.Object;
            return Results.Ok(new {
                found = true,
                name = o.Name,
                commonName = o.CommonName,
                // Friendly label: prefer the common name, fall back to the
                // catalog designation.
                displayName = string.IsNullOrWhiteSpace(o.CommonName) ? o.Name : o.CommonName,
                type = o.Type,
                magnitude = o.Magnitude >= 99 ? (double?)null : o.Magnitude,
                ra = o.Ra,
                dec = o.Dec,
                sizeArcmin = o.SizeArcmin,
                constellation = o.Constellation,
                separationArcmin = Math.Round(hit.SeparationArcmin, 1),
                withinExtent = hit.WithinExtent
            });
        });

        // ---- Comet orbital elements ----

        // What the ephemeris is working from, so the UI can say how old it is
        // instead of presenting a build-time snapshot as if it were current.
        group.MapGet("/comets/status", (CometEphemerisService comets) => Results.Ok(new {
            count = comets.AllComets.Count,
            source = comets.Source,                 // "bundled" or "jpl"
            fetchedAtUtc = comets.FetchedAtUtc,     // null while on the bundled set
            ageDays = comets.FetchedAtUtc is { } t
                ? Math.Round((DateTime.UtcNow - t).TotalDays, 1)
                : (double?)null
        }));

        // Manual refresh. The worker already does this when the host has
        // internet, but a field host often gets a hotspot for one minute and
        // the operator wants it to happen now rather than at the next tick.
        group.MapPost("/comets/refresh", async (CometElementsUpdater updater,
                                                CometEphemerisService comets,
                                                CancellationToken ct) => {
            try {
                var n = await updater.RefreshAsync(ct);
                return Results.Ok(new {
                    ok = true, count = n,
                    source = comets.Source, fetchedAtUtc = comets.FetchedAtUtc
                });
            } catch (Exception ex) {
                // Offline is the common case, not a server fault: report it as
                // a failed attempt with the reason, and leave the current
                // elements in place.
                return Results.Ok(new {
                    ok = false, error = ex.Message,
                    count = comets.AllComets.Count,
                    source = comets.Source, fetchedAtUtc = comets.FetchedAtUtc
                });
            }
        });

        // The URL the host would fetch. Handing it out keeps BuildQueryUrl the
        // single source of truth: a client that fetches on the host's behalf
        // asks for this rather than reimplementing the query, so the perihelion
        // window and field list can never drift between the two paths.
        group.MapGet("/comets/source", () => Results.Ok(new {
            url = CometElementsUpdater.BuildQueryUrl(DateTime.UtcNow)
        }));

        // Install elements fetched by somebody else, for the case the host
        // cannot reach the internet at all: out in a field the tablet has
        // mobile data and the Polaris box has none. The body is the raw JPL
        // response, parsed here by the very same parser the host path uses, so
        // the client stays a courier and every validation rule lives in one
        // place.
        group.MapPost("/comets/import", async (HttpRequest req,
                                               CometEphemerisService comets,
                                               ILoggerFactory loggerFactory) => {
            var log = loggerFactory.CreateLogger("CometImport");
            string body;
            using (var reader = new StreamReader(req.Body)) {
                // A sane ceiling: the filtered query is ~12 KB and the entire
                // unfiltered comet table is under 2 MB.
                var buffer = new char[4 * 1024 * 1024];
                var read = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
                body = new string(buffer, 0, read);
            }
            if (string.IsNullOrWhiteSpace(body))
                return Results.BadRequest(new { error = "Empty body" });

            try {
                var parsed = CometElementsUpdater.Parse(body, DateTime.UtcNow, out var skipped);
                if (parsed.Count == 0)
                    return Results.BadRequest(new {
                        error = "No usable comets in the supplied data; keeping the current elements"
                    });
                comets.ReplaceElements(parsed, "jpl", DateTime.UtcNow);
                log.LogInformation("Imported {Kept} comet elements from a client ({Skipped} skipped)",
                    parsed.Count, skipped);
                return Results.Ok(new {
                    ok = true, count = parsed.Count, skipped,
                    source = comets.Source, fetchedAtUtc = comets.FetchedAtUtc
                });
            } catch (Exception ex) {
                log.LogWarning(ex, "Client comet import failed");
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // ---- Altitude chart + night window ----

        group.MapGet("/altitude", (double ra, double dec, int? stepMinutes,
            AltitudeService alt, ProfileService profile) => {
            // Default window: current night (sunset to sunrise) at the
            // observer's longitude. If no profile lat/lon yet, fall back to
            // ±6h around now so the chart still draws something useful.
            var window = alt.ComputeNightWindow();
            DateTime from, to;
            if (Math.Abs(profile.Active.Latitude) > 0.01 || Math.Abs(profile.Active.Longitude) > 0.01) {
                from = window.Sunset;
                to = window.Sunrise;
            } else {
                from = DateTime.UtcNow.AddHours(-6);
                to = DateTime.UtcNow.AddHours(6);
            }

            var step = Math.Clamp(stepMinutes ?? 15, 1, 120);
            var track = alt.ComputeTrack(ra, dec, from, to, step);

            // Custom horizon: annotate each sample with the interpolated horizon
            // altitude at its azimuth and whether the target is blocked there.
            var horizon = profile.Active.HorizonPoints ?? new List<HorizonPoint>();
            var samples = track.Select(s => new {
                utc = s.Utc,
                altitudeDeg = s.AltitudeDeg,
                azimuthDeg = s.AzimuthDeg,
                horizonDeg = HorizonProfile.AltitudeAt(horizon, s.AzimuthDeg),
                blocked = HorizonProfile.IsBlocked(horizon, s.AltitudeDeg, s.AzimuthDeg)
            }).ToList();

            return Results.Ok(new {
                target = new { ra, dec },
                fromUtc = from,
                toUtc = to,
                stepMinutes = step,
                hasHorizon = horizon.Count > 0,
                samples,
                twilight = new {
                    sunset = window.Sunset,
                    civilDusk = window.CivilDuskUtc,
                    nauticalDusk = window.NauticalDuskUtc,
                    astronomicalDusk = window.AstronomicalDuskUtc,
                    astronomicalDawn = window.AstronomicalDawnUtc,
                    nauticalDawn = window.NauticalDawnUtc,
                    civilDawn = window.CivilDawnUtc,
                    sunrise = window.Sunrise
                }
            });
        });

        // ---- Custom horizon (az→alt visibility mask) ----

        group.MapGet("/horizon", (ProfileService profile) =>
            Results.Ok(new { points = profile.Active.HorizonPoints ?? new List<HorizonPoint>() }));

        // Replace the whole horizon from the in-UI editor (a list of points).
        group.MapPut("/horizon", (HorizonRequest req, ProfileService profile) => {
            var points = HorizonProfile.Normalize(req?.Points ?? new List<HorizonPoint>());
            profile.UpdateSettings(p => p.HorizonPoints = points);
            return Results.Ok(new { points });
        });

        // Import a horizon file (N.I.N.A. .hrz / Stellarium / TouchNStars text:
        // "azimuth altitude" pairs). The raw text is posted as the body.
        group.MapPost("/horizon/import", async (HttpRequest req, ProfileService profile) => {
            using var reader = new StreamReader(req.Body);
            var text = await reader.ReadToEndAsync();
            var points = HorizonProfile.Parse(text);
            if (points.Count == 0)
                return Results.BadRequest(new { error = "No horizon points found in the file" });
            profile.UpdateSettings(p => p.HorizonPoints = points);
            return Results.Ok(new { points });
        });

        group.MapDelete("/horizon", (ProfileService profile) => {
            profile.UpdateSettings(p => p.HorizonPoints = new List<HorizonPoint>());
            return Results.Ok(new { points = new List<HorizonPoint>() });
        });

        // Resolve a celestial object name to a thumbnail image URL (NASA
        // Image Library, falling back to Wikipedia). Caches per-name on
        // disk for 30 days. Returns { available: false } when neither
        // provider has anything, never 500s.
        group.MapGet("/image", async (string name, CelestialImageService svc, CancellationToken ct) => {
            if (string.IsNullOrWhiteSpace(name)) {
                return Results.BadRequest(new { error = "name is required" });
            }
            var img = await svc.GetImageAsync(name, ct);
            return Results.Ok(img);
        });

        // Serve a cached thumbnail blob from disk. Pairs with the
        // pre-fetch endpoint to give the UI a fully-offline catalogue:
        // the UI can prefer /api/sky/image/file/{slug} over the remote
        // URL and never hit NASA / Wikipedia at view time.
        group.MapGet("/image/file/{slug}", (string slug, CelestialImageService svc) => {
            var path = svc.GetLocalFilePath(slug);
            if (path == null) return Results.NotFound();
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var mime = ext switch {
                ".png"  => "image/png",
                ".gif"  => "image/gif",
                ".webp" => "image/webp",
                _       => "image/jpeg"
            };
            return Results.File(path, mime);
        });

        // Walk the local DSO catalogue + Moon + planets + curated comets
        // and warm the on-disk image cache for each. After this runs,
        // the Tonight tab works fully offline. Sequential lookups,
        // takes a couple of minutes on first run, then is instant.
        group.MapPost("/image/prefetch", async (
            CelestialImageService imgs,
            SkyCatalogService catalog,
            CometEphemerisService comets,
            CancellationToken ct) => {
            var names = new List<string>();
            // DSOs: prefer the common name (better hit rate on NASA),
            // also include the catalogue code so both cache files exist
            // and the frontend's two-shot lookup always finds something.
            foreach (var dso in catalog.AllObjects) {
                if (!string.IsNullOrEmpty(dso.CommonName)) names.Add(dso.CommonName);
                names.Add(dso.Name);
            }
            names.AddRange(new[] {
                "Moon",
                "Mercury", "Venus", "Mars", "Jupiter", "Saturn", "Uranus", "Neptune"
            });
            foreach (var c in comets.AllComets) names.Add(c.Name);
            var summary = await imgs.PrefetchAsync(names, ct);
            return Results.Ok(summary);
        });

        // Ranked list of objects best positioned for observation tonight
        // from the active profile's location. Includes DSOs (from the
        // local catalog) + Moon + visible planets.
        group.MapGet("/tonights-best", (int? limit, TonightsBestService svc) => {
            var n = Math.Clamp(limit ?? 30, 1, 100);
            var result = svc.Compute(n);
            return Results.Ok(result);
        });

        // GET /api/sky/nearest-planet?ra={raHours}&dec={decDeg}
        // Returns the Moon or planet closest to the given pointing
        // in angular separation (deg) on the celestial sphere right
        // now. Drives the VIDEO tab's "auto-detect target" button so
        // the operator doesn't have to type "Jupiter" / "Saturn" /
        // "Moon" manually when they slewed to one for planetary
        // imaging. Sun is intentionally excluded (you don't image it
        // with the same rig setup; including would make a 1° pointing
        // to a daytime sky always score 'Sun').
        group.MapGet("/nearest-planet", (double ra, double dec, ProfileService profiles) => {
            var p = profiles.Active;
            var time = new AstroTime(DateTime.UtcNow);
            var observer = new Observer(p.Latitude, p.Longitude, p.Altitude);

            (string name, Body body)[] targets = new[] {
                ("Moon",    Body.Moon),
                ("Mercury", Body.Mercury),
                ("Venus",   Body.Venus),
                ("Mars",    Body.Mars),
                ("Jupiter", Body.Jupiter),
                ("Saturn",  Body.Saturn),
                ("Uranus",  Body.Uranus),
                ("Neptune", Body.Neptune),
            };

            string? bestName = null;
            double bestSep = double.MaxValue;
            double bestRa = 0, bestDec = 0;

            foreach (var (name, body) in targets) {
                try {
                    var eq = Astronomy.Equator(body, time, observer,
                        EquatorEpoch.OfDate, Aberration.Corrected);
                    var sep = AngularSeparationDeg(ra, dec, eq.ra, eq.dec);
                    if (sep < bestSep) {
                        bestSep = sep;
                        bestName = name;
                        bestRa = eq.ra;
                        bestDec = eq.dec;
                    }
                } catch {
                    // AstronomyEngine doesn't compute Pluto-class
                    // bodies in all branches; skip whatever doesn't
                    // converge rather than dragging the whole probe
                    // down with it.
                }
            }

            if (bestName == null)
                return Results.Ok(new { found = false });

            return Results.Ok(new {
                found = true,
                name = bestName,
                raHours = bestRa,
                decDeg = bestDec,
                angularSepDeg = bestSep
            });
        });
    }

    /// <summary>
    /// Great-circle distance between two RA/Dec points in degrees.
    /// Haversine formula in spherical coordinates — accurate at the
    /// arcsec level which is way more than we need to pick "the closest
    /// planet". RA in hours, Dec in degrees, like the rest of the
    /// codebase. Reused by the nearest-planet probe + any future
    /// "what's at this pointing" query.
    /// </summary>
    private static double AngularSeparationDeg(double ra1H, double dec1D, double ra2H, double dec2D) {
        double D2R = Math.PI / 180.0;
        double ra1 = ra1H * 15.0 * D2R;
        double ra2 = ra2H * 15.0 * D2R;
        double d1  = dec1D * D2R;
        double d2  = dec2D * D2R;
        double dRa = ra2 - ra1;
        double dDec = d2 - d1;
        double a = Math.Sin(dDec / 2) * Math.Sin(dDec / 2)
                 + Math.Cos(d1) * Math.Cos(d2) * Math.Sin(dRa / 2) * Math.Sin(dRa / 2);
        double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return c / D2R;
    }

    public record SlewAndCenterRequest(double Ra, double Dec, double ToleranceArcsec = 30.0,
        bool CenterOnly = false, bool Force = false);

    /// <summary>POST body for <c>/api/sky/center-body</c>. <c>Body</c> is a
    /// friendly name (Sun/Moon/Mercury…Neptune). <c>OffsetDeg</c> is how far the
    /// nearby star field sits from the object (default 4°); <c>ToleranceArcsec</c>
    /// is the offset-field centering tolerance.</summary>
    public record CenterBodyRequest(string Body, double? OffsetDeg = null,
        double? ToleranceArcsec = null);

    /// <summary>PUT body for <c>/api/sky/horizon</c>: the full set of custom
    /// horizon points from the in-UI editor.</summary>
    public record HorizonRequest(List<HorizonPoint>? Points);
}