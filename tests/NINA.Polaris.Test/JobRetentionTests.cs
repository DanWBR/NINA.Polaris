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

using System.Collections.Concurrent;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Eight services keep a job dictionary that only ever gained entries. The trim
/// has to bound that WITHOUT ever dropping work someone is still waiting on,
/// which is the property worth pinning: the eviction runs on the hot path, right
/// after a job is inserted.
/// </summary>
[TestFixture]
public class JobRetentionTests {

    private sealed class FakeJob {
        public DateTime StartedAt { get; init; }
        public bool Finished { get; set; }
    }

    private static ConcurrentDictionary<string, FakeJob> Fill(int finished, int running) {
        var jobs = new ConcurrentDictionary<string, FakeJob>();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < finished; i++)
            jobs[$"done-{i:D4}"] = new FakeJob { StartedAt = t0.AddSeconds(i), Finished = true };
        for (int i = 0; i < running; i++)
            jobs[$"live-{i:D4}"] = new FakeJob { StartedAt = t0.AddSeconds(i), Finished = false };
        return jobs;
    }

    private static void Trim(ConcurrentDictionary<string, FakeJob> jobs, int max) =>
        JobRetention.TrimFinished(jobs, j => j.StartedAt, j => j.Finished, max);

    [Test]
    public void UnderTheCapNothingIsDropped() {
        var jobs = Fill(finished: 5, running: 0);
        Trim(jobs, 10);
        Assert.That(jobs, Has.Count.EqualTo(5));
    }

    [Test]
    public void OverTheCapTheOldestFinishedJobsGoFirst() {
        var jobs = Fill(finished: 30, running: 0);
        Trim(jobs, 10);

        Assert.That(jobs, Has.Count.EqualTo(10), "deveria cair exatamente ate o teto");
        Assert.That(jobs.Keys, Does.Contain("done-0029"), "o mais recente tem de sobreviver");
        Assert.That(jobs.Keys, Does.Not.Contain("done-0000"), "o mais antigo tem de sair");
    }

    /// <summary>The rule that makes this safe to call on the hot path.</summary>
    [Test]
    public void RunningJobsAreNeverEvictedEvenFarOverTheCap() {
        var jobs = Fill(finished: 0, running: 40);
        Trim(jobs, 10);

        Assert.That(jobs, Has.Count.EqualTo(40),
            "um job em andamento nunca pode ser descartado: alguem esta esperando o resultado");
    }

    [Test]
    public void RunningJobsSurviveWhileFinishedOnesAreTrimmedAroundThem() {
        var jobs = Fill(finished: 30, running: 5);
        Trim(jobs, 10);

        Assert.That(jobs.Values.Count(j => !j.Finished), Is.EqualTo(5),
            "os 5 em andamento continuam la");
        Assert.That(jobs, Has.Count.EqualTo(10));
    }

    /// <summary>A job holding a buffer or a process handle gets released, not
    /// just unreferenced.</summary>
    [Test]
    public void EvictedJobsAreDisposed() {
        var disposed = 0;
        var jobs = new ConcurrentDictionary<string, DisposableJob>();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 20; i++)
            jobs[$"j-{i:D4}"] = new DisposableJob(() => Interlocked.Increment(ref disposed))
                { StartedAt = t0.AddSeconds(i) };

        JobRetention.TrimFinished(jobs, j => j.StartedAt, _ => true, maxRetained: 5);

        Assert.That(jobs, Has.Count.EqualTo(5));
        Assert.That(disposed, Is.EqualTo(15));
    }

    private sealed class DisposableJob(Action onDispose) : IDisposable {
        public DateTime StartedAt { get; init; }
        public void Dispose() => onDispose();
    }
}
