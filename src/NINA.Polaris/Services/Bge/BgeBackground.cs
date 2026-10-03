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
/// A background model, held between frames.
///
/// <paramref name="Output"/> is the raw model output, NHWC and
/// <c>Tile*Tile*3</c> long, kept in the model's **normalised** space rather
/// than in brightness. That is the point of the type: what a later frame reuses
/// is the SHAPE of the gradient, and it is denormalised against that frame's own
/// median and MAD. Storing brightness instead would freeze the sky level of the
/// frame the model ran on, and as the sky brightens (moon rising, target
/// sinking) the correction would pull the stack off.
///
/// Small on purpose: a 256x256x3 float array is 768 KB whatever the sensor, so
/// holding one costs nothing next to the live stack's own buffers, and it can
/// cross to a browser and back without the frame going anywhere.
/// </summary>
/// <param name="Output">Raw NHWC model output, length <c>Tile*Tile*3</c>.</param>
/// <param name="Tile">Model window, 256 for the GraXpert background models.</param>
/// <param name="Width">Frame width this was computed for.</param>
/// <param name="Height">Frame height this was computed for.</param>
/// <param name="Channels">1 for mono, 3 for colour.</param>
/// <param name="Filter">Filter in the light path when it was computed, or null.
/// A different filter is a different gradient, so it invalidates.</param>
/// <param name="ComputedOnFrame">Frame index it was computed on, for the age
/// shown to the operator and for the recompute cadence.</param>
/// <param name="Producer">Where it ran, "host" or "client", for the status
/// line.</param>
/// <param name="ElapsedMs">How long the forward pass took, for the status
/// line.</param>
public sealed record BgeBackground(
    float[] Output,
    int Tile,
    int Width,
    int Height,
    int Channels,
    string? Filter,
    long ComputedOnFrame,
    string Producer,
    double ElapsedMs) {

    /// <summary>True when this model still describes the frame being stacked.
    /// Geometry has to match exactly: a binning change, a subframe or a
    /// different camera makes the upsample meaningless.</summary>
    public bool MatchesGeometry(int width, int height, int channels, string? filter)
        => Width == width && Height == height && Channels == channels
           && string.Equals(Filter ?? "", filter ?? "", StringComparison.OrdinalIgnoreCase);
}
