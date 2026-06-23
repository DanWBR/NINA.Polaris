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
/// the AUXILIARY camera. Same rationale as <see cref="CameraCaptureGate"/> but
/// for a SEPARATE device: the aux camera has two possible consumers — the aux
/// capture+save loop (<see cref="AuxCaptureService"/>) and the FOCUS-tab manual
/// focus loop when the user points it at the aux camera — and a concurrent
/// capture on one handle crashes the vendor SDK / INDI BLOB path. This is a
/// distinct semaphore from the main gate so aux captures never block (or are
/// blocked by) the main imaging camera.
/// </summary>
public static class AuxCameraCaptureGate {
    private static readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Run an aux-camera capture under the gate (see
    /// <see cref="CameraCaptureGate.RunAsync{T}"/> for the timeout semantics).</summary>
    public static async Task<T> RunAsync<T>(Func<Task<T>> capture,
            CancellationToken ct = default, TimeSpan? acquireTimeout = null) {
        if (acquireTimeout is { } to) {
            if (!await _gate.WaitAsync(to, ct))
                throw new TimeoutException(
                    "Aux camera busy: a previous capture did not release within "
                    + $"{to.TotalSeconds:0}s (driver may be wedged).");
        } else {
            await _gate.WaitAsync(ct);
        }
        try { return await capture(); }
        finally { _gate.Release(); }
    }
}
