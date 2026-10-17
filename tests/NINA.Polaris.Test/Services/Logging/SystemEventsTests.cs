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

using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Polaris.Services.Logging;

namespace NINA.Polaris.Test.Services.Logging;

/// <summary>System events in Polaris's log, and the diagnostics zip. The
/// lines are the kind field reports needed someone to fetch over SSH.</summary>
[TestFixture]
public class SystemEventsTests {
    [TestCase("usb 5-1: USB disconnect, device number 5", "warn")]
    [TestCase("usb 5-1: New USB device found, idVendor=04a9, idProduct=330c, bcdDevice= 2.10", "info")]
    [TestCase("usb 5-1: Product: Canon Digital Camera", "info")]
    [TestCase("usb 1-1.3: reset high-speed USB device number 4 using xhci_hcd", "warn")]
    [TestCase("usb 1-1.2: device descriptor read/64, error -71", "warn")]
    [TestCase("hwmon hwmon1: Undervoltage detected!", "error")]
    [TestCase("Out of memory: Killed process 1234 (NINA.Polaris) total-vm:3000000kB", "error")]
    [TestCase("indi_gphoto_ccd[3262]: segfault at 0 ip 0000ffff8a1b2c3d sp 0000ffffc1d2e3f0 error 4", "error")]
    [TestCase("cp210x ttyUSB0: cp210x converter now disconnected from ttyUSB0", "info")]
    [TestCase("mmc0: timeout waiting for hardware interrupt", "error")]
    public void KernelLinesThatMatter(string message, string level) {
        var ev = SystemEventClassifier.Classify("kernel", 4, message);
        Assert.That(ev, Is.Not.Null, message);
        Assert.That(ev!.Level, Is.EqualTo(level));
        Assert.That(ev.Category, Is.EqualTo("kernel"));
    }

    [TestCase("thermal_sys: Registered thermal governor 'step_wise'")]
    [TestCase("Booting Linux on physical CPU 0x0000000000 [0x412fd050]")]
    [TestCase("EXT4-fs (mmcblk0p2): mounted filesystem with ordered data mode")]
    public void KernelNoiseStaysOut(string message) {
        Assert.That(SystemEventClassifier.Classify("kernel", 6, message), Is.Null);
    }

    [TestCase("NetworkManager", "<info>  [1729100000.1234] device (wlan0): state change: activated -> disconnected (reason 'supplicant-disconnect')", "warn")]
    [TestCase("NetworkManager", "<info>  [1729100000.1234] device (wlan0): state change: ip-check -> activated (reason 'none')", "info")]
    [TestCase("wpa_supplicant", "wlan0: CTRL-EVENT-DISCONNECTED bssid=aa:bb:cc:dd:ee:ff reason=4 locally_generated=1", "warn")]
    public void WifiComingAndGoing(string id, string message, string level) {
        var ev = SystemEventClassifier.Classify(id, 6, message);
        Assert.That(ev?.Level, Is.EqualTo(level));
        Assert.That(ev!.Category, Is.EqualTo("network"));
    }

    [Test]
    public void ServiceRestartsAndCoreDumps() {
        Assert.Multiple(() => {
            Assert.That(SystemEventClassifier.Classify("systemd", 4,
                "polaris.service: Main process exited, code=dumped, status=6/ABRT")?.Level, Is.EqualTo("warn"));
            Assert.That(SystemEventClassifier.Classify("systemd-coredump", 2,
                "Process 4242 (NINA.Polaris) of user 999 dumped core.")?.Level, Is.EqualTo("error"));
            Assert.That(SystemEventClassifier.Classify("systemd", 6, "Started Session 3 of User polaris."), Is.Null);
        });
    }

    [Test]
    public void OtherProgramsOnlyForErrors_AndNeverPolarisItself() {
        Assert.Multiple(() => {
            Assert.That(SystemEventClassifier.Classify("phd2", 3, "camera connection lost")?.Level, Is.EqualTo("error"));
            Assert.That(SystemEventClassifier.Classify("phd2", 6, "guiding started"), Is.Null);
            Assert.That(SystemEventClassifier.Classify("NINA.Polaris", 3, "already in our log"), Is.Null);
        });
    }

