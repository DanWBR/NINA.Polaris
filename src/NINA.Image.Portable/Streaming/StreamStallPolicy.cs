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

namespace NINA.Image.Portable.Streaming;

/// <summary>
/// The timing rules a video stream uses to tell "no frame yet" from "the
/// driver has stalled", shared by the native SDK pull loops and the stream
/// service watchdog so both agree on what a stall is.
///
/// Field case (ASI585MC on an Orange Pi 5 Pro, 2026-09-03): changing gain or
/// exposure while streaming left the camera silent until the operator
/// reconnected, several times in one night. Two things made it worse than
/// it had to be: the pull loop waited on the SDK with a timeout computed
/// from the ORIGINAL exposure, so a longer exposure set live could never
/// return a frame in time, and nothing ever tried to restart the capture.
/// </summary>
public static class StreamStallPolicy {
    /// <summary>Longest a single blocking wait on the SDK may last. Kept
    /// short so control writes and status reads, which share the SDK lock,
    /// never queue behind a long exposure; a timeout here is "not yet", not
    /// a failure, and the frame is not lost.</summary>
    public const int MaxPollWaitMs = 250;

    /// <summary>Consecutive capture restarts without a frame before the
    /// stream gives up and reports the camera as unresponsive.</summary>
    public const int MaxRestarts = 3;

    /// <summary>How long to block on one SDK poll for the given exposure:
    /// twice the exposure plus half a second (the SDK vendors' own advice),
    /// capped at <see cref="MaxPollWaitMs"/> and never below 50 ms.</summary>
    public static int PollWaitMs(double exposureSeconds) {
        double ms = exposureSeconds * 1000 * 2 + 500;
        if (ms < 50) ms = 50;
        if (ms > MaxPollWaitMs) ms = MaxPollWaitMs;
        return (int)ms;
    }

    /// <summary>Silence after which the stream is considered stalled: four
    /// exposures, and at least three seconds so short-exposure planetary
    /// streams tolerate a USB hiccup without a restart storm.</summary>
    public static TimeSpan StallAfter(double exposureSeconds) {
        double s = exposureSeconds * 4;
        if (s < 3) s = 3;
        return TimeSpan.FromSeconds(s);
    }

    /// <summary>True when no frame has arrived for longer than
    /// <see cref="StallAfter"/>.</summary>
    public static bool IsStalled(DateTime lastFrameUtc, DateTime nowUtc, double exposureSeconds) =>
        nowUtc - lastFrameUtc > StallAfter(exposureSeconds);
}
