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

public static class RotatorEndpoints {
    public static void MapRotatorEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/rotator");

        group.MapGet("/status", (EquipmentManager equip) => {
            if (equip.Rotator == null)
                return Results.Ok(new {
                    connected = false,
                    position = 0.0,
                    moving = false,
                    reversed = false
                });

            var pos = equip.Rotator.Position;
            return Results.Ok(new {
                connected = equip.Rotator.IsConnected,
                name = equip.Rotator.DeviceName,
                position = double.IsNaN(pos) ? 0.0 : pos,
                moving = equip.Rotator.IsMoving,
                reversed = equip.Rotator.IsReversed
            });
        });

        group.MapPost("/move", async (EquipmentManager equip, MoveRotatorRequest request) => {
            if (equip.Rotator == null)
                return Results.BadRequest(new { error = "No rotator selected" });

            await equip.Rotator.MoveToAsync(request.Angle);
            return Results.Ok(new { status = "moving", target = request.Angle });
        });

        group.MapPost("/reverse", async (EquipmentManager equip, ReverseRequest request) => {
            if (equip.Rotator == null)
                return Results.BadRequest(new { error = "No rotator selected" });

            await equip.Rotator.ReverseAsync(request.Reversed);
            return Results.Ok(new { reversed = request.Reversed });
        });

        group.MapPost("/abort", async (EquipmentManager equip) => {
            if (equip.Rotator == null)
                return Results.BadRequest(new { error = "No rotator selected" });

            await equip.Rotator.AbortAsync();
            return Results.Ok(new { status = "stopped" });
        });

        group.MapPost("/select/{deviceName}", (EquipmentManager equip, string deviceName) => {
            equip.SelectRotator(deviceName);
            return Results.Ok(new { selected = deviceName });
        });

        group.MapPost("/connect", async (EquipmentManager equip) => {
            if (equip.Rotator == null)
                return Results.BadRequest(new { error = "No rotator selected" });

            return await DeviceConnectGuard.RunAsync(
                "connect", equip.Rotator.DeviceName,
                ct => equip.Rotator.ConnectAsync(ct),
                () => Results.Ok(new { status = "connected", device = equip.Rotator.DeviceName }));
        });

        group.MapPost("/disconnect", async (EquipmentManager equip) => {
            if (equip.Rotator == null)
                return Results.BadRequest(new { error = "No rotator selected" });

            return await DeviceConnectGuard.RunAsync(
                "disconnect", equip.Rotator.DeviceName,
                ct => equip.Rotator.DisconnectAsync(ct),
                () => Results.Ok(new { status = "disconnected" }));
        });

        // Manual rotator: how far to turn the camera by hand, and which way,
        // to reach the framing angle set on the SKY map. Pure arithmetic over
        // a solve the caller already has, so it needs neither a camera nor a
        // rotator: with a manual one the operator IS the loop (measure, turn,
        // measure again) and this is one step of it. The reverse override
        // comes from the active rig rather than the request, so every client
        // of this host is told the same direction.
        group.MapPost("/framing-advice", (FramingAdviceRequest request, ProfileService profiles) => {
            // Both angles are required rather than defaulted: a body missing
            // them would otherwise answer "0 to 0, nothing to turn", which
            // reads as a measurement the caller never made.
            if (request?.TargetRotationDeg is not double target
                    || request.SolvedRotationDeg is not double solved)
                return Results.BadRequest(new { error = "targetRotationDeg + solvedRotationDeg required" });
            if (!double.IsFinite(target) || !double.IsFinite(solved))
                return Results.BadRequest(new { error = "targetRotationDeg and solvedRotationDeg must be finite" });

            var advice = ManualRotatorAdvice.Compute(
                target, solved,
                request.Cd11, request.Cd12, request.Cd21, request.Cd22,
                // The caller's live toggle wins over the stored one. The UI
                // writes the rig through a debounced patch, so a measurement
                // taken right after "other way" would otherwise be answered
                // from the value the host has not been told about yet, and the
                // instruction would flip back under the operator's hands.
                reverse: request.Reverse ?? profiles.ActiveEquipmentProfile?.ManualRotatorReverse == true,
                toleranceDeg: request.ToleranceDeg ?? ManualRotatorAdvice.DefaultToleranceDeg);

            return Results.Ok(new {
                solvedPa = advice.SolvedPa,
                targetPa = advice.TargetPa,
                deltaDeg = advice.DeltaDeg,
                turnDeg = advice.TurnDeg,
                direction = advice.Direction,
                withinTolerance = advice.WithinTolerance,
                mirrored = advice.Mirrored,
                reversed = advice.Reversed
            });
        });
    }

    public record MoveRotatorRequest(double Angle);
    public record ReverseRequest(bool Reversed);
    /// <summary>Body for /framing-advice. The CD matrix is optional: it
    /// carries the field's parity, which sets the default turn direction.
    /// Without it the advice assumes an unmirrored train, and the rig's
    /// reverse flag is what corrects a wrong guess.</summary>
    public record FramingAdviceRequest(
        double? TargetRotationDeg,
        double? SolvedRotationDeg,
        double? Cd11 = null,
        double? Cd12 = null,
        double? Cd21 = null,
        double? Cd22 = null,
        double? ToleranceDeg = null,
        /// <summary>Override the rig's stored turn direction for this answer.
        /// Absent = use the rig's.</summary>
        bool? Reverse = null);
}