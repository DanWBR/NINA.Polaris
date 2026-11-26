// N.I.N.A. Polaris
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors

using System.Net;
using NINA.Polaris.Services.Alpaca;
using NUnit.Framework;

namespace NINA.Polaris.Test;

[TestFixture]
public class AlpacaDiscoveryDedupTests {
    private static readonly HashSet<IPAddress> Own = new() {
        IPAddress.Parse("192.168.1.10"), // eth0
        IPAddress.Parse("10.0.0.5"),     // wlan0
    };

    private static AlpacaServer Server(string host, int port, params string[] ids) => new() {
        Host = host,
        Port = port,
        Devices = ids.Select(id => new AlpacaConfiguredDevice { UniqueID = id, DeviceType = "Switch" }).ToList(),
    };

    private static List<string> Hosts(IEnumerable<AlpacaServer> s) => s.Select(x => $"{x.Host}:{x.Port}").ToList();

    [Test]
    public void SameServerOnLoopbackEthAndWlan_CollapsesToLoopback() {
        var result = AlpacaDiscovery.DeduplicateLocalServers(new[] {
            Server("192.168.1.10", 32241, "a", "b"),
            Server("127.0.0.1", 32241, "b", "a"),
            Server("10.0.0.5", 32241, "a", "b"),
        }, Own);
        Assert.That(Hosts(result), Is.EqualTo(new[] { "127.0.0.1:32241" }));
    }

    [Test]
    public void InterfaceEntryWithoutDevices_IsDroppedWhenLoopbackHasDevices() {
        var result = AlpacaDiscovery.DeduplicateLocalServers(new[] {
            Server("127.0.0.1", 32241, "a"),
            Server("192.168.1.10", 32241),
        }, Own);
        Assert.That(Hosts(result), Is.EqualTo(new[] { "127.0.0.1:32241" }));
    }

    [Test]
    public void LoopbackWithoutDevices_KeepsInterfaceEntry() {
        // Server bound to the LAN IP only: answers the loopback probe but HTTP on 127.0.0.1 fails.
        var result = AlpacaDiscovery.DeduplicateLocalServers(new[] {
            Server("127.0.0.1", 32241),
            Server("192.168.1.10", 32241, "a"),
        }, Own);
        Assert.That(Hosts(result), Is.EquivalentTo(new[] { "127.0.0.1:32241", "192.168.1.10:32241" }));
    }

    [Test]
    public void DifferentPorts_AreDistinctServers() {
        var result = AlpacaDiscovery.DeduplicateLocalServers(new[] {
            Server("127.0.0.1", 32241, "a"),
            Server("192.168.1.10", 31426, "a"),
        }, Own);
        Assert.That(result, Has.Count.EqualTo(2));
    }

    [Test]
    public void DifferentDeviceIdsOnSamePort_AreKept() {
        var result = AlpacaDiscovery.DeduplicateLocalServers(new[] {
            Server("127.0.0.1", 11111, "a"),
            Server("192.168.1.10", 11111, "z"),
        }, Own);
        Assert.That(result, Has.Count.EqualTo(2));
    }

    [Test]
    public void RemoteHostsWithIdenticalIds_AreNeverMerged() {
        // Two SV241 units share compiled-in UniqueIDs; they must both stay listed.
        var result = AlpacaDiscovery.DeduplicateLocalServers(new[] {
            Server("127.0.0.1", 32241, "a"),
            Server("192.168.1.50", 32241, "a"),
            Server("192.168.1.51", 32241, "a"),
        }, Own);
        Assert.That(result, Has.Count.EqualTo(3));
    }
}
