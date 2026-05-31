using NINA.Polaris.Services;

namespace NINA.Polaris.Endpoints;

public static class TelescopeEndpoints {
    public static void MapTelescopeEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/telescope");

        group.MapGet("/status", (EquipmentManager equip) => {
            if (equip.Telescope == null)
                return Results.Ok(new {
                    connected = false, tracking = false,
                    ra = 0.0, dec = 0.0, alt = 0.0, az = 0.0,
                    pierSide = "unknown", slewing = false, parked = false
                });

            return Results.Ok(new {
                connected = equip.Telescope.IsConnected,
                tracking = equip.Telescope.IsTracking,
                ra = equip.Telescope.RightAscension,
                dec = equip.Telescope.Declination,
                alt = equip.Telescope.Altitude,
                az = equip.Telescope.Azimuth,
                pierSide = equip.Telescope.SideOfPier.ToString(),
                slewing = equip.Telescope.IsSlewing,
                parked = equip.Telescope.IsParked
            });
        });

        group.MapPost("/slew", async (EquipmentManager equip, SlewRequest request) => {
            if (equip.Telescope == null)
                return Results.BadRequest(new { error = "No telescope selected" });

            try {
                await equip.Telescope.SlewAsync(request.Ra, request.Dec);
                return Results.Ok(new {
                    status = "slewing",
                    target = new { request.Ra, request.Dec }
                });
            } catch (Exception ex) {
                return Results.Problem(ex.Message);
            }
        });

        group.MapPost("/sync", async (EquipmentManager equip, SlewRequest request) => {
            if (equip.Telescope == null)
                return Results.BadRequest(new { error = "No telescope selected" });

            await equip.Telescope.SyncAsync(request.Ra, request.Dec);
            return Results.Ok(new { status = "synced", ra = request.Ra, dec = request.Dec });
        });

        group.MapPost("/park", async (EquipmentManager equip) => {
            if (equip.Telescope == null)
                return Results.BadRequest(new { error = "No telescope selected" });

            await equip.Telescope.ParkAsync();
            return Results.Ok(new { status = "parking" });
        });

        group.MapPost("/unpark", async (EquipmentManager equip) => {
            if (equip.Telescope == null)
                return Results.BadRequest(new { error = "No telescope selected" });

            await equip.Telescope.UnparkAsync();
            return Results.Ok(new { status = "unparking" });
        });

