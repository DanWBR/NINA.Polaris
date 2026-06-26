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

namespace NINA.Polaris.Services.Sequencer.Containers;

/// <summary>
/// The default container: runs children in array order, each child finishing
/// before the next starts. Honours triggers between every step and supports
/// <see cref="SequenceContainer.IsLoop"/> + conditions for "do block until X".
/// </summary>
public class SequentialContainer : SequenceContainer {
    public override string Type => "Sequential";

    public override Task ExecuteAsync(SequenceContext ctx, CancellationToken ct)
        // Per-step retry/error-policy, parent→child trigger cascade, IsLoop +
        // conditions all live in the shared base helper.
        => RunChildrenSequentialAsync(ctx, ct);
}