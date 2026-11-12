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

using Microsoft.Extensions.DependencyInjection;

namespace NINA.Polaris.Services.Sequencer.Instructions;

/// <summary>
/// Power the host off. Meant as the last instruction of a sequence, after the
/// mount is parked and the camera warmed: nothing after it runs. The run only
/// reaches it when the instructions before it did not abort the run, so a
/// failed night leaves the host up to be looked at. A build where powering
/// off is refused (a development build) fails the instruction with the reason.
/// </summary>
public class ShutdownHostInstruction : SequenceInstruction {
    public override string Type => "ShutdownHost";

    public override Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        var power = ctx.Services?.GetService<PowerService>()
            ?? throw new InvalidOperationException("The power service is not available");
        var r = power.ScheduleShutdown();
        if (!r.Ok) throw new InvalidOperationException("The host was not powered off: " + r.Message);
        ctx.Logger.LogWarning("ShutdownHost: powering the host off");
        return Task.CompletedTask;
    }
}
