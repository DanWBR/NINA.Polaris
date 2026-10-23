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

namespace NINA.Ascom.Com;

/// <summary>
/// Decides, poll by poll, when an ASCOM exposure that has not produced an
/// image is never going to.
///
/// <para>A ToupTek ATR2600C took five 60 s darks and then its in-process
/// driver stopped answering: StartExposure still returned, but ImageReady
/// stayed false and every property read threw. The capture waited the full
/// exposure + 120 s twice per frame and reported only "ImageReady didn't go
/// true". This gives up as soon as the driver says so (CameraState Error), or
/// once it has been idle or unreadable for <see cref="StallSeconds"/> past the
/// end of the exposure, and keeps the driver's own message for the error.</para>
/// </summary>
public sealed class AscomExposureWatch {
    /// <summary>How long after the exposure should have ended a driver may sit
    /// idle, or throw on every read, before the exposure is given up. A
    /// compliant driver reports Reading / Download while it fetches the frame,
    /// so this only has to cover drivers that report Idle a little early.</summary>
    public const double StallSeconds = 30;

    private const int StateIdle = 0;
    private const int StateError = 5;

    private readonly double _exposureSeconds;
    private readonly bool _stateKnownToWork;
    private double? _stallSince;
    private double? _readyFailingSince;

    /// <summary>The last message the driver threw while being polled.</summary>
    public string? LastDriverError { get; private set; }

    /// <param name="stateKnownToWork">Whether this driver has answered
    /// CameraState before on this connection. Only then does a read that
    /// throws count as the driver failing rather than not implementing it.</param>
    public AscomExposureWatch(double exposureSeconds, bool stateKnownToWork) {
        _exposureSeconds = Math.Max(0, exposureSeconds);
        _stateKnownToWork = stateKnownToWork;
    }

    /// <summary>One poll that found no image yet. <paramref name="state"/> is
    /// ICameraV3.CameraState, or null when the read threw; the errors are the
    /// messages of whatever threw. Returns why the exposure has failed, or
    /// null to keep waiting.</summary>
    public string? Observe(double elapsedSeconds, bool readyThrew, string? readyError,
            int? state, string? stateError) {
        if (readyError != null) LastDriverError = readyError;
        if (stateError != null) LastDriverError = stateError;

        if (state == StateError)
            return "the camera reports an error" + Detail();

        // ImageReady itself failing, for any reason, at any point.
        if (readyThrew) {
            _readyFailingSince ??= elapsedSeconds;
            if (elapsedSeconds - _readyFailingSince.Value >= StallSeconds)
                return "the driver fails every ImageReady read" + Detail();
        } else {
            _readyFailingSince = null;
        }

        // Past the end of the exposure: idle, or unreadable from a driver
        // that used to answer, means nothing is being read out.
        bool stalled = state == StateIdle || (state == null && _stateKnownToWork);
        if (elapsedSeconds >= _exposureSeconds && stalled) {
            _stallSince ??= elapsedSeconds;
            if (elapsedSeconds - _stallSince.Value >= StallSeconds)
                return (state == StateIdle
                    ? "the camera went idle without delivering an image"
                    : "the driver stopped answering") + Detail();
        } else {
            _stallSince = null;
        }
        return null;
    }

    private string Detail() => LastDriverError is { Length: > 0 } e ? $" ({e})" : "";
}
