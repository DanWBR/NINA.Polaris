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

using System.Diagnostics;
using NINA.Ascom.Com;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Unit tests for the per-driver STA worker thread that funnels all
/// COM property/method calls. Windows-only; the [Platform] attribute
/// gates the whole fixture so CI on Linux just skips.
/// </summary>
[TestFixture]
[Platform("Win")]                                       // NUnit: skip off Windows
[System.Runtime.Versioning.SupportedOSPlatform("windows")]   // CA1416: same fact, for the compiler
public class AscomComStaDispatcherTests {

    [Test]
    public async Task ReadyAsync_ResolvesBeforeFirstWork() {
        using var disp = new AscomComStaDispatcher("test-ready");
        await disp.ReadyAsync().WaitAsync(TimeSpan.FromSeconds(1));
        // Subsequent work executes on the STA thread.
        var apt = await disp.Invoke(() => Thread.CurrentThread.GetApartmentState());
        Assert.That(apt, Is.EqualTo(ApartmentState.STA));
    }

    [Test]
    public async Task Invoke_ReturnsValueFromStaThread() {
        using var disp = new AscomComStaDispatcher("test-value");
        var v = await disp.Invoke(() => 42);
        Assert.That(v, Is.EqualTo(42));
    }

    [Test]
    public async Task Invoke_PropagatesExceptions() {
        using var disp = new AscomComStaDispatcher("test-throw");
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await disp.Invoke<int>(() => throw new InvalidOperationException("boom")));
        // Pump still alive after the exception, no thread-poison.
        var v = await disp.Invoke(() => 1);
        Assert.That(v, Is.EqualTo(1));
    }

    [Test]
    public async Task Invoke_SerialisesConcurrentCalls() {
        // 50 callers from the thread pool. The dispatcher must run
        // them strictly serially on its single STA thread (no
        // interleaving), otherwise an ASCOM driver behind it would
        // see overlapping calls and crash.
        using var disp = new AscomComStaDispatcher("test-serial");
        int counter = 0;
        int maxConcurrent = 0;
        int currentConcurrent = 0;
        var tasks = Enumerable.Range(0, 50).Select(_ => disp.Invoke(() => {
            Interlocked.Increment(ref currentConcurrent);
            var observed = Volatile.Read(ref currentConcurrent);
            if (observed > maxConcurrent) maxConcurrent = observed;
            Thread.Sleep(5);
            Interlocked.Decrement(ref currentConcurrent);
            return Interlocked.Increment(ref counter);
        })).ToArray();
        await Task.WhenAll(tasks);
        Assert.That(counter, Is.EqualTo(50));
        Assert.That(maxConcurrent, Is.EqualTo(1),
            "STA dispatcher must serialise all work on its single thread.");
    }

    [Test]
    public void Dispose_ShutsDownPumpAndRejectsNewWork() {
        var disp = new AscomComStaDispatcher("test-dispose");
        disp.Dispose();
        Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await disp.Invoke(() => 1));
    }
}