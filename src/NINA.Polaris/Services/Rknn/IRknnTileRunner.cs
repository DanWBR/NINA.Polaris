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

namespace NINA.Polaris.Services.Rknn;

/// <summary>
/// Abstraction over "run one model tile". The real implementation is
/// <see cref="RknnSession"/> (NPU via librknnrt); tests substitute a mock so
/// the tiling / normalization math in <c>RknnInferenceService</c> can be
/// verified without an NPU. The GraXpert AI models all take a
/// <c>[1, 256, 256, 3]</c> NHWC fp32 input and return a same-shaped fp32
/// output, so a single-tensor in/out contract is sufficient.
/// </summary>
public interface IRknnTileRunner : IDisposable {
    /// <summary>Model input width/height (256 for the GraXpert models).</summary>
    int TileSize { get; }

    /// <summary>Model input channel count (3 — NHWC RGB).</summary>
    int Channels { get; }

    /// <summary>
    /// Run a single tile. <paramref name="nhwcInput"/> is row-major NHWC of
    /// length <c>TileSize*TileSize*Channels</c>. Returns the model output as a
    /// freshly allocated fp32 array. The runner must not retain a reference to
    /// the input array (the caller reuses it across tiles).
    /// </summary>
    float[] RunTile(float[] nhwcInput);
}
