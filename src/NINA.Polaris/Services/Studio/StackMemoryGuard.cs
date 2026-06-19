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

namespace NINA.Polaris.Services.Studio;

/// <summary>
/// Pre-flight RAM guard for the STUDIO integration jobs (master frames +
/// light integration). The tiled/streaming integrators already keep peak
/// memory flat, but the unavoidable parts — the full-resolution output
/// buffer, plus a single frame's debayer/resample transient during light
/// alignment — still scale with sensor size. On a small SBC a big enough
/// sensor can still exhaust RAM and trip the OOM killer, which kills the
/// whole process instead of failing one job.
///
/// This guard estimates the job's peak working set and, if it won't fit in
/// the currently-available system memory (with a fixed reserve for the OS +
/// the rest of the process), refuses the job up front with an actionable
/// message instead of risking the OOM killer. When available memory is
/// unknown (metrics not sampled yet) it allows the job — fail-open, since a
/// false refusal is worse than the pre-existing behaviour.
/// </summary>
public static class StackMemoryGuard {
    private const long Mb = 1024 * 1024;

    /// <summary>Headroom kept free for the OS and the rest of the Polaris
    /// process (web server, equipment drivers, caches). The job must fit in
    /// <c>available - this</c>.</summary>
    public const long ReserveBytes = 256 * Mb;

    /// <summary>
    /// Estimate the peak additional working set of a <b>master frame</b>
    /// integration. The tiled integrator holds the mono output buffer plus
    /// one tile (~<paramref name="stripBudgetBytes"/>) of decoded strips
    /// across the inputs.
    /// </summary>
    public static long EstimateMasterBytes(int width, int height, long stripBudgetBytes) {
        long output = (long)width * height * 2;          // mono ushort master
        return output + stripBudgetBytes;
    }

    /// <summary>
    /// Estimate the peak additional working set of a <b>light</b> integration.
    /// Two phases bound it:
    ///   • align: one frame resident as raw + (for OSC) debayered + resampled
    ///     planes — ~7× a plane for colour, ~2× for mono.
    ///   • integrate: the full output (W·H·planes) plus one strip tile.
    /// Peak is the larger of the two.
    /// </summary>
    public static long EstimateLightBytes(int width, int height, int planes, long stripBudgetBytes) {
        long plane = (long)width * height * 2;
        long alignPhase = plane * (planes == 3 ? 7 : 2);
        long integratePhase = plane * planes + stripBudgetBytes;
        return Math.Max(alignPhase, integratePhase);
    }

    /// <summary>
    /// Decide whether a job needing <paramref name="requiredBytes"/> may run
    /// given <paramref name="availableBytes"/> of free system memory.
    /// <paramref name="availableBytes"/> &lt;= 0 means "unknown" → allowed.
    /// Returns <c>(true, null)</c> to proceed, or <c>(false, message)</c>
    /// with a user-facing explanation.
    /// </summary>
    public static (bool ok, string? message) Check(
            long requiredBytes, long availableBytes, string jobLabel) {
        if (availableBytes <= 0) return (true, null);   // metrics unknown → fail-open

        long budget = availableBytes - ReserveBytes;
        if (requiredBytes > budget) {
            var msg =
                $"Not enough memory to {jobLabel}: needs about {Round(requiredBytes)} MB " +
                $"but only about {Round(availableBytes)} MB is free " +
                $"({Round(ReserveBytes)} MB is reserved for the system). " +
                "Reduce the number of frames, use a smaller sensor / binning, " +
                "or close other applications and try again.";
            return (false, msg);
        }
        return (true, null);
    }

    private static long Round(long bytes) => bytes / Mb;
}
