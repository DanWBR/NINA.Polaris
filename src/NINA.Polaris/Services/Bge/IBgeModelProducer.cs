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

namespace NINA.Polaris.Services.Bge;

/// <summary>
/// Somewhere a 256x256x3 tensor can be turned into a background model.
///
/// Two of these exist. The host one runs the forward pass in this process
/// through whichever accelerator the board has. The client one hands the tensor
/// to the operator's browser, which has a GPU and ONNX Runtime Web, and takes
/// the answer back; that is the only path on a board with no usable
/// accelerator, and the payload is a few hundred kilobytes rather than a frame.
///
/// The contract is deliberately thin, because the caller never waits for it:
/// <see cref="BgeFrameCorrector"/> starts a request and keeps correcting frames
/// with the background it already has until the answer turns up.
/// </summary>
public interface IBgeModelProducer {

    /// <summary>"host" or "client". Shown to the operator and recorded on the
    /// background, so the status line can say where it ran.</summary>
    string Name { get; }

    /// <summary>True when a request would have somewhere to go: an accelerator
    /// that resolved a model, or a browser currently connected. False is a
    /// normal, quiet state, not an error, and the frame is simply stacked
    /// uncorrected.</summary>
    bool CanRun { get; }

    /// <summary>Run one forward pass. Returns the raw NHWC output
    /// (<c>tile*tile*3</c>), or null when it could not run; must not throw for
    /// an ordinary failure, because this runs detached from the frame that
    /// asked for it.</summary>
    Task<float[]?> RunAsync(float[] nhwcTensor, int tile, CancellationToken ct);
}