    [TestCase("2026-10-15T22:30:21: FIFO: Shutting down driver: indi_gphoto_ccd", "info")]
    [TestCase("2026-10-15T22:30:21: FIFO: Starting driver indi_gphoto_ccd", "info")]
    [TestCase("2026-10-15T22:30:21: Driver indi_asi_ccd: Terminated by signal 11.", "error")]
    [TestCase("2026-10-15T22:30:21: Driver indi_asi_ccd: stderr EOF", "warn")]
    public void IndiserverDriverEvents(string line, string level) {
        Assert.That(SystemEventClassifier.ClassifyIndiserverLine(line)?.Level, Is.EqualTo(level));
    }

    [TestCase("2026-10-15T22:30:17: Client 8: new arrival from 127.0.0.1:34450 - welcome!")]
    [TestCase("2026-10-15T22:30:21: Driver indi_gphoto_ccd: snooping on Telescope Simulator.EQUATORIAL_EOD_COORD")]
    public void IndiserverChatterStaysOut(string line) {
        Assert.That(SystemEventClassifier.ClassifyIndiserverLine(line), Is.Null);
    }

    [Test]
    public void JournalJson_KernelLine() {
        var line = "{\"__REALTIME_TIMESTAMP\":\"1760565013000000\",\"PRIORITY\":\"6\",\"_TRANSPORT\":\"kernel\",\"MESSAGE\":\"usb 5-1: USB disconnect, device number 5\"}";
        var p = SystemEventClassifier.ParseJournalJson(line);
        Assert.That(p, Is.Not.Null);
        Assert.Multiple(() => {
            Assert.That(p!.Value.Identifier, Is.EqualTo("kernel"));
            Assert.That(p.Value.Priority, Is.EqualTo(6));
            Assert.That(p.Value.Message, Does.Contain("USB disconnect"));
            Assert.That(p.Value.At, Is.EqualTo(DateTime.UnixEpoch.AddSeconds(1760565013)));
        });
    }

    [Test]
    public void JournalJson_MessageAsBytes() {
        // journald emits MESSAGE as a byte array when it is not valid UTF-8 text.
        var line = "{\"SYSLOG_IDENTIFIER\":\"NetworkManager\",\"PRIORITY\":\"4\",\"MESSAGE\":[104,105]}";
        Assert.That(SystemEventClassifier.ParseJournalJson(line)?.Message, Is.EqualTo("hi"));
    }

    [Test]
    public void JournalJson_NotAnEntry() {
        Assert.That(SystemEventClassifier.ParseJournalJson("-- No entries --"), Is.Null);
        Assert.That(SystemEventClassifier.ParseJournalJson("{broken"), Is.Null);
    }

    [Test]
    public void AFloodIsCappedPerCategory_AndCountedAfterwards() {
        var limiter = new SystemJournalService.RateLimiter(perMinute: 3);
        var t0 = new DateTime(2026, 10, 16, 20, 0, 0, DateTimeKind.Utc);
        var admitted = Enumerable.Range(0, 10).Count(i => limiter.Admit("kernel", t0.AddSeconds(i)).Admit);
        Assert.That(admitted, Is.EqualTo(3));
        Assert.That(limiter.Admit("network", t0.AddSeconds(5)).Admit, Is.True, "another category is not affected");
        var next = limiter.Admit("kernel", t0.AddMinutes(1).AddSeconds(1));
        Assert.That(next, Is.EqualTo((true, 7)));
    }

    private const string UsbReset = "usb 5-1: reset high-speed USB device number 3 using ehci-platform";

    [Test]
    public void ARepeatedLineIsLoggedOnce_AndSummarisedPerWindow() {
        // A guide camera the kernel resets on every 2 s exposure: one WARN every
        // ~3 s for an hour, which buried every other warning in the log.
        var c = new SystemJournalService.RepeatCollapser(TimeSpan.FromMinutes(5));
        var t0 = new DateTime(2026, 10, 17, 13, 0, 0, DateTimeKind.Utc);
        var logged = Enumerable.Range(0, 100).Count(i => c.Offer("warn", "kernel", UsbReset, t0.AddSeconds(3 * i)));
        Assert.That(logged, Is.EqualTo(1), "only the first is written");
        Assert.That(c.Flush(t0.AddMinutes(2)), Is.Empty, "nothing due before the window runs out");
        var s = c.Flush(t0.AddMinutes(5).AddSeconds(1));
        Assert.That(s, Has.Count.EqualTo(1));
        Assert.That(s[0].Count, Is.EqualTo(99));
        Assert.That(s[0].Message, Is.EqualTo(UsbReset));
        Assert.That(s[0].Level, Is.EqualTo("warn"));
    }

