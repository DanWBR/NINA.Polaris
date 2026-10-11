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
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using NINA.Polaris.Services;
using NINA.Polaris.Services.Storage;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Finding a file server again after DHCP moves it, and the recent-servers
/// list. Field, 2026-10-10: each Starlink outage reset the router, the server
/// came back on another address, and the push stopped until the new IP was
/// typed in by hand.
/// </summary>
[TestFixture]
public class SmbServerMemoryTests {

    // ---- NetBIOS packets ----

    [Test]
    public void NamesAreEncodedTheRfc1002Way() {
        // "*" padded with zeros encodes as 'C','K' then thirty 'A's.
        var enc = NetBiosNames.EncodeName(new byte[] { (byte)'*', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        Assert.Multiple(() => {
            Assert.That(enc[0], Is.EqualTo(32));
            Assert.That(Encoding.ASCII.GetString(enc, 1, 32), Is.EqualTo("CK" + new string('A', 30)));
            Assert.That(enc[33], Is.EqualTo(0));
        });
    }

    [Test]
    public void ANameQueryAsksForTheFileServerService_ByBroadcast() {
        var q = NetBiosNames.BuildNameQuery(0x1234, "nas01");
        Assert.Multiple(() => {
            Assert.That(q[0] << 8 | q[1], Is.EqualTo(0x1234));
            Assert.That(q[2] << 8 | q[3], Is.EqualTo(0x0110), "recursion desired + broadcast");
            var raw = NetBiosNames.RawName("nas01", 0x20);
            Assert.That(Encoding.ASCII.GetString(raw, 0, 15), Is.EqualTo("NAS01          "), "upper case, space padded");
            Assert.That(raw[15], Is.EqualTo(0x20));
            Assert.That(q[^4] << 8 | q[^3], Is.EqualTo(0x0020), "NB question");
        });
    }

    private static byte[] Header(ushort id, ushort flags, int answers) => new byte[] {
        (byte)(id >> 8), (byte)id, (byte)(flags >> 8), (byte)flags, 0, 0, 0, (byte)answers, 0, 0, 0, 0 };

    private static byte[] Entry(string name, byte suffix, bool group) {
        var e = new byte[18];
        var n = Encoding.ASCII.GetBytes(name.PadRight(15));
        Buffer.BlockCopy(n, 0, e, 0, 15);
        e[15] = suffix;
        e[16] = (byte)(group ? 0x84 : 0x04);
        return e;
    }

    [Test]
    public void ANodeStatusAnswerGivesTheServersOwnName() {
        var entries = Entry("WORKGROUP", 0x00, group: true)
            .Concat(Entry("NAS01", 0x00, group: false))
            .Concat(Entry("NAS01", 0x20, group: false)).ToArray();
        var rdata = new byte[] { 3 }.Concat(entries).ToArray();
        var star = NetBiosNames.EncodeName(new byte[] { (byte)'*', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        var reply = Header(7, 0x8400, 1).Concat(star)
            .Concat(new byte[] { 0, 0x21, 0, 1, 0, 0, 0, 0, (byte)(rdata.Length >> 8), (byte)rdata.Length })
            .Concat(rdata).ToArray();
        Assert.That(NetBiosNames.ParseNodeStatusResponse(reply), Is.EqualTo("NAS01"));
    }

    [Test]
    public void ANameQueryAnswerGivesTheAddress() {
        var name = NetBiosNames.EncodeName(NetBiosNames.RawName("NAS01", 0x20));
        var reply = Header(9, 0x8500, 1).Concat(name)
            .Concat(new byte[] { 0, 0x20, 0, 1, 0, 0, 0x0e, 0x10, 0, 6, 0, 0, 192, 168, 3, 42 }).ToArray();
        Assert.That(NetBiosNames.ParseNameQueryResponse(reply), Is.EqualTo(IPAddress.Parse("192.168.3.42")));
    }

    [Test]
    public void ANegativeAnswerGivesNoAddress() {
        var name = NetBiosNames.EncodeName(NetBiosNames.RawName("NAS01", 0x20));
        var reply = Header(9, 0x8503, 0).Concat(name).ToArray();   // rcode 3: no such name
        Assert.That(NetBiosNames.ParseNameQueryResponse(reply), Is.Null);
    }

    [Test]
    public void ATruncatedAnswerIsIgnored() {
        Assert.Multiple(() => {
            Assert.That(NetBiosNames.ParseNodeStatusResponse(Header(1, 0x8400, 1)), Is.Null);
            Assert.That(NetBiosNames.ParseNameQueryResponse(Header(1, 0x8500, 1)), Is.Null);
        });
    }

    // ---- names ----

    [Test]
    public void ALearnedNameIsLookedUpAsItselfThenByMdns() {
        Assert.Multiple(() => {
            Assert.That(SmbServerMemory.DnsCandidates("NAS01"), Is.EqualTo(new[] { "NAS01", "NAS01.local" }));
            Assert.That(SmbServerMemory.DnsCandidates("kraken.lan"), Is.EqualTo(new[] { "kraken.lan", "kraken.local" }));
            Assert.That(SmbServerMemory.DnsCandidates("nas.local"), Is.EqualTo(new[] { "nas.local" }));
            Assert.That(SmbServerMemory.ShortName("kraken.lan"), Is.EqualTo("kraken"));
        });
    }

    [Test]
    public void OnlyARealReverseNameIsKept() {
        Assert.Multiple(() => {
            Assert.That(SmbServerMemory.UsableReverseName("kraken.lan."), Is.EqualTo("kraken.lan"));
            Assert.That(SmbServerMemory.UsableReverseName("192.168.3.42"), Is.Null);
            Assert.That(SmbServerMemory.UsableReverseName("42.3.168.192.in-addr.arpa"), Is.Null);
            Assert.That(SmbServerMemory.UsableReverseName(""), Is.Null);
        });
    }

    // ---- recents ----

    private static StorageRecentServer S(string host, string server = "", string share = "astro", string user = "dan")
        => new() { Kind = "smb", Host = host, Share = share, Username = user, ServerName = server };

    [Test]
    public void TheSameServerAtANewAddressUpdatesItsEntry() {
        var list = new List<StorageRecentServer> { S("192.168.3.10", "NAS01"), S("10.0.0.5", "OFFICE") };
        var after = SmbServerMemory.Upsert(list, S("192.168.3.42", "NAS01"), SmbServerMemory.MaxRecent);
        Assert.Multiple(() => {
            Assert.That(after.Count, Is.EqualTo(2), "not a second NAS01 entry");
            Assert.That(after[0].Host, Is.EqualTo("192.168.3.42"));
            Assert.That(after[1].ServerName, Is.EqualTo("OFFICE"));
        });
    }

    [Test]
    public void AnotherShareOnTheSameServerIsItsOwnEntry() {
        var list = new List<StorageRecentServer> { S("192.168.3.10", "NAS01", share: "astro") };
        var after = SmbServerMemory.Upsert(list, S("192.168.3.10", "NAS01", share: "backup"), SmbServerMemory.MaxRecent);
        Assert.That(after.Count, Is.EqualTo(2));
    }

    [Test]
    public void TheListIsCapped_NewestFirst() {
        var list = new List<StorageRecentServer>();
        for (int i = 0; i < 10; i++)
            list = SmbServerMemory.Upsert(list, S($"10.0.0.{i}"), SmbServerMemory.MaxRecent);
        Assert.Multiple(() => {
            Assert.That(list.Count, Is.EqualTo(SmbServerMemory.MaxRecent));
            Assert.That(list[0].Host, Is.EqualTo("10.0.0.9"));
        });
    }

    [Test]
    public void UsingARecentServerCopiesItsPasswordOnTheHost() {
        var p = new UserProfile { StorageRecent = new() { new StorageRecentServer {
            Kind = "smb", Host = "192.168.3.42", Share = "astro", Username = "dan", Password = "s3cret", ServerName = "NAS01" } } };
        Assert.That(SmbServerMemory.Use(p, 0), Is.True);
        Assert.Multiple(() => {
            Assert.That(p.StorageHost, Is.EqualTo("192.168.3.42"));
            Assert.That(p.StoragePassword, Is.EqualTo("s3cret"));
            Assert.That(p.StorageServerName, Is.EqualTo("NAS01"));
            Assert.That(SmbServerMemory.Use(p, 5), Is.False);
        });
    }

    // ---- wiring ----

    private static string Here([CallerFilePath] string p = "") => p;

    private static string Src(params string[] parts) {
        var all = new List<string> { Path.GetDirectoryName(Here())!, "..", "..", "src" };
        all.AddRange(parts);
        return File.ReadAllText(Path.GetFullPath(Path.Combine(all.ToArray())));
    }

    [Test]
    public void PointingThePushElsewhereForgetsTheOldServersName() {
        var ep = Src("NINA.Polaris", "Endpoints", "StorageEndpoints.cs");
        Assert.That(ep, Does.Contain("p.StorageServerName = \"\";"),
            "a stale name could otherwise lead a failed connect back to the old server");
    }

    [Test]
    public void TheRecentsListNeverCarriesAPassword() {
        var ep = Src("NINA.Polaris", "Endpoints", "StorageEndpoints.cs");
        var at = ep.IndexOf("recent         = (p.StorageRecent", StringComparison.Ordinal);
        Assert.That(at, Is.GreaterThanOrEqualTo(0));
        var block = ep.Substring(at, 400);
        Assert.Multiple(() => {
            Assert.That(block, Does.Contain("hasPassword = !string.IsNullOrEmpty(r.Password)"));
            Assert.That(block, Does.Not.Contain("password = r.Password"));
        });
    }

    [Test]
    public void AFailedConnectLooksTheServerUpByName() {
        var push = Src("NINA.Polaris", "Services", "StoragePushService.cs");
        Assert.Multiple(() => {
            Assert.That(push, Does.Contain("var moved = await _svc.RelocateSmbAsync(cfg, ct);"));
            Assert.That(push, Does.Contain("_svc.CommitRelocation(cfg, moved);"));
        });
    }
}
