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

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using NINA.Polaris.Services.Storage;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>A server given by name survives one lost mDNS answer: the lookup
/// is retried, then the address it was last reached at is used if recent.</summary>
[TestFixture, NonParallelizable]
public class SmbNameResolutionTests {
    private static readonly IPAddress Jupiter = IPAddress.Parse("192.168.3.189");
    private static readonly DateTime Now = new(2026, 11, 14, 23, 30, 0, DateTimeKind.Utc);

    private Func<string, IPAddress[]> _lookup = null!;
    private Action<TimeSpan> _pause = null!;
    private Func<DateTime> _clock = null!;
    private readonly List<string> _asked = new();
    private int _pauses;

    [SetUp]
    public void SetUp() {
        _lookup = SmbStorageTarget.Lookup; _pause = SmbStorageTarget.Pause; _clock = SmbStorageTarget.UtcNow;
        SmbStorageTarget.Pause = _ => _pauses++;
        SmbStorageTarget.UtcNow = () => Now;
        SmbStorageTarget.ForgetAddresses();
        _asked.Clear(); _pauses = 0;
    }

    [TearDown]
    public void TearDown() {
        SmbStorageTarget.Lookup = _lookup; SmbStorageTarget.Pause = _pause; SmbStorageTarget.UtcNow = _clock;
        SmbStorageTarget.ForgetAddresses();
    }

    private void Answers(Func<string, int, IPAddress[]?> answer) {
        int call = 0;
        SmbStorageTarget.Lookup = name => {
            _asked.Add(name);
            return answer(name, call++) ?? throw new SocketException((int)SocketError.HostNotFound);
        };
    }

    [Test]
    public void AnIpAddressIsUsedAsIs() {
        Answers((_, _) => null);
        Assert.That(SmbStorageTarget.ResolveIPv4("192.168.3.197"), Is.EqualTo(IPAddress.Parse("192.168.3.197")));
        Assert.That(_asked, Is.Empty);
    }

    [Test]
    public void ABareNameFallsBackToDotLocal() {
        Answers((name, _) => name == "jupiter.local" ? new[] { Jupiter } : null);
        Assert.That(SmbStorageTarget.ResolveIPv4("jupiter"), Is.EqualTo(Jupiter));
        Assert.That(_pauses, Is.EqualTo(0));
    }

    [Test]
    public void OneLostAnswerIsRetried() {
        // The first pass (jupiter, jupiter.local) gets nothing; the retry does.
        Answers((name, call) => call >= 2 && name == "jupiter.local" ? new[] { Jupiter } : null);
        Assert.That(SmbStorageTarget.ResolveIPv4("jupiter"), Is.EqualTo(Jupiter));
        Assert.That(_pauses, Is.EqualTo(1));
    }

    [Test]
    public void TheLastAddressIsUsedWhenTheNameIsGone() {
        SmbStorageTarget.RememberAddress("jupiter", Jupiter, Now.AddMinutes(-5));
        Answers((_, _) => null);
        Assert.That(SmbStorageTarget.ResolveIPv4("JUPITER"), Is.EqualTo(Jupiter));
    }

    [Test]
    public void AnOldLastAddressIsNotUsed() {
        SmbStorageTarget.RememberAddress("jupiter", Jupiter,
            Now - SmbStorageTarget.LastAddressValidFor - TimeSpan.FromMinutes(1));
        Answers((_, _) => null);
        Assert.Throws<IOException>(() => SmbStorageTarget.ResolveIPv4("jupiter"));
    }

    [Test]
    public void ANameThatNeverWorkedStillFails() {
        Answers((_, _) => null);
        var ex = Assert.Throws<IOException>(() => SmbStorageTarget.ResolveIPv4("jupiter"));
        Assert.That(ex!.Message, Does.Contain("Could not resolve host 'jupiter'"));
    }

    [Test]
    public void AnAddressGivenAsIpIsNotRememberedAsAName() {
        SmbStorageTarget.RememberAddress("192.168.3.189", Jupiter, Now);
        Answers((_, _) => null);
        Assert.Throws<IOException>(() => SmbStorageTarget.ResolveIPv4("192-168-3-189"));
    }
}
