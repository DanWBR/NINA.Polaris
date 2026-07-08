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
/// Abstraction over "run one deconvolution tile". Unlike the single-tensor
/// <see cref="IRknnTileRunner"/> used by BGE / Denoise / StarNet, the GraXpert
/// deconvolution models (stars v1.0.0, objects v1.0.1) take TWO inputs:
///   • the image tile <c>[1, 1, 512, 512]</c> NCHW fp32 (log-mean-std normalized), and
///   • a <c>params</c> tensor <c>[1, 2]</c> = <c>[sigmaNormalized, effStrength]</c>,
/// and return a same-shaped <c>[1, 1, 512, 512]</c> fp32 <b>residual</b> (the model
/// predicts the correction to subtract, not the corrected image).
///
/// The <c>params</c> vector is constant for the whole image, so a batched runner
/// can broadcast it to every tile. Tests substitute a mock so the tiling /
/// normalization / inverse-log math in <see cref="RknnPipelines.RunDecon"/> can be
/// verified without an NPU.
/// </summary>
public interface IRknnDeconTileRunner : IDisposable {
    /// <summary>Model input width/height (512 for the GraXpert decon models).</summary>
    int TileSize { get; }

    /// <summary>
    /// Run a single decon tile. <paramref name="chwInput"/> is row-major
    /// <c>[1,1,TileSize,TileSize]</c> fp32 (the normalized image plane);
    /// <paramref name="pars"/> is the length-2 <c>[sigmaNormalized, effStrength]</c>
    /// tensor. Returns the model's residual output as a freshly allocated fp32
    /// array of length <c>TileSize*TileSize</c>. The runner must not retain a
    /// reference to either input array (the caller reuses them across tiles).
    /// </summary>
    float[] RunTile(float[] chwInput, float[] pars);
}
