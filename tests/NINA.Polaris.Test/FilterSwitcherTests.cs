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

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Image.Interfaces;
using NINA.Polaris.Services;

namespace NINA.Polaris.Test;

/// <summary>
/// Pins the FilterSwitcher contract: it moves the wheel only when the filter
/// changes, and applies the focuser offset as a DELTA from the previously
/// applied filter (so repeated/round-trip switches don't accumulate).
/// </summary>
[TestFixture]
public class FilterSwitcherTests {

    private static readonly Dictionary<string, int> Offsets = new() {
        ["L"] = 0, ["R"] = 30, ["G"] = 35, ["B"] = 50, ["Ha"] = -120
    };

    [Test]
    public async Task First_switch_moves_wheel_and_applies_absolute_offset() {
        var fw = new FakeWheel(); var foc = new FakeFocuser();
        var st = new FilterState();
        await FilterSwitcher.ApplyAsync(fw, foc, Offsets, "R", st, NullLogger.Instance, CancellationToken.None);

        Assert.That(fw.LastFilter, Is.EqualTo("R"));
        Assert.That(foc.TotalMoved, Is.EqualTo(30));   // 30 - 0
        Assert.That(st.CurrentFilter, Is.EqualTo("R"));
        Assert.That(st.AppliedOffset, Is.EqualTo(30));
    }

    [Test]
    public async Task Subsequent_switches_apply_delta_not_absolute() {
        var fw = new FakeWheel(); var foc = new FakeFocuser();
        var st = new FilterState();
        await FilterSwitcher.ApplyAsync(fw, foc, Offsets, "R", st, NullLogger.Instance, CancellationToken.None); // +30
        await FilterSwitcher.ApplyAsync(fw, foc, Offsets, "B", st, NullLogger.Instance, CancellationToken.None); // +20 (50-30)
        await FilterSwitcher.ApplyAsync(fw, foc, Offsets, "Ha", st, NullLogger.Instance, CancellationToken.None); // -170 (-120-50)

        // Net focuser position should equal the absolute offset of the last filter.
        Assert.That(foc.Position, Is.EqualTo(-120));
        Assert.That(st.AppliedOffset, Is.EqualTo(-120));
        Assert.That(fw.MoveCount, Is.EqualTo(3));
    }

    [Test]
    public async Task Same_filter_again_is_a_noop() {
        var fw = new FakeWheel(); var foc = new FakeFocuser();
        var st = new FilterState();
        await FilterSwitcher.ApplyAsync(fw, foc, Offsets, "G", st, NullLogger.Instance, CancellationToken.None);
        await FilterSwitcher.ApplyAsync(fw, foc, Offsets, "G", st, NullLogger.Instance, CancellationToken.None);

        Assert.That(fw.MoveCount, Is.EqualTo(1));       // didn't move the wheel twice
        Assert.That(foc.MoveCount, Is.EqualTo(1));      // didn't re-apply the offset
        Assert.That(foc.Position, Is.EqualTo(35));
    }

    [Test]
    public async Task Round_trip_returns_focuser_to_origin() {
        var fw = new FakeWheel(); var foc = new FakeFocuser();
        var st = new FilterState();
        await FilterSwitcher.ApplyAsync(fw, foc, Offsets, "B", st, NullLogger.Instance, CancellationToken.None); // +50
        await FilterSwitcher.ApplyAsync(fw, foc, Offsets, "L", st, NullLogger.Instance, CancellationToken.None); // -50 (0-50)
        Assert.That(foc.Position, Is.EqualTo(0));
    }

    [Test]
    public async Task No_filter_requested_is_a_noop() {
        var fw = new FakeWheel(); var foc = new FakeFocuser();
        var st = new FilterState();
        await FilterSwitcher.ApplyAsync(fw, foc, Offsets, null, st, NullLogger.Instance, CancellationToken.None);
        await FilterSwitcher.ApplyAsync(fw, foc, Offsets, "", st, NullLogger.Instance, CancellationToken.None);
        Assert.That(fw.MoveCount, Is.EqualTo(0));
        Assert.That(foc.MoveCount, Is.EqualTo(0));
    }

    [Test]
    public async Task Unknown_filter_offset_treated_as_zero() {
        var fw = new FakeWheel(); var foc = new FakeFocuser();
        var st = new FilterState();
        await FilterSwitcher.ApplyAsync(fw, foc, Offsets, "R", st, NullLogger.Instance, CancellationToken.None); // +30
        await FilterSwitcher.ApplyAsync(fw, foc, Offsets, "SII", st, NullLogger.Instance, CancellationToken.None); // 0 → -30
        Assert.That(foc.Position, Is.EqualTo(0));
        Assert.That(st.AppliedOffset, Is.EqualTo(0));
        Assert.That(fw.LastFilter, Is.EqualTo("SII"));   // wheel still moved
    }

    // ── minimal fakes ────────────────────────────────────────────────
    private sealed class FakeWheel : IFilterWheel {
        public string? LastFilter; public int MoveCount;
        public string DeviceName => "fake-fw";
        public bool IsConnected => true;
        public int Position { get; private set; }
        public bool IsMoving => false;
        public string[] FilterNames => new[] { "L", "R", "G", "B", "Ha", "SII" };
        public int FilterCount => FilterNames.Length;
        public string CurrentFilterName => LastFilter ?? "";
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SetPositionAsync(int position, CancellationToken ct = default) { Position = position; MoveCount++; return Task.CompletedTask; }
        public Task SetFilterByNameAsync(string filterName, CancellationToken ct = default) { LastFilter = filterName; MoveCount++; return Task.CompletedTask; }
    }

    private sealed class FakeFocuser : IFocuser {
        public int TotalMoved; public int MoveCount;
        public string DeviceName => "fake-foc";
        public bool IsConnected => true;
        public int Position { get; private set; }
        public int MaxPosition => 100000;
        public double Temperature => double.NaN;
        public bool IsMoving => false;
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task MoveAbsoluteAsync(int position, CancellationToken ct = default) { Position = position; MoveCount++; return Task.CompletedTask; }
        public Task MoveRelativeAsync(int steps, CancellationToken ct = default) { Position += steps; TotalMoved += steps; MoveCount++; return Task.CompletedTask; }
        public Task AbortAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
