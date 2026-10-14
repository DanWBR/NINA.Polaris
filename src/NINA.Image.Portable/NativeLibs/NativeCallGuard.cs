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
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace NINA.Image.NativeLibs;

/// <summary>
/// Keeps a vendor camera SDK that crashes the process from crashing it again.
///
/// <para>A native SDK fault (a segfault in a USB enumeration, a library built
/// for another glibc) kills the whole .NET process: no try/catch sees it. And
/// the call that faults is usually one Polaris makes on its own at every start
/// or page load (the driver list probes each SDK, auto-connect opens the
/// camera), so systemd restarts Polaris into the same crash every five seconds
/// and the browser only ever sees "the server keeps restarting".</para>
///
/// <para>So each risky call leaves a marker file on disk for as long as it
/// runs. A call that returns, or throws, removes it; a process that dies inside
/// the call cannot. The next start finds the marker and quarantines that SDK:
/// it reports unavailable, with the reason, and its calls are refused, so one
/// crash costs one restart instead of a loop. The quarantine lifts by itself
/// when Polaris is updated (the record carries the version), or when the
/// operator clears it (<see cref="Clear"/>).</para>
/// </summary>
public static class NativeCallGuard {
    /// <summary>Overrides where the markers live (tests).</summary>
    public const string DirEnvVar = "POLARIS_NATIVE_GUARD_DIR";

    private const string InFlightExt = ".inflight";
    private const string QuarantineExt = ".quarantined";
    private static readonly object _sweepLock = new();
    private static string? _sweptDir;
    private static int _counter;

    public static string Dir {
        get {
            var over = Environment.GetEnvironmentVariable(DirEnvVar);
            if (!string.IsNullOrWhiteSpace(over)) return over;
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NINA.Polaris", "native-guard");
        }
    }

    private static string CurrentVersion =>
        (Assembly.GetEntryAssembly() ?? typeof(NativeCallGuard).Assembly).GetName().Version?.ToString() ?? "0";

    /// <summary>Why <paramref name="sdk"/> is switched off, or null when it is
    /// not. A record left by an older Polaris version is dropped.</summary>
    public static string? QuarantineReason(string sdk) {
        try {
            var dir = Dir;
            Sweep(dir);
            var file = Path.Combine(dir, Safe(sdk) + QuarantineExt);
            if (!File.Exists(file)) return null;
            var parts = File.ReadAllText(file).Split('|');
            string what = parts.Length > 0 ? parts[0] : "a call";
            string when = parts.Length > 1 ? parts[1] : "";
            string version = parts.Length > 2 ? parts[2] : "";
            if (version != CurrentVersion) {
                File.Delete(file);
                return null;
            }
            return $"the {sdk} SDK crashed Polaris while {what} ({when}), so Polaris switched it off to keep running. "
                 + "It is tried again after the next Polaris update, or when it is re-enabled.";
        } catch {
            return null;
        }
    }

    /// <summary>Lift the quarantine on <paramref name="sdk"/>. True when there
    /// was one.</summary>
    public static bool Clear(string sdk) {
        try {
            var file = Path.Combine(Dir, Safe(sdk) + QuarantineExt);
            if (!File.Exists(file)) return false;
            File.Delete(file);
            return true;
        } catch {
            return false;
        }
    }

    /// <summary>Every SDK currently switched off, with its reason.</summary>
    public static IReadOnlyList<(string Sdk, string Reason)> Quarantined() {
        var list = new List<(string, string)>();
        try {
            var dir = Dir;
            Sweep(dir);
            if (!Directory.Exists(dir)) return list;
            foreach (var f in Directory.GetFiles(dir, "*" + QuarantineExt)) {
                var sdk = Path.GetFileNameWithoutExtension(f);
                var why = QuarantineReason(sdk);
                if (why != null) list.Add((sdk, why));
            }
        } catch { }
        return list;
    }

    /// <summary>Run a native call that could take the process down. Throws
    /// <see cref="InvalidOperationException"/> without calling when the SDK
    /// is quarantined.</summary>
    public static T Run<T>(string sdk, string what, Func<T> call) {
        var why = QuarantineReason(sdk);
        if (why != null) throw new InvalidOperationException(char.ToUpperInvariant(why[0]) + why[1..]);
        var marker = Mark(sdk, what);
        try {
            return call();
        } finally {
            if (marker != null) { try { File.Delete(marker); } catch { } }
        }
    }

    public static void Run(string sdk, string what, Action call)
        => Run<object?>(sdk, what, () => { call(); return null; });

    private static string? Mark(string sdk, string what) {
        try {
            var dir = Dir;
            Directory.CreateDirectory(dir);
            int n = Interlocked.Increment(ref _counter);
            var file = Path.Combine(dir, $"{Safe(sdk)}.{Environment.ProcessId}.{n}{InFlightExt}");
            File.WriteAllText(file, string.Join("|",
                Clean(what),
                DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture),
                CurrentVersion,
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture)));
            return file;
        } catch {
            // A read-only disk must not take the driver away: run unguarded.
            return null;
        }
    }

    /// <summary>Turn the markers a dead process left behind into quarantine
    /// records. Once per directory per process; markers of a process that is
    /// still running (another Polaris) are left alone.</summary>
    private static void Sweep(string dir) {
        lock (_sweepLock) {
            if (_sweptDir == dir) return;
            _sweptDir = dir;
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, "*" + InFlightExt)) {
                try {
                    var parts = File.ReadAllText(f).Split('|');
                    if (parts.Length >= 4 && int.TryParse(parts[3], out var pid) && pid != Environment.ProcessId && Alive(pid)) continue;
                    if (parts.Length >= 4 && int.TryParse(parts[3], out var pid2) && pid2 == Environment.ProcessId) continue;
                    var name = Path.GetFileName(f);
                    var sdk = name[..name.IndexOf('.')];
                    File.WriteAllText(Path.Combine(dir, sdk + QuarantineExt),
                        string.Join("|", parts.Length > 0 ? parts[0] : "a call", parts.Length > 1 ? parts[1] : "",
                            parts.Length > 2 ? parts[2] : CurrentVersion));
                    File.Delete(f);
                } catch { }
            }
        }
    }

    /// <summary>Forget which directory was swept, so a test can simulate a
    /// fresh start.</summary>
    public static void ResetForTests() {
        lock (_sweepLock) _sweptDir = null;
    }

    private static bool Alive(int pid) {
        try {
            using var p = Process.GetProcessById(pid);
            using var me = Process.GetCurrentProcess();
            return !p.HasExited && string.Equals(p.ProcessName, me.ProcessName, StringComparison.OrdinalIgnoreCase);
        } catch {
            return false;
        }
    }

    private static string Safe(string sdk) =>
        new string((sdk ?? "sdk").Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-').ToArray());

    private static string Clean(string s) => (s ?? "").Replace('|', '/').Replace('\n', ' ').Replace('\r', ' ');
}
