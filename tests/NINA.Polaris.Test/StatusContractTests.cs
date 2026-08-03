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
/// Pins the top-level blocks of the /ws/status payload.
///
/// The client reads ~388 key paths out of this one message, and a block that
/// stops being emitted does not fail anything: the socket still connects, the
/// UI just quietly shows nothing for that subsystem, and it surfaces as a field
/// report weeks later. That risk is about to go up, because the payload is
/// being split into per-subsystem contributors and a split is exactly where a
/// block goes missing.
///
/// What this checks is the SOURCE, not a live payload. Building the real thing
/// needs 42 services out of the app's DI graph, and reproducing ~200 service
/// registrations in a fixture would be more fragile than the code it guards.
/// The source check catches the failure that actually happens (a block dropped
/// while moving code) at the cost of not catching one that does not (a block
/// still written but nested in the wrong place).
///
/// The companion file status-contract.txt holds the full 388-path capture taken
/// from a running server, as the reference for a manual before/after diff when
/// the payload is restructured.
///
/// If you take that capture: give the process a minute first. Several blocks are
/// filled by background services and are simply absent from the first ticks
/// (host.device is populated by HostMetricsService and cost an afternoon once,
/// read as a regression when it was a cold server).
/// </summary>
[TestFixture]
public class StatusContractTests {

    /// Every top-level key of the status frame, captured from a live socket.
    private static readonly string[] Blocks = {
        "type", "timestamp", "equipment", "auxCapture", "liveStack", "guider",
        "autoFocus", "meridianFlip", "plan", "advSeq", "sequence", "cameraStream",
        "keepCentered", "videoRecording", "videoStack", "flatWizard", "slewPreview",
        "host", "sirilJobs", "graXpertJobs", "server", "notifications", "simulator",
        "network", "storagePush", "usbDrive", "usbRemoved", "benchmark",
        "sensorAnalysis", "polarAlignment", "debugLog", "plateSolve", "capture",
        "decon", "liveCapture", "cooling"
    };

    private static string PayloadSource([CallerFilePath] string here = "") {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
        var path = Path.Combine(root, "src", "NINA.Polaris", "WebSocket", "StatusPayloadBuilder.cs");
        Assert.That(File.Exists(path), $"nao achei {path}");
        return File.ReadAllText(path);
    }

    [Test]
    public void EveryStatusBlockIsStillEmitted() {
        var src = PayloadSource();
        var missing = Blocks
            .Where(b => !Regex.IsMatch(src, $@"^\s*{Regex.Escape(b)}\s*=", RegexOptions.Multiline))
            .ToList();

        Assert.That(missing, Is.Empty,
            "Estes blocos sumiram do payload de /ws/status: " + string.Join(", ", missing)
            + ". Se a remocao foi intencional, tire-os desta lista E de status-contract.txt "
            + "na mesma mudanca, para o proximo leitor saber que o cliente nao os espera mais.");
    }

    [Test]
    public void TheCapturedContractCoversTheSameBlocks() {
        // Guards the pair: the golden capture and the list above must not drift
        // apart, or a future reader trusts a file that stopped being true.
        var contract = Path.Combine(CallerDir(), "status-contract.txt");
        Assert.That(File.Exists(contract), $"nao achei {contract}");

        var topLevel = File.ReadAllLines(contract)
            .Where(l => l.Length > 0 && !l.Contains('.') && !l.Contains('['))
            .ToHashSet();

        Assert.That(topLevel, Is.EquivalentTo(Blocks),
            "status-contract.txt e a lista Blocks divergiram.");
    }

    private static string CallerDir([CallerFilePath] string here = "") =>
        Path.GetDirectoryName(here)!;
}
