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

namespace NINA.Polaris.Endpoints;

/// <summary>
/// REST surface for the power box / switch hub (<c>ISwitchDevice</c>).
/// Multi-driver like the filter wheel: <c>/select</c> takes an optional
/// <c>driver</c>, <c>/discover</c> + <c>/drivers</c> feed the RIGS picker.
/// The power-box actions (<c>/set-bool</c>, <c>/set-value</c>) address a
/// channel by its stable id.
/// </summary>
public static class SwitchEndpoints {
    public static void MapSwitchEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/switch");

        group.MapGet("/status", (EquipmentManager equip, ProfileService profileSvc) => {
            if (equip.Switch == null)
                return Results.Ok(new { connected = false });
            var names = profileSvc.ActiveEquipmentProfile?.SwitchChannelNames
                        ?? new Dictionary<string, string>();
            return Results.Ok(new {
                connected = equip.Switch.IsConnected,
                name = equip.Switch.DeviceName,
                driver = equip.SwitchDriver,
                channels = equip.Switch.Channels.Select(c => new {
                    id = c.Id,
                    // `name` stays the driver's own label so the UI can always
                    // show what the hardware calls the channel; `displayName`
                    // is what the operator named it, when they have.
                    name = c.Name,
                    key = c.Key,
                    displayName = (!string.IsNullOrEmpty(c.Key)
                                   && names.TryGetValue(c.Key, out var friendly)
                                   && !string.IsNullOrWhiteSpace(friendly))
                                  ? friendly : c.Name,
                    boolean = c.Boolean,
                    value = double.IsNaN(c.Value) ? 0.0 : c.Value,
                    min = c.Min, max = c.Max, step = c.Step, writable = c.Writable,
                    // A read-only channel is a MEASUREMENT, not a control: on a
                    // power box these are the input voltage, current draw,
                    // temperature, humidity and dew point. They arrive through
                    // the same generic property scan as the outlets, so without
                    // this the UI renders a dew-point reading as if it were a
                    // switch you could flip.
                    sensor = !c.Writable
                }).ToList()
            });
        });

        // Operator-assigned channel names, keyed by the channel's stable Key so
        // they survive reconnects. Sent as a full map; an empty or whitespace
        // value clears that channel's name and falls back to the driver label.
        group.MapPut("/names", (SwitchNamesRequest request,
                                ProfileService profileSvc) => {
            var rig = profileSvc.ActiveEquipmentProfile;
            if (rig == null)
                return Results.BadRequest(new { error = "No active rig" });
            if (request?.Names == null)
                return Results.BadRequest(new { error = "names required" });
            var cleaned = new Dictionary<string, string>();
            foreach (var kv in request.Names) {
                if (string.IsNullOrWhiteSpace(kv.Key)) continue;
                if (string.IsNullOrWhiteSpace(kv.Value)) continue;
                cleaned[kv.Key] = kv.Value.Trim();
            }
            rig.SwitchChannelNames = cleaned;
            profileSvc.Save();
            return Results.Ok(new { status = "saved", count = cleaned.Count });
        });

        group.MapPost("/select/{deviceName}", (string deviceName, EquipmentManager equip, string? driver) => {
            try {
                equip.SelectSwitch(driver ?? "indi", deviceName);
                return Results.Ok(new { device = deviceName, driver = driver ?? "indi" });
            } catch (NotSupportedException ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapGet("/discover", (EquipmentManager equip, string? driver) => {
            var d = (driver ?? "indi").Trim().ToLowerInvariant();
            if (d == "ascom-com")
                return Results.Ok(equip.GetAscomDrivers(
                    NINA.Ascom.Com.AscomComRegistry.DeviceType.Switch));
            if (d == "alpaca")
                return Results.Ok(equip.GetDiscoveredSwitchesFor("alpaca"));
            return Results.Ok(equip.GetDeviceNames()
                .Select(n => new DiscoveredCamera(n, n, n))
                .ToList());
        });

        group.MapGet("/drivers", (EquipmentManager equip) => {
            var alpacaCount = equip.GetDiscoveredSwitchesFor("alpaca").Count;
            var list = new List<CameraDriverInfo> {
                new("indi", "INDI", Available: true,
                    Description: "Any power box / switch driver the running INDI server exposes (e.g. indi_pegasus_upb)."),
                new("alpaca", "Alpaca (ASCOM)", Available: alpacaCount > 0,
                    Description: alpacaCount > 0
                        ? $"ASCOM-over-HTTP switch devices. {alpacaCount} discovered."
                        : "Run Alpaca Discover in RIGS first to populate this list."),
            };
            if (OperatingSystem.IsWindows()) {
                var n = equip.GetAscomDrivers(
                    NINA.Ascom.Com.AscomComRegistry.DeviceType.Switch).Count;
                list.Add(new("ascom-com", "ASCOM (COM, direct)",
                    Available: n > 0,
                    Description: n > 0
                        ? $"Direct COM-interop ISwitchV2. {n} driver(s) registered."
                        : "Install the ASCOM Platform + a switch driver."));
            }
            return Results.Ok(list);
        });

        group.MapPost("/connect", async (EquipmentManager equip) => {
            if (equip.Switch == null)
                return Results.BadRequest(new { error = "No power box selected" });
            await equip.Switch.ConnectAsync();
            return Results.Ok(new {
                connected = true, device = equip.Switch.DeviceName,
                channelCount = equip.Switch.SwitchCount
            });
        });

        group.MapPost("/disconnect", async (EquipmentManager equip) => {
            if (equip.Switch == null)
                return Results.BadRequest(new { error = "No power box selected" });
            await equip.Switch.DisconnectAsync();
            return Results.Ok(new { connected = false });
        });

        group.MapPost("/refresh", async (EquipmentManager equip) => {
            if (equip.Switch == null)
                return Results.BadRequest(new { error = "No power box connected" });
            await equip.Switch.RefreshAsync();
            return Results.Ok(new { channelCount = equip.Switch.SwitchCount });
        });

        group.MapPost("/set-bool", async (EquipmentManager equip, SetSwitchBoolRequest request) => {
            if (equip.Switch == null)
                return Results.BadRequest(new { error = "No power box connected" });
            try {
                await equip.Switch.SetBoolAsync(request.Id, request.On);
                return Results.Ok(new { id = request.Id, on = request.On });
            } catch (ArgumentOutOfRangeException ex) {
                return Results.BadRequest(new { error = ex.Message });
            } catch (InvalidOperationException ex) {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        group.MapPost("/set-value", async (EquipmentManager equip, SetSwitchValueRequest request) => {
            if (equip.Switch == null)
                return Results.BadRequest(new { error = "No power box connected" });
            try {
                await equip.Switch.SetValueAsync(request.Id, request.Value);
                return Results.Ok(new { id = request.Id, value = request.Value });
            } catch (ArgumentOutOfRangeException ex) {
                return Results.BadRequest(new { error = ex.Message });
            } catch (InvalidOperationException ex) {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });
    }

    public record SetSwitchBoolRequest(int Id, bool On);
    public record SetSwitchValueRequest(int Id, double Value);

    /// <param name="Names">Channel Key to operator-assigned name. Replaces the
    /// stored map wholesale, so the caller sends the full set.</param>
    public record SwitchNamesRequest(Dictionary<string, string> Names);
}
