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

using NINA.Polaris.Services.Planetary;
using NINA.Polaris.Services.Sequencer;

namespace NINA.Polaris.Services;

/// <summary>
/// UPDGATE: one answer to "is this host in the middle of something the
/// operator would hate to lose?".
///
/// <para>The question kept being asked implicitly and answered differently in
/// each place. On 2026-07-31 the self-update took it for granted: the operator
/// pressed Update during a live session and the journal shows the service
/// stopped at 20:38:44 and came back at 20:39:46, a minute of imaging gone.
/// From the tablet it looked like the board had rebooted.</para>
///
/// <para>Deliberately a LIST of names rather than a boolean: refusing an
/// action is only tolerable when it says what it is protecting, and "a live
/// stack and a running sequence" is the difference between the operator
/// cancelling and the operator overriding on purpose.</para>
///
/// <para>What counts as activity is anything a process restart would break
/// beyond repair (a night of frames, a running sequence, a recording in
/// progress). A connected camera or a mount that is merely tracking does NOT
/// count: those reconnect on their own, and blocking updates on them would
/// mean never being able to update.</para>
/// </summary>
public class HostActivityService {
    private readonly SequenceEngine _sequence;
    private readonly AdvancedSequenceEngine _adv;
    private readonly LiveCaptureService _liveCapture;
    private readonly LiveStackingService _liveStack;
    private readonly VideoRecordingService _video;
    private readonly AutoFocusService _autoFocus;
    private readonly CameraStreamService _stream;

    public HostActivityService(SequenceEngine sequence,
                               AdvancedSequenceEngine adv,
                               LiveCaptureService liveCapture,
                               LiveStackingService liveStack,
                               VideoRecordingService video,
                               AutoFocusService autoFocus,
                               CameraStreamService stream) {
        _sequence = sequence;
        _adv = adv;
        _liveCapture = liveCapture;
        _liveStack = liveStack;
        _video = video;
        _autoFocus = autoFocus;
        _stream = stream;
    }

    /// <summary>Human-readable names of what is running right now, empty when
    /// the host is idle. Ordered by how much a restart would cost.</summary>
    public IReadOnlyList<string> Current() {
        var busy = new List<string>();
        try {
            if (_video.IsRecording) busy.Add("a video recording");
            if (_sequence.State != SequenceState.Idle) busy.Add("a sequence (AUTORUN)");
            // PLAN compiles down to the advanced engine, so this covers both.
            if (_adv.State != AdvancedSequenceState.Idle) busy.Add("a sequence (PLAN / ADV)");
            if (_liveCapture.IsRunning) busy.Add("the LIVE capture loop");
            if (_liveStack.IsRunning) busy.Add("live stacking");
            if (_autoFocus.State != AutoFocusState.Idle) busy.Add("auto-focus");
            // The stream is last and is the one soft entry: losing it costs a
            // click, not data. It is listed because a restart DOES kill it and
            // the operator should not be surprised.
            if (_stream.IsRunning) busy.Add("the camera stream");
        } catch {
            // A service that throws while being asked is not a reason to let a
            // restart through blind; treat the unknown as busy.
            busy.Add("an unknown activity (a service did not answer)");
        }
        return busy;
    }

    public bool IsBusy => Current().Count > 0;

    /// <summary>One sentence for a toast or an error body.</summary>
    public string Describe() {
        var busy = Current();
        return busy.Count switch {
            0 => "nothing is running",
            1 => busy[0],
            2 => $"{busy[0]} and {busy[1]}",
            _ => string.Join(", ", busy.Take(busy.Count - 1)) + " and " + busy[^1]
        };
    }
}