        // Drive the mount to its mechanical home position. Surfaces
        // NotSupportedException as a 501 + actionable error message
        // so the UI can toast "this mount doesn't support Find Home"
        // instead of failing silently. Most GEM and alt-az mounts
        // honour it via INDI TELESCOPE_HOME / Alpaca findhome.
        group.MapPost("/find-home", async (EquipmentManager equip) => {
            if (equip.Telescope == null)
                return Results.BadRequest(new { error = "No telescope selected" });
            try {
                await equip.Telescope.FindHomeAsync();
                return Results.Ok(new { status = "homing" });
            } catch (NotSupportedException ex) {
                return Results.Json(new { error = ex.Message }, statusCode: 501);
            } catch (Exception ex) {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        // Push the observatory coordinates currently stored in the
        // active profile into the mount. Body is optional: callers
        // can override with explicit lat/long/elev (used by the
        // automated tests + planned "set from GPS" flow); when
        // omitted, falls back to profile values. Returns the values
        // actually sent so the frontend can show "Latitude -5.18,
        // Longitude -37.36 pushed".
        group.MapPost("/sync-location", async (EquipmentManager equip,
                                                ProfileService profiles,
                                                SyncLocationRequest? body) => {
            if (equip.Telescope == null)
                return Results.BadRequest(new { error = "No telescope selected" });
            var p = profiles.Active;
            // null body / partially-null body falls through to profile
            // values. The OR-zero guard for latitude is intentional:
            // 0° latitude is the equator (Quito, Singapore, Macapá)
            // which is a valid observatory location, so we ONLY pull
            // from profile when the request field was actually omitted.
            var lat = body?.Latitude ?? p.Latitude;
            var lon = body?.Longitude ?? p.Longitude;
            var elev = body?.Elevation ?? p.Altitude;
            try {
                await equip.Telescope.SetSiteLocationAsync(lat, lon, elev);
                return Results.Ok(new {
                    status = "synced",
                    latitude = lat, longitude = lon, elevation = elev
                });
            } catch (NotSupportedException ex) {
                return Results.Json(new { error = ex.Message }, statusCode: 501);
            } catch (Exception ex) {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        group.MapPost("/tracking", async (EquipmentManager equip, TrackingRequest request) => {
            if (equip.Telescope == null)
                return Results.BadRequest(new { error = "No telescope selected" });

            await equip.Telescope.SetTrackingAsync(request.Enabled);
            return Results.Ok(new { tracking = request.Enabled });
        });

        // Push wall-clock UTC + the host's local-timezone offset into
        // the mount via INDI TIME_UTC / Alpaca utcdate. Body is optional:
        // when omitted, sends DateTime.UtcNow + the host's current
        // offset (typical case: user just clicked "Sync time" after the
        // RPi clock was set via NTP / chrony). Returns the actual values
        // sent for UI feedback.
        group.MapPost("/sync-time", async (EquipmentManager equip,
                                            SyncTimeRequest? body) => {
            if (equip.Telescope == null)
                return Results.BadRequest(new { error = "No telescope selected" });
            // Date.UtcNow forbidden in workflow scripts, fine here in a
            // normal endpoint handler — request handlers ARE the place
            // where ambient time is read.
            var utc = body?.Utc ?? DateTime.UtcNow;
            var offset = body?.OffsetHoursFromUtc
                ?? TimeZoneInfo.Local.GetUtcOffset(utc).TotalHours;
            try {
                await equip.Telescope.SetSiteTimeAsync(utc, offset);
                return Results.Ok(new {
                    status = "synced",
                    utc = utc.ToString("o"),
                    offsetHoursFromUtc = offset
                });
            } catch (NotSupportedException ex) {
                return Results.Json(new { error = ex.Message }, statusCode: 501);
            } catch (Exception ex) {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        // Select the mount's tracking-rate model. Accepts the string
        // "sidereal" (default for star tracking) / "solar" (Sun) /
        // "lunar" (Moon's mean motion). Case-insensitive. Some INDI
        // drivers REQUIRE a track mode set before TRACK_ON actually
        // engages — without it the mount silently ignores enable, so
        // this endpoint is also called by the connect-wizard once on
        // first attach to force a known good baseline.
        group.MapPost("/tracking-mode", async (EquipmentManager equip,
                                                TrackingModeRequest request) => {
            if (equip.Telescope == null)
                return Results.BadRequest(new { error = "No telescope selected" });
            var modeStr = (request.Mode ?? "sidereal").Trim().ToLowerInvariant();
            var mode = modeStr switch {
                "solar"    => NINA.Image.Interfaces.TrackingMode.Solar,
                "lunar"    => NINA.Image.Interfaces.TrackingMode.Lunar,
                "sidereal" => NINA.Image.Interfaces.TrackingMode.Sidereal,
                _ => (NINA.Image.Interfaces.TrackingMode?)null
            };
            if (mode == null) {
                return Results.BadRequest(new {
                    error = $"Unknown tracking mode '{request.Mode}'. Use sidereal | solar | lunar."
                });
            }
            try {
                await equip.Telescope.SetTrackingModeAsync(mode.Value);
                return Results.Ok(new { status = "set", mode = mode.Value.ToString() });
            } catch (NotSupportedException ex) {
                return Results.Json(new { error = ex.Message }, statusCode: 501);
            } catch (Exception ex) {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        group.MapPost("/abort", async (EquipmentManager equip) => {
            if (equip.Telescope == null)
                return Results.BadRequest(new { error = "No telescope selected" });

            await equip.Telescope.AbortSlewAsync();
            return Results.Ok(new { status = "stopped" });
        });

        group.MapPost("/move/{direction}", async (EquipmentManager equip, string direction) => {
            if (equip.Telescope == null)
                return Results.BadRequest(new { error = "No telescope selected" });

            switch (direction.ToLowerInvariant()) {
                case "north": await equip.Telescope.MoveNorthAsync(); break;
                case "south": await equip.Telescope.MoveSouthAsync(); break;
                case "east": await equip.Telescope.MoveEastAsync(); break;
                case "west": await equip.Telescope.MoveWestAsync(); break;
                case "stop": await equip.Telescope.StopMotionAsync(); break;
                default: return Results.BadRequest(new { error = $"Unknown direction: {direction}" });
            }

            return Results.Ok(new { status = "moving", direction });
        });

        group.MapPost("/select/{deviceName}", (EquipmentManager equip, string deviceName, string? driver) => {
            // ?driver=indi (default) | synscan-wifi | nexstar-wifi |
            // lx200-tcp | alpaca. Legacy clients omit it and get INDI.
            try {
                equip.SelectTelescope(driver ?? "indi", deviceName);
                return Results.Ok(new {
                    selected = deviceName,
                    driver = driver ?? "indi"
                });
            } catch (NotSupportedException ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Mount driver catalogue. Same shape as /api/camera/drivers.
        // INDI is always available; direct WiFi drivers (SynScan UDP,
        // NexStar TCP, LX200 TCP) advertise as "not installed" until
        // their backend lands, see docs/mounts-wifi.md.
        group.MapGet("/drivers", (EquipmentManager equip)
            => Results.Ok(equip.GetAvailableMountDrivers()));

        // Per-driver telescope discovery. INDI uses device names from
        // the active connection; ASCOM uses registered ProgIDs from
        // the local Windows registry. SynScan-WiFi is host:port-based
        // (no enumeration possible), so it returns empty here — the
        // user types the address directly.
        group.MapGet("/discover", (EquipmentManager equip, string? driver) => {
            var d = (driver ?? "indi").Trim().ToLowerInvariant();
            if (d == "ascom-com") {
                return Results.Ok(equip.GetAscomDrivers(
                    NINA.Ascom.Com.AscomComRegistry.DeviceType.Telescope));
            }
            if (d == "alpaca") {
                return Results.Ok(equip.GetDiscoveredTelescopesFor("alpaca"));
            }
            if (d == "indi") {
                return Results.Ok(equip.GetDeviceNames()
                    .Select(n => new DiscoveredCamera(n, n, n))
                    .ToList());
            }
            return Results.Ok(Array.Empty<DiscoveredCamera>());
        });

        group.MapPost("/connect", async (EquipmentManager equip) => {
            if (equip.Telescope == null)
                return Results.BadRequest(new { error = "No telescope selected" });

            await equip.Telescope.ConnectAsync();
            return Results.Ok(new { status = "connected", device = equip.Telescope.DeviceName });
        });

        group.MapPost("/disconnect", async (EquipmentManager equip) => {
            if (equip.Telescope == null)
                return Results.BadRequest(new { error = "No telescope selected" });

            await equip.Telescope.DisconnectAsync();
            return Results.Ok(new { status = "disconnected" });
        });
    }

    public record SlewRequest(double Ra, double Dec);
    public record TrackingRequest(bool Enabled);
    /// <summary>Optional override for POST /sync-location. All three
    /// fields are nullable so a null body OR an empty {} OR a partial
    /// {Elevation: 1200} body all work, falling back to the active
    /// profile for the missing values.</summary>
    public record SyncLocationRequest(double? Latitude, double? Longitude, double? Elevation);
    /// <summary>Optional body for POST /sync-time. When fully null the
    /// endpoint sends DateTime.UtcNow + the host's current local-time
    /// offset; provide both fields to push an arbitrary moment.</summary>
    public record SyncTimeRequest(DateTime? Utc, double? OffsetHoursFromUtc);
    /// <summary>Body for POST /tracking-mode. Mode = "sidereal" |
    /// "solar" | "lunar" (case-insensitive). Required field.</summary>
    public record TrackingModeRequest(string Mode);
}
