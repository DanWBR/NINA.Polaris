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

using System.Net;
using System.Net.Sockets;

namespace NINA.Polaris.Services.Storage;

/// <summary>
/// What the host remembers about the file servers it has pushed to: the
/// server's name behind an address the operator typed as an IP, so it can be
/// found again when DHCP moves it, and a short list of recent servers to pick
/// from instead of retyping them.
///
/// <para>Field report, 2026-10-10: every time the Starlink behind the router
/// dropped, the router reset, the server came back on another address, and
/// the push to it stopped until the new IP was found and typed in.</para>
/// </summary>
public static class SmbServerMemory {
    public const int MaxRecent = 6;
    private static readonly object Gate = new();

    /// <summary>The address <paramref name="serverName"/> answers at now: the
    /// router's DNS (most register their DHCP clients), then mDNS, then a
    /// NetBIOS broadcast. Only the machine that answers to the name is ever
    /// contacted, so credentials never go to some other host on the network.</summary>
    public static async Task<string?> FindByNameAsync(string serverName, CancellationToken ct) {
        if (string.IsNullOrWhiteSpace(serverName)) return null;
        foreach (var candidate in DnsCandidates(serverName)) {
            var ip = await DnsV4Async(candidate, TimeSpan.FromSeconds(3), ct);
            if (ip != null) return ip.ToString();
        }
        var nb = await NetBiosNames.ResolveAsync(ShortName(serverName), TimeSpan.FromSeconds(2), ct);
        return nb?.ToString();
    }

    /// <summary>The name of a server given by IP, or null when the host is
    /// already a name (it survives an address change by itself) or nothing
    /// says. Asked of the server over NetBIOS first; Windows 11 often has that
    /// switched off, so then of the router's DNS, which most routers fill in
    /// for their DHCP clients.</summary>
    public static async Task<string?> LearnNameAsync(string host, CancellationToken ct) {
        if (!IPAddress.TryParse((host ?? "").Trim(), out var ip)) return null;
        var nb = await NetBiosNames.GetNameAsync(ip, TimeSpan.FromSeconds(2), ct);
        if (!string.IsNullOrWhiteSpace(nb)) return nb;
        try {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            var entry = await Dns.GetHostEntryAsync(ip.ToString(), cts.Token);
            return UsableReverseName(entry.HostName);
        } catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException) {
            return null;
        }
    }

    /// <summary>A reverse-DNS answer worth keeping as a name: not an address,
    /// not an in-addr.arpa echo.</summary>
    internal static string? UsableReverseName(string? hostName) {
        var h = (hostName ?? "").Trim().TrimEnd('.');
        if (h.Length == 0 || IPAddress.TryParse(h, out _)) return null;
        if (h.EndsWith(".in-addr.arpa", StringComparison.OrdinalIgnoreCase)) return null;
        return h;
    }

    /// <summary>The first label of a name: what NetBIOS and mDNS know a
    /// machine as ("kraken" for "kraken.lan").</summary>
    internal static string ShortName(string name) => name.Trim().Split('.')[0];

    /// <summary>Names to ask DNS for: the name as learned (the router's DNS),
    /// then its mDNS form.</summary>
    internal static IReadOnlyList<string> DnsCandidates(string name) {
        var n = name.Trim().TrimEnd('.');
        var mdns = ShortName(n) + ".local";
        return string.Equals(n, mdns, StringComparison.OrdinalIgnoreCase) ? new[] { n } : new[] { n, mdns };
    }

    /// <summary>Put the profile's current server at the top of the recents.</summary>
    public static void Remember(UserProfile p) {
        var entry = new StorageRecentServer {
            Kind = p.StorageKind ?? "smb", Host = p.StorageHost ?? "", Port = p.StoragePort,
            Share = p.StorageShare ?? "", BasePath = p.StorageBasePath ?? "", Domain = p.StorageDomain ?? "",
            Username = p.StorageUsername ?? "", Password = p.StoragePassword ?? "",
            ServerName = p.StorageServerName ?? "", LastUsedUtc = DateTime.UtcNow,
        };
        lock (Gate) p.StorageRecent = Upsert(p.StorageRecent ?? new(), entry, MaxRecent);
    }

    /// <summary>Copy recent entry <paramref name="index"/> into the active
    /// configuration. False when there is no such entry.</summary>
    public static bool Use(UserProfile p, int index) {
        lock (Gate) {
            var list = p.StorageRecent ?? new();
            if (index < 0 || index >= list.Count) return false;
            var e = list[index];
            p.StorageKind = e.Kind; p.StorageHost = e.Host; p.StoragePort = e.Port;
            p.StorageShare = e.Share; p.StorageBasePath = e.BasePath; p.StorageDomain = e.Domain;
            p.StorageUsername = e.Username; p.StoragePassword = e.Password;
            p.StorageServerName = e.ServerName;
            return true;
        }
    }

    public static bool Forget(UserProfile p, int index) {
        lock (Gate) {
            var list = p.StorageRecent ?? new();
            if (index < 0 || index >= list.Count) return false;
            list.RemoveAt(index);
            p.StorageRecent = list;
            return true;
        }
    }

    /// <summary>Insert or move <paramref name="entry"/> to the front, replacing
    /// an older entry for the same server, and cap the list.</summary>
    internal static List<StorageRecentServer> Upsert(List<StorageRecentServer> list, StorageRecentServer entry, int max) {
        var result = new List<StorageRecentServer> { entry };
        result.AddRange(list.Where(e => !SameServer(e, entry)));
        return result.Take(max).ToList();
    }

    /// <summary>Same kind, share and user, and the same server: by its learned
    /// name when both entries know it (so a new address updates the entry
    /// instead of adding another), else by host.</summary>
    internal static bool SameServer(StorageRecentServer a, StorageRecentServer b) {
        bool Eq(string? x, string? y) => string.Equals((x ?? "").Trim(), (y ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
        if (!Eq(a.Kind, b.Kind) || !Eq(a.Share, b.Share) || !Eq(a.Username, b.Username) || !Eq(a.BasePath, b.BasePath))
            return false;
        if (!string.IsNullOrWhiteSpace(a.ServerName) && !string.IsNullOrWhiteSpace(b.ServerName))
            return Eq(a.ServerName, b.ServerName);
        return Eq(a.Host, b.Host);
    }

    private static async Task<IPAddress?> DnsV4Async(string name, TimeSpan timeout, CancellationToken ct) {
        try {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var addrs = await Dns.GetHostAddressesAsync(name, cts.Token);
            return addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        } catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException) {
            return null;
        }
    }
}
