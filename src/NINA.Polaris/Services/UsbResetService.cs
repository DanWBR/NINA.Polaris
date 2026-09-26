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
using System.Text.Json;

namespace NINA.Polaris.Services;

/// <summary>
/// The summary the privileged script leaves behind, and the before/after
/// comparison the operator actually reads. Pure, so it can be tested without
/// a USB bus: what matters is that "came back" and "did not come back" are
/// right, because that is the difference between a fixed rig and an hour
/// spent on the wrong theory.
/// </summary>
public sealed record UsbResetReport(string Method, IReadOnlyList<string> Reset,
                                    IReadOnlyList<string> Skipped,
                                    IReadOnlyList<string> Before, IReadOnlyList<string> After) {
    public static UsbResetReport? Parse(string? json) {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            return new UsbResetReport(
                r.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "",
                Strings(r, "reset"), Strings(r, "skipped"), Strings(r, "before"), Strings(r, "after"));
        } catch (JsonException) {
            return null;
        }
    }

    /// <summary>On the bus now, and not before.</summary>
    public static IReadOnlyList<string> Appeared(UsbResetReport r) =>
        r.After.Except(r.Before, StringComparer.Ordinal).ToList();

    /// <summary>Was on the bus, and is not any more. A device that fails to
    /// come back from a re-enumeration is a cable or a power problem, and
    /// saying so by name is the whole point of the report.</summary>
    public static IReadOnlyList<string> Disappeared(UsbResetReport r) =>
        r.Before.Except(r.After, StringComparer.Ordinal).ToList();

    private static List<string> Strings(JsonElement root, string name) {
        var list = new List<string>();
        if (root.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array) {
            foreach (var e in arr.EnumerateArray()) {
                var s = e.GetString();
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
            }
        }
        return list;
    }
}

/// <summary>What a re-enumeration did.</summary>
public sealed class UsbResetResult {
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public string Method { get; init; } = "";
    public IReadOnlyList<string> Reset { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Skipped { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Before { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> After { get; init; } = Array.Empty<string>();
    /// <summary>Present after, absent before. What the reset recovered.</summary>
    public IReadOnlyList<string> Appeared { get; init; } = Array.Empty<string>();
    /// <summary>Absent after, present before. What did not come back.</summary>
    public IReadOnlyList<string> Disappeared { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RestartedDrivers { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Re-enumerates the USB bus, which is the software half of walking to the
/// telescope and replugging a cable.
///
/// A device dropping off the bus mid session is routine on an SBC, and the
/// symptoms are always the same: a camera that no longer appears, a focuser
/// whose serial port vanished, a driver holding a file descriptor for a port
/// that renumbered. All of that is fixed by making the kernel enumerate the
/// bus again; none of it is fixed by restarting Polaris, which is what people
/// try first.
///
/// The privileged part is a packaged script behind a systemd unit, because
/// /sys/bus/usb/drivers/usb is root-only and Polaris runs as an ordinary user.
/// </summary>
public sealed class UsbResetService {
    /// <summary>Where the script leaves its summary.</summary>
    public const string SummaryPath = "/run/polaris/usb-reset.json";
    public const string Unit = "polaris-usb-reset.service";
    private const string SysUsb = "/sys/bus/usb/devices";

    private readonly ILogger<UsbResetService> _logger;
    private readonly IndiWebManagerService _indiWeb;

    public UsbResetService(ILogger<UsbResetService> logger, IndiWebManagerService indiWeb) {
        _logger = logger;
        _indiWeb = indiWeb;
    }

    /// <summary>Linux with the packaged unit present. Everywhere else the
    /// button should not be offered at all.</summary>
    public bool IsSupported =>
        OperatingSystem.IsLinux() && Directory.Exists(SysUsb);

    // Listing the bus is UsbScanService's job (it also reports driver binding,
    // link speed and the serial ports), so this service only resets.

    /// <summary>
    /// Re-enumerate, then restart the INDI drivers. The restart is not
    /// optional politeness: a driver that had a serial port open keeps the old
    /// descriptor after the port renumbers, and answers every command with an
    /// I/O error until it is restarted. Leaving that behind would make the
    /// button look like it broke the mount.
    /// </summary>
    public async Task<UsbResetResult> ResetAsync(CancellationToken ct = default) {
        if (!IsSupported)
            return new UsbResetResult { Ok = false, Error = "USB re-enumeration is only available on a Linux host." };

        try { File.Delete(SummaryPath); } catch { }

        var (code, stdout, stderr) = await RunAsync("systemctl", $"start {Unit}", ct);
        if (code != 0) {
            var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            _logger.LogWarning("USB reset unit failed ({Code}): {Detail}", code, detail);
            return new UsbResetResult {
                Ok = false,
                Error = "Could not run the USB reset: " + Trim(detail)
            };
        }

        var summary = ReadSummary();
        if (summary == null)
            return new UsbResetResult { Ok = false, Error = "The USB reset ran but left no report." };

        var appeared = UsbResetReport.Appeared(summary);
        var gone = UsbResetReport.Disappeared(summary);

        var restarted = await RestartIndiDriversAsync(ct);

        return new UsbResetResult {
            Ok = true,
            Method = summary.Method,
            Reset = summary.Reset,
            Skipped = summary.Skipped,
            Before = summary.Before,
            After = summary.After,
            Appeared = appeared,
            Disappeared = gone,
            RestartedDrivers = restarted
        };
    }

    private async Task<IReadOnlyList<string>> RestartIndiDriversAsync(CancellationToken ct) {
        var restarted = new List<string>();
        try {
            var running = await _indiWeb.GetRunningDriverLabelsAsync(ct);
            foreach (var label in running) {
                try {
                    if (await _indiWeb.RestartDriverAsync(label)) restarted.Add(label);
                } catch (Exception ex) {
                    _logger.LogDebug(ex, "Could not restart INDI driver {Label} after the USB reset", label);
                }
            }
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Could not enumerate INDI drivers after the USB reset");
        }
        return restarted;
    }

    private UsbResetReport? ReadSummary() {
        try {
            if (!File.Exists(SummaryPath)) return null;
            return UsbResetReport.Parse(File.ReadAllText(SummaryPath));
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Could not read the USB reset report");
            return null;
        }
    }

    private static string Trim(string s) =>
        s.Length <= 300 ? s.Trim() : s[..300].Trim() + "...";

    private static async Task<(int code, string stdout, string stderr)> RunAsync(
            string file, string args, CancellationToken ct) {
        var psi = new ProcessStartInfo(file, args) {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        using var p = Process.Start(psi);
        if (p == null) return (-1, "", "could not start " + file);
        var so = await p.StandardOutput.ReadToEndAsync(ct);
        var se = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, so, se);
    }
}
