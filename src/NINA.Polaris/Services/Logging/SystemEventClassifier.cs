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

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NINA.Polaris.Services.Logging;

/// <summary>A system log line worth putting in Polaris's own log.</summary>
public sealed record SystemEvent(string Level, string Category, string Message);

/// <summary>
/// Picks, out of everything the system logs, the lines that explain what
/// happened to a rig: a USB device dropping or resetting, a driver or SDK
/// crashing, the kernel killing a process for memory, the Pi's supply sagging,
/// the WiFi going down, indiserver restarting a driver. Everything else
/// (and the system logs a lot) is left out, so the LOG panel shows these next
/// to Polaris's own events in time order instead of drowning them.
///
/// <para>Pure functions, so the rules are tested against real log lines.</para>
/// </summary>
public static class SystemEventClassifier {
    private record Rule(Regex Pattern, string Level);

    private static Rule R(string pattern, string level) =>
        new(new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), level);

    // Kernel: USB, power, memory, storage, crashes. Order matters: first match wins.
    private static readonly Rule[] KernelRules = {
        R(@"under-?voltage detected", "error"),
        R(@"voltage normali[sz]ed", "info"),
        R(@"out of memory: kill|oom-kill|invoked oom-killer", "error"),
        R(@"\bsegfault at\b|general protection fault|traps: .* trap", "error"),
        R(@"over-?current", "warn"),
        R(@"usb \S+: USB disconnect", "warn"),
        R(@"usb \S+: reset (high|super|full|low)[- ]speed", "warn"),
        R(@"device descriptor read/\S+, error|device not accepting address|unable to enumerate USB device|cannot enable\. Maybe the USB cable is bad", "warn"),
        R(@"usb \S+: New USB device found", "info"),
        R(@"usb \S+: Product: ", "info"),
        R(@"(ttyUSB|ttyACM)\d+", "info"),
        R(@"I/O error|EXT4-fs error|mmc\d+: (timeout|error)|Buffer I/O error", "error"),
        R(@"temperature above threshold|clock throttled|critical temperature|overheat", "warn"),
    };

    // NetworkManager / wpa_supplicant: the WiFi link coming and going.
    private static readonly Rule[] NetworkRules = {
        R(@"CTRL-EVENT-DISCONNECTED|beacon loss|deauthenticat", "warn"),
        R(@"device \(\w+\): state change: [\w-]+ -> (disconnected|failed|unavailable)", "warn"),
        R(@"device \(\w+\): state change: [\w-]+ -> activated", "info"),
        R(@"Activation: .* failed|connection .* failed", "warn"),
        R(@"device \(\w+\): Activation: successful", "info"),
    };

    // systemd: a service dying or being restarted, and core dumps.
    private static readonly Rule[] SystemdRules = {
        R(@"dumped core|Process \d+ \(.*\) of user \d+ (dumped core|terminated abnormally)", "error"),
        R(@"\.service: (Main process exited|Failed with result|Scheduled restart job)", "warn"),
    };

    // indiserver's own log: drivers stopping, crashing, being started again.
    private static readonly Rule[] IndiRules = {
        R(@"Driver \S+: (.*(terminated|died|exited|crash|segmentation|abort).*)", "error"),
        R(@"Driver \S+: stderr EOF|Driver \S+: restart #\d+", "warn"),
        R(@"FIFO: (Shutting down|Starting) driver", "info"),
    };

    /// <summary>Classify one journal entry. <paramref name="identifier"/> is
    /// SYSLOG_IDENTIFIER (or _COMM), "kernel" for kernel lines. Null when the
    /// line is not one Polaris keeps.</summary>
    public static SystemEvent? Classify(string? identifier, int priority, string message) {
        if (string.IsNullOrWhiteSpace(message)) return null;
        var id = (identifier ?? "").Trim();
        Rule[]? rules = null;
        string category;
        if (id.Equals("kernel", StringComparison.OrdinalIgnoreCase)) { rules = KernelRules; category = "kernel"; }
        else if (id.Equals("NetworkManager", StringComparison.OrdinalIgnoreCase)
                 || id.StartsWith("wpa_supplicant", StringComparison.OrdinalIgnoreCase)) { rules = NetworkRules; category = "network"; }
        else if (id.Equals("systemd", StringComparison.OrdinalIgnoreCase)
                 || id.StartsWith("systemd-coredump", StringComparison.OrdinalIgnoreCase)) { rules = SystemdRules; category = "systemd"; }
        else if (id.StartsWith("indi", StringComparison.OrdinalIgnoreCase)) { rules = IndiRules; category = "indi"; }
        else category = id;

        if (rules != null) {
            foreach (var r in rules)
                if (r.Pattern.IsMatch(message)) return new SystemEvent(r.Level, category, message.Trim());
            return null;
        }
        // Any other program: only what it logged as an error or worse, and
        // never Polaris's own lines (they are in the log already).
        if (id.StartsWith("NINA.Polaris", StringComparison.OrdinalIgnoreCase) || id.Equals("polaris", StringComparison.OrdinalIgnoreCase))
            return null;
        if (priority is >= 0 and <= 3) return new SystemEvent("error", category.Length > 0 ? category : "system", message.Trim());
        return null;
    }

    /// <summary>Classify a line of indiserver's log file
    /// ("2026-10-15T22:30:21: FIFO: Starting driver indi_gphoto_ccd").</summary>
    public static SystemEvent? ClassifyIndiserverLine(string line) {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var msg = Regex.Replace(line.Trim(), @"^\d{4}-\d\d-\d\dT[\d:]+:\s*", "");
        foreach (var r in IndiRules)
            if (r.Pattern.IsMatch(msg)) return new SystemEvent(r.Level, "indi", msg);
        return null;
    }

    /// <summary>One line of <c>journalctl -o json</c>: when, who, priority,
    /// message. MESSAGE arrives as an array of bytes when it is not valid
    /// UTF-8; that is decoded too. Null for a line that is not a journal entry.</summary>
    public static (DateTime At, string? Identifier, int Priority, string Message)? ParseJournalJson(string line) {
        if (string.IsNullOrWhiteSpace(line) || line[0] != '{') return null;
        try {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            string? Str(string name) =>
                root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            string? message = null;
            if (root.TryGetProperty("MESSAGE", out var m)) {
                if (m.ValueKind == JsonValueKind.String) message = m.GetString();
                else if (m.ValueKind == JsonValueKind.Array) {
                    var bytes = m.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Number)
                                 .Select(e => (byte)e.GetInt32()).ToArray();
                    message = System.Text.Encoding.UTF8.GetString(bytes);
                }
            }
            if (message == null) return null;

            var at = DateTime.UtcNow;
            if (long.TryParse(Str("__REALTIME_TIMESTAMP"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us))
                at = DateTime.UnixEpoch.AddTicks(us * 10);
            int priority = int.TryParse(Str("PRIORITY"), out var p) ? p : 6;
            var transport = Str("_TRANSPORT");
            var identifier = transport == "kernel" ? "kernel" : (Str("SYSLOG_IDENTIFIER") ?? Str("_COMM"));
            return (at, identifier, priority, message);
        } catch (JsonException) {
            return null;
        }
    }
}