    [Test]
    public void AFloodThatGoesOnGetsOneSummaryPerWindow() {
        var c = new SystemJournalService.RepeatCollapser(TimeSpan.FromMinutes(5));
        var t0 = new DateTime(2026, 10, 17, 13, 0, 0, DateTimeKind.Utc);
        c.Offer("warn", "kernel", UsbReset, t0);
        for (int i = 1; i <= 100; i++) c.Offer("warn", "kernel", UsbReset, t0.AddSeconds(3 * i));
        Assert.That(c.Flush(t0.AddMinutes(5).AddSeconds(1)).Single().Count, Is.EqualTo(100));
        // Still repeating: counted again, not logged again.
        for (int i = 101; i <= 200; i++)
            Assert.That(c.Offer("warn", "kernel", UsbReset, t0.AddSeconds(3 * i)), Is.False);
        Assert.That(c.Flush(t0.AddMinutes(10).AddSeconds(2)).Single().Count, Is.EqualTo(100));
    }

    [Test]
    public void ALineThatWentQuietIsLoggedInFullNextTime() {
        var c = new SystemJournalService.RepeatCollapser(TimeSpan.FromMinutes(5));
        var t0 = new DateTime(2026, 10, 17, 13, 0, 0, DateTimeKind.Utc);
        c.Offer("warn", "kernel", UsbReset, t0);
        c.Offer("warn", "kernel", UsbReset, t0.AddSeconds(3));
        c.Flush(t0.AddMinutes(6));                // reports the one repeat, forgets the line
        Assert.That(c.Offer("warn", "kernel", UsbReset, t0.AddMinutes(20)), Is.True);
        Assert.That(c.Flush(t0.AddMinutes(30)), Is.Empty, "a lone line has nothing to summarise");
    }

    [Test]
    public void DifferentLinesAreNotCollapsedTogether() {
        var c = new SystemJournalService.RepeatCollapser(TimeSpan.FromMinutes(5));
        var t0 = new DateTime(2026, 10, 17, 13, 0, 0, DateTimeKind.Utc);
        Assert.That(c.Offer("warn", "kernel", UsbReset, t0), Is.True);
        Assert.That(c.Offer("warn", "kernel", "usb 2-1: USB disconnect, device number 2", t0.AddSeconds(1)), Is.True);
        Assert.That(c.Offer("error", "kernel", UsbReset, t0.AddSeconds(2)), Is.True, "another level is another line");
    }

    [Test]
    public async Task TheDiagnosticsZip_HasTheLogsAndRedactsSecrets() {
        var log = new LogService();
        log.Append(new LogEntry(0, DateTime.UtcNow, "info", "server", "login with password=hunter2 ok"));
        var svc = new DiagnosticsBundleService(log, NullLogger<DiagnosticsBundleService>.Instance);

        using var ms = new MemoryStream();
        await svc.WriteZipAsync(ms, CancellationToken.None);
        ms.Position = 0;
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var names = zip.Entries.Select(e => e.FullName).ToList();

        Assert.That(names, Does.Contain("README.txt"));
        Assert.That(names, Does.Contain("polaris-log.jsonl"));
        Assert.That(names, Does.Contain("system/polaris.txt"));
        Assert.That(names, Does.Contain("native-sdk-guard.txt"));
        using var sr = new StreamReader(zip.GetEntry("polaris-log.jsonl")!.Open());
        var text = await sr.ReadToEndAsync();
        Assert.That(text, Does.Not.Contain("hunter2"));
        Assert.That(text, Does.Contain("password=***"));
    }

    [Test]
    public void RedactText_CoversWifiKeys() {
        Assert.That(LogService.RedactText("nmcli ... psk=MySecret123 ifname wlan0"), Does.Not.Contain("MySecret123"));
    }
}
