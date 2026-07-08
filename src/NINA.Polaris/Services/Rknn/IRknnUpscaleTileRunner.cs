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
/// Abstraction over "run one super-resolution tile". Unlike the same-size
/// <see cref="IRknnTileRunner"/>, the upscale model's OUTPUT is <see cref="Scale"/>×
/// larger than its input: a <c>[1, TileSize, TileSize, 3]</c> NHWC fp32 low-res tile
/// in, a <c>[1, TileSize*Scale, TileSize*Scale, 3]</c> high-res tile out. Tests
/// substitute a mock so the tiling / normalization / stitch math in
/// <see cref="RknnPipelines.RunUpscale"/> can be verified without an NPU.
/// </summary>
public interface IRknnUpscaleTileRunner : IDisposable {
    /// <summary>Low-res model input width/height (128 for the Polaris upscale model).</summary>
    int TileSize { get; }

    /// <summary>Output magnification (2 for the shipped model).</summary>
    int Scale { get; }

    /// <summary>
    /// Run a single LR tile. <paramref name="nhwcInput"/> is row-major NHWC of
    /// length <c>TileSize*TileSize*3</c>. Returns the HR output as a freshly
    /// allocated fp32 array of length <c>(TileSize*Scale)*(TileSize*Scale)*3</c>.
    /// The runner must not retain a reference to the input array.
    /// </summary>
    float[] RunTile(float[] nhwcInput);
}
