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

namespace NINA.Polaris.Services;

/// <summary>
/// Process-wide serialization for native <c>ICamera.CaptureAsync</c> calls on
/// the MAIN imaging camera. Concurrent captures on one camera handle call the
/// vendor SDK / INDI BLOB path reentrantly, which crashes the native driver and
/// takes the whole server down — observed when, e.g., the LIVE capture loop runs
/// in one browser tab while the FOCUS-manual loop runs in another, or a sequence
/// overlaps a manual snap.
///
/// IMPORTANT — keep the guarded region NARROW: wrap only the single
/// <c>CaptureAsync</c> call, never a whole workflow. The semaphore is NOT
/// reentrant, so holding it across a step that itself captures (e.g. a sequence
/// holding it while triggering autofocus) would deadlock. Acquire → capture →
/// release, every time.
///
/// The guide camera (NativeGuider) is a SEPARATE device and deliberately does
/// NOT use this gate — guiding must keep running while the main camera images.
/// </summary>
public static class CameraCaptureGate {
    private static readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Run a main-camera capture under the gate. A second caller queues
    /// behind the first instead of racing it into the native driver.
    ///
    /// <paramref name="acquireTimeout"/> caps how long we wait to ENTER the gate
    /// (not the capture itself). If the holder wedges in the native driver, a
    /// queued capture would otherwise block forever and freeze its shutter/
    /// progress at 0; on timeout we throw <see cref="TimeoutException"/> so the
    /// caller fails fast and the UI resets instead of hanging until a restart.
    /// Null = wait indefinitely (legacy).</summary>
    public static async Task<T> RunAsync<T>(Func<Task<T>> capture,
            CancellationToken ct = default, TimeSpan? acquireTimeout = null) {
        if (acquireTimeout is { } to) {
            if (!await _gate.WaitAsync(to, ct))
                throw new TimeoutException(
                    "Camera busy: a previous capture did not release within "
                    + $"{to.TotalSeconds:0}s (driver may be wedged).");
        } else {
            await _gate.WaitAsync(ct);
        }
        try { return await capture(); }
        finally { _gate.Release(); }
    }

    /// <summary>Non-generic overload for capture calls that return a plain Task.</summary>
    public static async Task RunAsync(Func<Task> capture, CancellationToken ct = default,
            TimeSpan? acquireTimeout = null) {
        if (acquireTimeout is { } to) {
            if (!await _gate.WaitAsync(to, ct))
                throw new TimeoutException(
                    "Camera busy: a previous capture did not release within "
                    + $"{to.TotalSeconds:0}s (driver may be wedged).");
        } else {
            await _gate.WaitAsync(ct);
        }
        try { await capture(); }
        finally { _gate.Release(); }
    }
}
