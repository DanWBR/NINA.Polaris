using NINA.Polaris.Services;

namespace NINA.Polaris.Endpoints;

public static class FilterWheelEndpoints {
    public static void MapFilterWheelEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/filterwheel");

        group.MapGet("/status", (EquipmentManager equip) => {
            if (equip.FilterWheel == null)
                return Results.Ok(new { connected = false });

            var caps = equip.FilterWheel.Capabilities;
            return Results.Ok(new {
                connected = true,
                name = equip.FilterWheel.DeviceName,
                position = equip.FilterWheel.Position,
                currentFilter = equip.FilterWheel.CurrentFilterName,
                filters = equip.FilterWheel.FilterNames,
                moving = equip.FilterWheel.IsMoving,
                capabilities = new {
                    editNames = caps.SupportsEditNames
                }
            });
        });

        // INDI FILTER_NAME push: rename slots from Polaris into the
        // driver so they persist across restarts. ASCOM/Alpaca
        // wheels don't expose this through the driver surface, so
        // they 501 -- the frontend hides the "Edit names" button
        // for non-INDI wheels based on the capabilities flag.
        group.MapPut("/names", async (EquipmentManager equip, FilterNamesRequest request) => {
            if (equip.FilterWheel == null)
                return Results.BadRequest(new { error = "No filter wheel selected" });
            if (request?.Names == null)
                return Results.BadRequest(new { error = "names[] required" });
            try {
                await equip.FilterWheel.SetFilterNamesAsync(request.Names);
                return Results.Ok(new {
                    status = "set",
                    count = request.Names.Length,
                    names = equip.FilterWheel.FilterNames
                });
            } catch (NotSupportedException ex) {
                return Results.Json(new { error = ex.Message }, statusCode: 501);
            } catch (ArgumentException ex) {
                return Results.BadRequest(new { error = ex.Message });
            } catch (Exception ex) {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        group.MapPost("/position/{slot:int}", async (int slot, EquipmentManager equip) => {
            if (equip.FilterWheel == null)
                return Results.BadRequest(new { error = "No filter wheel connected" });

            await equip.FilterWheel.SetPositionAsync(slot);
            // Wait until the wheel actually settles on the slot (or
            // times out at 30 s -- ZWO EFWs typically finish in 2-4 s
            // but slower 7-pos wheels can take longer). Returns the
            // observed final position so the UI can confirm the move
            // landed where it asked.
            var settled = await WaitForFilterSettleAsync(equip.FilterWheel, slot, TimeSpan.FromSeconds(30));
            return Results.Ok(new {
                position = equip.FilterWheel.Position,
                currentFilter = equip.FilterWheel.CurrentFilterName,
                settled,
                message = settled
                    ? $"Filter wheel arrived at slot {slot}"
                    : $"Filter wheel still moving (timeout); last reported slot {equip.FilterWheel.Position}"
            });
        });

        group.MapPost("/filter/{filterName}", async (string filterName, EquipmentManager equip) => {
            if (equip.FilterWheel == null)
                return Results.BadRequest(new { error = "No filter wheel connected" });

            try {
                await equip.FilterWheel.SetFilterByNameAsync(filterName);
                // Block the response until the wheel reports the new
                // filter as current (or 30 s). Without this the
                // frontend's toast says "Moving to X" but the user
                // never gets a confirmation that the move actually
                // completed -- INDI's BUSY → OK state transition is
                // the only ack we have.
                var targetPos = equip.FilterWheel.Position;
                var settled = await WaitForFilterSettleAsync(equip.FilterWheel, targetPos, TimeSpan.FromSeconds(30));
                return Results.Ok(new {
                    filter = equip.FilterWheel.CurrentFilterName,
                    position = equip.FilterWheel.Position,
                    settled,
                    message = settled
                        ? $"Filter wheel set to '{equip.FilterWheel.CurrentFilterName}'"
                        : $"Filter wheel still moving (timeout); current '{equip.FilterWheel.CurrentFilterName}'"
                });
            } catch (InvalidOperationException ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPost("/select/{deviceName}", (string deviceName, EquipmentManager equip, string? driver) => {
            try {
                equip.SelectFilterWheel(driver ?? "indi", deviceName);
                return Results.Ok(new { device = deviceName, driver = driver ?? "indi" });
            } catch (NotSupportedException ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Per-driver filter-wheel discovery + driver catalogue.
        // Same shape as the focuser endpoints.
        group.MapGet("/discover", (EquipmentManager equip, string? driver) => {
            var d = (driver ?? "indi").Trim().ToLowerInvariant();
            if (d == "ascom-com") {
                return Results.Ok(equip.GetAscomDrivers(
                    NINA.Ascom.Com.AscomComRegistry.DeviceType.FilterWheel));
            }
            if (d == "alpaca") {
                return Results.Ok(equip.GetDiscoveredFilterWheelsFor("alpaca"));
            }
            return Results.Ok(equip.GetDeviceNames()
                .Select(n => new DiscoveredCamera(n, n, n))
                .ToList());
        });

        group.MapGet("/drivers", (EquipmentManager equip) => {
            var alpacaCount = equip.GetDiscoveredFilterWheelsFor("alpaca").Count;
            var list = new List<CameraDriverInfo> {
                new("indi", "INDI", Available: true,
                    Description: "Any filter wheel the running INDI server exposes."),
                new("alpaca", "Alpaca (ASCOM)", Available: alpacaCount > 0,
                    Description: alpacaCount > 0
                        ? $"ASCOM-over-HTTP filter wheels. {alpacaCount} discovered."
                        : "Run Alpaca Discover in RIGS first to populate this list."),
            };
            if (OperatingSystem.IsWindows()) {
                var n = equip.GetAscomDrivers(
                    NINA.Ascom.Com.AscomComRegistry.DeviceType.FilterWheel).Count;
                list.Add(new("ascom-com", "ASCOM (COM, direct)",
                    Available: n > 0,
                    Description: n > 0
                        ? $"Direct COM-interop. {n} driver(s) registered."
                        : "Install the ASCOM Platform + a filter-wheel driver."));
            }
            return Results.Ok(list);
        });

        group.MapPost("/connect", async (EquipmentManager equip) => {
            if (equip.FilterWheel == null)
                return Results.BadRequest(new { error = "No filter wheel selected" });

            await equip.FilterWheel.ConnectAsync();
            return Results.Ok(new { connected = true });
        });

        group.MapPost("/disconnect", async (EquipmentManager equip) => {
            if (equip.FilterWheel == null)
                return Results.BadRequest(new { error = "No filter wheel selected" });

            await equip.FilterWheel.DisconnectAsync();
            return Results.Ok(new { connected = false });
        });
    }

    /// <summary>Body for PUT /names. Required field; Names.Length
    /// must equal the wheel's slot count (validated server-side).</summary>
    public record FilterNamesRequest(string[] Names);

    /// <summary>Poll <see cref="NINA.Image.Interfaces.IFilterWheel.IsMoving"/> until the
    /// wheel reports settled at the expected slot (or close enough --
    /// some drivers report Position before clearing IsMoving by a few
    /// hundred ms). 50 ms cadence balances responsiveness vs. CPU on
    /// the Pi. Returns true when settled, false on timeout.
    ///
    /// Without this wait the /position and /filter responses returned
    /// immediately after SetPositionAsync queued the INDI vector, so
    /// the frontend toasted "Moving to G" but never knew if it
    /// finished. The blocking wait lets the UI surface success or a
    /// timeout warning to the user without polling /status manually.</summary>
    private static async Task<bool> WaitForFilterSettleAsync(
            NINA.Image.Interfaces.IFilterWheel fw, int targetSlot, TimeSpan timeout) {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline) {
            try {
                // Settled when IsMoving cleared. Position check is an
                // extra guard for drivers that don't drop the BUSY
                // flag promptly -- some report state=OK on the wheel
                // landing within a slot of the request, but the
                // settled position only updates a tick later.
                if (!fw.IsMoving && fw.Position == targetSlot) return true;
            } catch {
                // A transient read failure shouldn't break the wait;
                // try again on the next tick.
            }
            await Task.Delay(50);
        }
        return false;
    }
}
