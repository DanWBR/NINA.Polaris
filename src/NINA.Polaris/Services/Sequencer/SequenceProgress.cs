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

using NINA.Polaris.Services.Sequencer.Instructions;

namespace NINA.Polaris.Services.Sequencer;

/// <summary>
/// The runtime state of a loaded sequence, flat and keyed by entity id, for
/// the ADV editor's live view (see the /api/sequencer/progress endpoint).
/// </summary>
public static class SequenceProgress {
    public sealed record EntityState(string Status, string? Error, DateTime? StartedAt, DateTime? FinishedAt,
                                     int? CompletedCount, int? Count);

    public sealed record ProgressSnapshot(string State, DateTime? StartedAt, DateTime? FinishedAt, string? LastError,
                                   string DocumentName, IReadOnlyDictionary<string, EntityState> Entities);

    public static ProgressSnapshot Snapshot(AdvancedSequenceEngine engine) {
        var map = new Dictionary<string, EntityState>();
        Collect(engine.Document.Root, map);
        return new ProgressSnapshot(engine.State.ToString(), engine.StartedAt, engine.FinishedAt, engine.LastError,
                             engine.Document.Name, map);
    }

    internal static void Collect(ISequenceEntity e, Dictionary<string, EntityState> map) {
        if (e is SequenceEntityBase b && !string.IsNullOrEmpty(b.Id)) {
            int? done = null, count = null;
            if (e is TakeExposureInstruction x) { done = x.CompletedCount; count = x.Count; }
            map[b.Id] = new EntityState(b.Status.ToString(), b.Error, b.StartedAt, b.FinishedAt, done, count);
        }
        if (e is SequenceContainer c) {
            foreach (var i in c.Items) Collect(i, map);
            foreach (var t in c.Triggers) Collect(t, map);
            foreach (var k in c.Conditions) Collect(k, map);
        }
    }
}
