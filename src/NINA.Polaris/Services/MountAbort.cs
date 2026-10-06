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

using NINA.Image.Interfaces;

namespace NINA.Polaris.Services;

/// <summary>
/// Stopping the mount on an operator cancel, and leaving it as it was found.
/// </summary>
internal static class MountAbort {

    /// <summary>How long to let the driver finish the abort before asking for
    /// tracking again. A mount that is still unwinding an abort can swallow the
    /// write, and a few hundred milliseconds is nothing next to the slew that
    /// was just cancelled.</summary>
    internal static TimeSpan SettleAfterAbort { get; set; } = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Abort whatever the mount is doing, then put tracking back the way the
    /// operation found it.
    ///
    /// <para>Cancelling a centering run leaves the mount wherever it had got
    /// to, which is the point of a cancel. It should not also leave the mount
    /// dead on the sky. A GoTo is where tracking goes down: plenty of drivers
    /// report tracking off for the duration of a slew and turn it back on when
    /// the slew completes, and some stop tracking on an abort outright. Either
    /// way the step that would have restored it belongs to the pipeline being
    /// cancelled, so nothing restores it. Reported from the field (issue #32):
    /// tracking on, plate solve starts, Stop pressed mid-solve, and the mount
    /// is left not tracking with nothing in the interface saying so.</para>
    ///
    /// <para>The restore is unconditional rather than conditional on a fresh
    /// read of <see cref="ITelescope.IsTracking"/>: that property is served
    /// from the last state the driver published, which right after an abort is
    /// whatever it was before the abort landed. Asking a mount that is already
    /// tracking to track is a no-op write.</para>
    ///
    /// <para>Every failure here is logged and swallowed. This runs on the
    /// cancel path, where the caller has already decided to stop, and throwing
    /// would only replace one problem with another.</para>
    /// </summary>
    public static async Task AbortAndRestoreTrackingAsync(
            ITelescope? scope, bool trackingWasOn, Microsoft.Extensions.Logging.ILogger logger,
            CancellationToken ct = default) {
        if (scope == null) return;

        try {
            await scope.AbortSlewAsync(ct);
        } catch (Exception ex) {
            logger.LogWarning(ex, "AbortSlew on cancel failed");
        }

        if (!trackingWasOn) return;

        try {
            if (SettleAfterAbort > TimeSpan.Zero)
                await Task.Delay(SettleAfterAbort, ct);
            await scope.SetTrackingAsync(true, ct);
            logger.LogInformation("Tracking restored after cancel");
        } catch (Exception ex) {
            logger.LogWarning(ex, "Could not restore tracking after cancel");
        }
    }

    /// <summary>Was the mount tracking, as far as the driver has said? Used to
    /// record the state a job found, so a cancel can put it back.</summary>
    public static bool WasTracking(ITelescope? scope)
        => scope is { IsConnected: true, IsTracking: true };
}
