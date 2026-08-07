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

namespace NINA.Polaris.Endpoints;

/// <summary>
/// Bounds a device connect or disconnect so a driver that never answers cannot
/// hold a request open forever.
///
/// Every one of these endpoints used to call ConnectAsync() with no token and no
/// deadline. Connecting a device whose driver is not actually there is not an
/// exotic case: the profile keeps the last selection, and moving a rig to
/// different hardware leaves a stale device selected, one click away.
///
/// Field report 2026-08-06: a Gemini focuser left selected via INDI on a rig
/// whose focuser is a ZWO EAF. Connecting it took the whole server down - the
/// operator could still find Polaris from the Android app, but the page came up
/// blank, so the process was alive and not serving. The board had to be
/// rebooted, mid-session.
///
/// This does not identify what hung; it makes the hang survivable and legible.
/// A device that does not answer within the deadline gets a 504 that names the
/// device, and the request thread and the operator both go free.
/// </summary>
internal static class DeviceConnectGuard {

    /// <summary>Generous enough for a real driver on a slow SBC (an INDI
    /// CONNECT can legitimately take tens of seconds while the driver opens
    /// USB and populates properties), short enough that a wedged one does not
    /// look like a dead server.</summary>
    internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(45);

    /// <summary>Bound a connect/disconnect that is followed by more work in the
    /// endpoint (the camera routes re-apply saved native controls afterwards),
    /// so the deadline still applies without swallowing the rest of the body.
    /// Surfaces as a TimeoutException carrying the same explanation.</summary>
    internal static async Task BoundedAsync(
            string verb, string device, Func<CancellationToken, Task> op,
            TimeSpan? deadline = null) {
        using var cts = new CancellationTokenSource(deadline ?? Deadline);
        try {
            await op(cts.Token);
        } catch (OperationCanceledException) when (cts.IsCancellationRequested) {
            throw new TimeoutException(Explain(verb, device, deadline ?? Deadline));
        }
    }

    private static string Explain(string verb, string device, TimeSpan deadline) =>
        $"The {verb} of '{device}' got no answer within {deadline.TotalSeconds:F0}s. "
        + "The driver is most likely not running, or the device is not the one this rig "
        + "actually has. Check the device selection and that its INDI driver is up.";

    /// <summary>Run a connect/disconnect under the deadline. <paramref name="onOk"/>
    /// builds the endpoint's own success payload, so each route keeps the shape
    /// its client already expects.</summary>
    internal static async Task<IResult> RunAsync(
            string verb, string device, Func<CancellationToken, Task> op, Func<IResult> onOk,
            TimeSpan? deadline = null) {
        using var cts = new CancellationTokenSource(deadline ?? Deadline);
        try {
            await op(cts.Token);
            return onOk();
        } catch (OperationCanceledException) when (cts.IsCancellationRequested) {
            return Results.Json(
                new { error = Explain(verb, device, deadline ?? Deadline), device, timedOut = true },
                               statusCode: 504);
        }
    }
}
