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

using Microsoft.Extensions.Logging;
using NINA.Polaris.Services.External;

namespace NINA.Polaris.Services.Bge;

/// <summary>
/// The background model computed in this process, on whatever accelerator the
/// board has: a Rockchip NPU, a Qualcomm Hexagon, or any Vulkan GPU through
/// ncnn. About ninety milliseconds on an RK3588 and about thirty on a Hexagon,
/// for a model that runs once every N frames.
///
/// Notably absent: the GraXpert CLI. It is the right fallback for a file in
/// Studio and the wrong one inside a capture loop, where it costs seconds to
/// tens of seconds of a session per frame. When no accelerator can serve a
/// board, <see cref="CanRun"/> is false, the client path takes over if a browser
/// is there, and otherwise subs are stacked with their gradient and counted.
/// That is a better trade than quietly spending the night in Python.
/// </summary>
public sealed class HostBgeModelProducer : IBgeModelProducer {

    private readonly GraXpertService _graxpert;
    private readonly ILogger<HostBgeModelProducer>? _logger;

    public HostBgeModelProducer(GraXpertService graxpert,
                                ILogger<HostBgeModelProducer>? logger = null) {
        _graxpert = graxpert;
        _logger = logger;
    }

    public string Name => "host";

    /// <summary>The lane that last served a request, for the status line
    /// ("npu-rknn", "npu-qnn", "gpu-ncnn"), or null before the first one.</summary>
    public string? Lane { get; private set; }

    public bool CanRun => _graxpert.CanRunBgeTile();

    public Task<float[]?> RunAsync(float[] nhwcTensor, int tile, CancellationToken ct) {
        var result = _graxpert.RunBgeTile(nhwcTensor, tile);
        if (result == null) {
            _logger?.LogInformation(
                "No in-process accelerator served the background model; "
                + "the client path or an uncorrected sub is next");
            return Task.FromResult<float[]?>(null);
        }
        Lane = result.Value.Lane;
        _logger?.LogDebug("Background model on {Lane} ({Version}) in {Ms:F0} ms",
            result.Value.Lane, result.Value.Version, result.Value.ElapsedMs);
        return Task.FromResult<float[]?>(result.Value.Output);
    }
}
