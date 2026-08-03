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

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Pins the top-level blocks of the /ws/status frame.
///
/// The client reads ~388 key paths out of this one message, and a block that
/// stops being emitted does not fail anything: the socket still connects, the
/// frame is still valid JSON, and one UI panel is quietly blank until somebody
/// reports it weeks later.
///
/// Two halves guard that, and neither is redundant:
///   - at runtime, StatusPayloadBuilder rejects a contributor that declares a
///     block and does not write it, and refuses to start if two contributors
///     claim the same key;
///   - here, the union of what the contributors declare is compared against a
///     capture taken from a live socket, so a block cannot be dropped by
///     deleting its contributor outright, which the runtime check cannot see.
///
/// This reads the sources rather than instantiating anything: the contributors
/// take 42 services between them, and reproducing the app's ~200 service
/// registrations in a fixture would be more fragile than what it guards.
///
/// If you retake status-contract.txt: give the process a minute first. Several
/// blocks are filled by background services and are simply absent from the first
/// ticks (host.device is populated by HostMetricsService and cost an afternoon
/// once, read as a regression when it was really a cold server).
/// </summary>
[TestFixture]
public class StatusContractTests {

    /// Written by StatusPayloadBuilder itself, not by any contributor.
    private static readonly string[] Envelope = { "type", "timestamp" };

    private static string ContributorDir([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..",
                                      "src", "NINA.Polaris", "WebSocket", "Status"));

    /// <summary>Every key declared by an IStatusContributor, by contributor.</summary>
    private static Dictionary<string, string[]> DeclaredKeys() {
        var dir = ContributorDir();
        Assert.That(Directory.Exists(dir), $"nao achei {dir}");

        var byType = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(dir, "*StatusContributor.cs")) {
            var src = File.ReadAllText(file);
            var m = Regex.Match(src, @"Keys\s*\{\s*get;\s*\}\s*=\s*new\[\]\s*\{(?<keys>[^}]*)\}");
            if (!m.Success) continue;   // the interface file has no Keys of its own
            byType[Path.GetFileNameWithoutExtension(file)] =
                Regex.Matches(m.Groups["keys"].Value, "\"([^\"]+)\"")
                     .Select(x => x.Groups[1].Value).ToArray();
        }
        Assert.That(byType, Is.Not.Empty, "nenhum contribuidor declarou chaves");
        return byType;
    }

    [Test]
    public void ContributorsCoverEveryBlockTheClientReads() {
        var declared = DeclaredKeys().Values.SelectMany(x => x).Concat(Envelope).ToList();

        var contract = Path.Combine(Path.GetDirectoryName(Here())!, "status-contract.txt");
        Assert.That(File.Exists(contract), $"nao achei {contract}");
        var expected = File.ReadAllLines(contract)
            .Where(l => l.Length > 0 && !l.Contains('.') && !l.Contains('['))
            .ToList();

        Assert.That(declared, Is.EquivalentTo(expected),
            "Os blocos declarados pelos contribuidores divergiram da captura do socket. "
            + "Se a mudanca foi intencional, atualize status-contract.txt na mesma "
            + "alteracao, para o proximo leitor saber o que o cliente ainda espera.");
    }

    [Test]
    public void NoTwoContributorsClaimTheSameBlock() {
        // StatusPayloadBuilder throws on this at startup, which is the real
        // guard. Here it fails on a laptop instead of on the operator's mount.
        var dupes = DeclaredKeys()
            .SelectMany(kv => kv.Value.Select(k => (Key: k, Owner: kv.Key)))
            .GroupBy(x => x.Key, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} ({string.Join(", ", g.Select(x => x.Owner))})")
            .ToList();

        Assert.That(dupes, Is.Empty, "Blocos reivindicados por mais de um contribuidor: "
            + string.Join("; ", dupes));
    }

    private static string Here([CallerFilePath] string here = "") => here;
}
