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

using NINA.Polaris.Services.Broadcast;

namespace NINA.Polaris.WebSocket.Status;

/// <summary>
/// The live broadcast.
///
/// <para>Blocks owned: broadcast.</para>
///
/// <para>Every field here answers a question an operator asks while something
/// is being published in their name: is it on air, where is it going, is it
/// keeping up, has it dropped and come back, and is the file being written.
/// The stream key is not among them and never will be.</para>
/// </summary>
public sealed class BroadcastStatusContributor : IStatusContributor {
    private readonly BroadcastService _broadcast;

    public BroadcastStatusContributor(BroadcastService broadcast) => _broadcast = broadcast;

    public IReadOnlyCollection<string> Keys { get; } = new[] { "broadcast" };

    public void Contribute(StatusTick tick) {
        var s = _broadcast.GetStatus();
        tick.Blocks["broadcast"] = new {
            running = s.Running,
            destination = s.Destination,
            quality = s.Quality,
            encoder = s.Encoder,
            // Whether the board is encoding in silicon. A high quality setting
            // on a Pi doing H.264 in software is a promise it cannot keep, and
            // this is what the card says so with.
            hardwareEncoder = s.HardwareEncoder,
            uptimeSec = s.UptimeSec,
            fps = s.Fps,
            bitrateKbps = s.BitrateKbps,
            // Frames ffmpeg threw away: the number that says the host is too
            // slow for the chosen quality.
            droppedFrames = s.DroppedFrames,
            // Times the encoder died and was restarted. On a hotspot at a dark
            // site this climbing slowly is normal; climbing fast is the uplink.
            reconnects = s.Reconnects,
            recording = s.Recording,
            recordPath = s.RecordPath,
            lastError = s.LastError,
            // Which of the three pictures the last frame came from, which is
            // what makes a broadcast showing a stale image diagnosable.
            frameSource = s.FrameSource,
            // The corner picture: which source, and why it is blank when it
            // is. A guide frame needs the loop running, a snapshot URL needs
            // a camera that answers, and neither failure is visible on air.
            pipSource = s.PipSource,
            pipError = s.PipError,
            framesSent = s.FramesSent
        };
    }
}
