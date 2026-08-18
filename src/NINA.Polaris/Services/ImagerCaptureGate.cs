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

using System.Collections.Concurrent;

namespace NINA.Polaris.Services;

/// <summary>
/// Process-wide serialization for native <c>ICamera.CaptureAsync</c> calls on an
/// EXTRA imaging camera (imager slot index 2+), one semaphore per slot. Same
/// rationale as <see cref="CameraCaptureGate"/> / <see cref="AuxCameraCaptureGate"/>
/// but for the N extra cameras: each extra imager has two possible consumers —
/// its own capture+save loop (<see cref="MultiImagerCaptureService"/>) and a
/// one-shot Preview / Focus / Solve capture when the user targets that camera —
/// and a concurrent capture on one handle crashes the vendor SDK / INDI BLOB
/// path. Per-slot semaphores keep each imager independent so one camera's capture
/// never blocks (or is blocked by) another.
/// </summary>
public static class ImagerCaptureGate {
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> _gates = new();

    private static SemaphoreSlim GateFor(int index) =>
        _gates.GetOrAdd(index, _ => new SemaphoreSlim(1, 1));

    /// <summary>Run an extra-imager capture under that slot's gate (see
    /// <see cref="CameraCaptureGate.RunAsync{T}"/> for the timeout semantics).</summary>
    public static async Task<T> RunAsync<T>(int index, Func<Task<T>> capture,
            CancellationToken ct = default, TimeSpan? acquireTimeout = null) {
        var gate = GateFor(index);
        if (acquireTimeout is { } to) {
            if (!await gate.WaitAsync(to, ct))
                throw new TimeoutException(
                    $"Imager {index} busy: a previous capture did not release within "
                    + $"{to.TotalSeconds:0}s (driver may be wedged).");
        } else {
            await gate.WaitAsync(ct);
        }
        try { return await capture(); }
        finally { gate.Release(); }
    }
}
