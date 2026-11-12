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
using System.IO;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Polaris.Services;
using NINA.Polaris.Services.Auth;

namespace NINA.Polaris.Test;

/// <summary>
/// A host without a battery-backed RTC boots with a stale clock that is
/// corrected later (NTP, or Polaris from the client's time). Sessions must
/// age with the time that really passed, not with the size of that
/// correction.
/// </summary>
[TestFixture]
public class AuthClockJumpTests {

    private readonly List<string> _tempDirs = new();
    private DateTime _wall;
    private long _mono;

    [TearDown]
    public void Cleanup() {
        foreach (var d in _tempDirs) {
            try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
        }
        _tempDirs.Clear();
    }

    private string NewDir() {
        var dir = Path.Combine(Path.GetTempPath(),
            "polaris-authclock-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private AuthService Make(string dir) {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> {
                ["Profiles:Directory"] = dir
            })
            .Build();
        var profiles = new ProfileService(cfg, NullLogger<ProfileService>.Instance);
        return new AuthService(profiles, NullLogger<AuthService>.Instance,
            () => _wall, () => _mono);
    }

    private void Elapse(TimeSpan t) {
        _wall += t;
        _mono += (long)t.TotalMilliseconds;
    }

    private void SetClock(DateTime wall, TimeSpan monoElapsed) {
        _wall = wall;
        _mono += (long)monoElapsed.TotalMilliseconds;
    }

    private static void WaitForPersist(string dir, string token) {
        var path = Path.Combine(dir, "auth-sessions.json");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline) {
            try {
                if (File.Exists(path) && File.ReadAllText(path).Contains(token)) return;
            } catch (IOException) { }
            Thread.Sleep(20);
        }
        Assert.Fail("Session store was not written");
    }

    [Test]
    public void ForwardJump_LargerThanTtl_KeepsRememberedSession() {
        _wall = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        _mono = 1_000;
        var auth = Make(NewDir());
        var token = auth.SetInitialPassword("hunter2!", remember: true)!;

        // Clock corrected 200 days forward one minute later.
        SetClock(_wall + TimeSpan.FromDays(200), TimeSpan.FromMinutes(1));

        Assert.That(auth.ValidateToken(token), Is.True);
    }

    [Test]
    public void ForwardJump_KeepsShortSession() {
        _wall = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        _mono = 1_000;
        var dir = NewDir();
        var auth = Make(dir);
        auth.SetInitialPassword("hunter2!", remember: true);
        var token = auth.Login("hunter2!", null, remember: false)!;

        SetClock(_wall + TimeSpan.FromDays(3), TimeSpan.FromMinutes(1));

        Assert.That(auth.ValidateToken(token), Is.True,
            "A 24 h session must survive a 3-day clock correction");
    }

    [Test]
    public void RealElapsedTime_StillExpires() {
        _wall = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        _mono = 1_000;
        var auth = Make(NewDir());
        var token = auth.SetInitialPassword("hunter2!", remember: true)!;

        Elapse(TimeSpan.FromDays(31));

        Assert.That(auth.ValidateToken(token), Is.False);
    }

    [Test]
    public void RestartOnStaleClock_ThenCorrection_KeepsRememberedSession() {
        var real = new DateTime(2026, 11, 10, 22, 0, 0, DateTimeKind.Utc);
        var dir = NewDir();

        // Night 1: correct clock, remembered login, saved to disk.
        _wall = real;
        _mono = 1_000;
        var night1 = Make(dir);
        var token = night1.SetInitialPassword("hunter2!", remember: true)!;
        WaitForPersist(dir, token);
        night1.Dispose();

        // Night 2: the host boots six months behind and the app connects
        // right away, which stamps the session with the stale time.
        _wall = real - TimeSpan.FromDays(180);
        _mono = 5_000;
        var night2 = Make(dir);
        Assert.That(night2.ValidateToken(token), Is.True, "restored at boot");

        // A minute later the clock is put right (a day after night 1).
        SetClock(real + TimeSpan.FromDays(1), TimeSpan.FromMinutes(1));

        Assert.That(night2.ValidateToken(token), Is.True,
            "the clock correction must not expire the remembered session");
    }

    [Test]
    public void SmallDrift_IsNotTreatedAsJump() {
        _wall = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        _mono = 1_000;
        var dir = NewDir();
        var auth = Make(dir);
        auth.SetInitialPassword("hunter2!", remember: true);
        var token = auth.Login("hunter2!", null, remember: false)!;

        // 23 h of real time plus a 30 s NTP step: still inside 24 h.
        Elapse(TimeSpan.FromHours(23));
        _wall += TimeSpan.FromSeconds(30);
        Assert.That(auth.ValidateToken(token), Is.True);

        // Another 25 h idle: expired.
        Elapse(TimeSpan.FromHours(25));
        Assert.That(auth.ValidateToken(token), Is.False);
    }
}
