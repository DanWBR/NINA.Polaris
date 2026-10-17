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

namespace NINA.Polaris.Services.Logging;

/// <summary>
/// Brings the system events that explain a rig's trouble into Polaris's own
/// log (source "system"): USB devices dropping and resetting, a driver or SDK
/// crashing, the out-of-memory killer, under-voltage on a Pi, the WiFi link
/// going down, indiserver restarting a driver. Which lines qualify is decided
/// by <see cref="SystemEventClassifier"/>.
///
/// <para>Almost every field report used to need the operator to run
/// journalctl, dmesg and look at /tmp/indiserver.log over SSH, and the answer
/// was often there: a camera that disconnected on USB a second before the
/// guide frame timed out. Now those lines sit in the LOG panel and the session
/// file, in time order with Polaris's own.</para>
///
/// <para>Linux only. Follows <c>journalctl -f -o json</c>, which the polaris
/// user can read through the systemd-journal group (the .deb adds it). At
/// start it also picks up the last 15 minutes, so the events that led to a
/// crash and restart are in the new session's log. A flood (a USB port
/// resetting in a loop) is capped per category.</para>
/// </summary>
public sealed class SystemJournalService : BackgroundService {
    private readonly LogService _log;
    private readonly ILogger<SystemJournalService> _logger;

    /// <summary>Where indi-web points indiserver's output.</summary>
    internal const string IndiserverLog = "/tmp/indiserver.log";

    private readonly RateLimiter _limiter = new(perMinute: 30);
    private readonly RepeatCollapser _repeats = new(TimeSpan.FromMinutes(5));

    public SystemJournalService(LogService log, ILogger<SystemJournalService> logger) {
        _log = log;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct) {
        if (!OperatingSystem.IsLinux()) return;
        if (!File.Exists("/usr/bin/journalctl") && !File.Exists("/bin/journalctl")) return;
        try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch { return; }

        _ = Task.Run(() => FlushRepeatsAsync(ct), ct);
        if (!await JournalReadableAsync(ct)) {
            _logger.LogInformation(
                "System journal not readable by this user: USB, crash and WiFi events will not appear in the log. " +
                "Adding the polaris user to the systemd-journal group fixes it.");
        } else {
            await BackfillAsync(ct);
            _ = Task.Run(() => FollowJournalAsync(ct), ct);
        }
        await FollowIndiserverLogAsync(ct);
    }

    private static async Task<bool> JournalReadableAsync(CancellationToken ct) {
        var (code, stdout, stderr) = await RunAsync("journalctl", "-k -n 1 -q --no-pager -o short", ct, 10000);
        if (code != 0) return false;
        return !(stdout + stderr).Contains("insufficient permissions", StringComparison.OrdinalIgnoreCase)
               && !(stdout + stderr).Contains("No journal files were opened", StringComparison.OrdinalIgnoreCase);
    }

    private async Task BackfillAsync(CancellationToken ct) {
        var (code, stdout, _) = await RunAsync("journalctl", "-o json --no-pager -q --since -15min", ct, 30000);
        if (code != 0) return;
        int n = 0;
        foreach (var line in stdout.Split('\n')) {
            if (Ingest(line)) n++;
        }
        if (n > 0)
            _logger.LogInformation("System log: {Count} event(s) from the 15 minutes before this start", n);
    }

    private async Task FollowJournalAsync(CancellationToken ct) {
        var backoff = TimeSpan.FromSeconds(5);
        while (!ct.IsCancellationRequested) {
            try {
                var psi = new ProcessStartInfo("journalctl") {
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
                };
                foreach (var a in new[] { "-f", "-o", "json", "--no-pager", "-q", "--since", "now" }) psi.ArgumentList.Add(a);
                using var proc = Process.Start(psi);
                if (proc == null) throw new InvalidOperationException("journalctl did not start");
                using var reg = ct.Register(() => { try { proc.Kill(); } catch { } });
                string? line;
                while ((line = await proc.StandardOutput.ReadLineAsync(ct)) != null) Ingest(line);
                await proc.WaitForExitAsync(ct);
            } catch (OperationCanceledException) {
                return;
            } catch (Exception ex) {
                _logger.LogDebug(ex, "System log: journal follow ended; restarting");
            }
            try { await Task.Delay(backoff, ct); } catch { return; }
        }
    }

    private async Task FollowIndiserverLogAsync(CancellationToken ct) {
        long offset = File.Exists(IndiserverLog) ? new FileInfo(IndiserverLog).Length : 0;
        while (!ct.IsCancellationRequested) {
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct); } catch { return; }
            try {
                if (!File.Exists(IndiserverLog)) { offset = 0; continue; }
                var len = new FileInfo(IndiserverLog).Length;
                if (len < offset) offset = 0;   // indiserver restarted and truncated it
                if (len == offset) continue;
                using var fs = new FileStream(IndiserverLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                fs.Seek(offset, SeekOrigin.Begin);
                using var sr = new StreamReader(fs);
                var chunk = await sr.ReadToEndAsync(ct);
                offset = len;
                foreach (var l in chunk.Split('\n')) {
                    var ev = SystemEventClassifier.ClassifyIndiserverLine(l);
                    if (ev != null) Append(DateTime.UtcNow, ev);
                }
            } catch (OperationCanceledException) {
                return;
            } catch (Exception ex) {
                _logger.LogDebug(ex, "System log: reading {Path} failed", IndiserverLog);
            }
        }
    }

    private bool Ingest(string line) {
        var parsed = SystemEventClassifier.ParseJournalJson(line);
        if (parsed == null) return false;
        var (at, id, prio, msg) = parsed.Value;
        var ev = SystemEventClassifier.Classify(id, prio, msg);
        if (ev == null) return false;
        Append(at, ev);
        return true;
    }

    private void Append(DateTime at, SystemEvent ev) {
        // An identical line again within the window is counted, not logged:
        // a guide camera reset by the kernel on every exposure wrote one WARN
        // every 3 s for an hour and buried the warnings that mattered.
        if (!_repeats.Offer(ev.Level, ev.Category, ev.Message, at)) return;
        var verdict = _limiter.Admit(ev.Category, at);
        if (verdict.Suppressed > 0)
            _log.Append(new LogEntry(0, at, "warn", "system",
                $"{verdict.Suppressed} more {ev.Category} event(s) in the last minute were not logged",
                Category: ev.Category));
        if (!verdict.Admit) return;
        _log.Append(new LogEntry(0, at, ev.Level, "system", ev.Message, Category: ev.Category));
    }

    /// <summary>Writes the "repeated N×" lines, on a timer so a burst that
    /// stops still gets its count reported.</summary>
    private async Task FlushRepeatsAsync(CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch { return; }
            foreach (var r in _repeats.Flush(DateTime.UtcNow))
                _log.Append(new LogEntry(0, DateTime.UtcNow, r.Level, "system",
                    $"{r.Message} (repeated {r.Count}× in the last {Math.Max(1, (int)Math.Round(r.Span.TotalMinutes))} min)",
                    Category: r.Category));
        }
    }

    private static async Task<(int Code, string Stdout, string Stderr)> RunAsync(
            string file, string args, CancellationToken ct, int timeoutMs) {
        try {
            var psi = new ProcessStartInfo(file, args) {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            };
            using var proc = Process.Start(psi);
            if (proc == null) return (-1, "", "");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            var outTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var errTask = proc.StandardError.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);
            return (proc.ExitCode, await outTask, await errTask);
        } catch {
            return (-1, "", "");
        }
    }

    /// <summary>Collapses an identical line repeated within a window: the first
    /// one is let through, the repeats are counted, and <see cref="Flush"/>
    /// hands back one summary per window for as long as they keep coming.</summary>
    internal sealed class RepeatCollapser {
        private readonly TimeSpan _window;
        private sealed class Run {
            public required string Level, Category, Message;
            public DateTime Since;      // start of the current counting window
            public DateTime LastSeen;
            public int Count;           // repeats held back in this window
        }
        private readonly Dictionary<string, Run> _runs = new();

        public RepeatCollapser(TimeSpan window) { _window = window; }

        public readonly record struct Summary(string Level, string Category, string Message, int Count, TimeSpan Span);

        /// <summary>True when the line should be logged.</summary>
        public bool Offer(string level, string category, string message, DateTime at) {
            var key = level + "\u0001" + category + "\u0001" + message;
            lock (_runs) {
                if (_runs.TryGetValue(key, out var r) && at - r.LastSeen < _window) {
                    r.Count++;
                    r.LastSeen = at;
                    return false;
                }
                _runs[key] = new Run { Level = level, Category = category, Message = message, Since = at, LastSeen = at };
                return true;
            }
        }

        /// <summary>One summary per line whose window has run out with repeats
        /// in it. A line still repeating starts a new window; one that has
        /// gone quiet is forgotten, so its next appearance is logged in full.</summary>
        public List<Summary> Flush(DateTime now) {
            var due = new List<Summary>();
            lock (_runs) {
                foreach (var (key, r) in _runs.ToList()) {
                    bool quiet = now - r.LastSeen >= _window;
                    if (!quiet && now - r.Since < _window) continue;
                    if (r.Count > 0) due.Add(new Summary(r.Level, r.Category, r.Message, r.Count, r.LastSeen - r.Since));
                    if (quiet) _runs.Remove(key);
                    else { r.Since = now; r.Count = 0; }
                }
            }
            return due;
        }
    }

    /// <summary>At most <c>perMinute</c> events per category per minute; the
    /// rest are counted and reported once the next one is let through.</summary>
    internal sealed class RateLimiter {
        private readonly int _perMinute;
        private readonly Dictionary<string, (DateTime WindowStart, int Count, int Dropped)> _state = new();
        public RateLimiter(int perMinute) { _perMinute = perMinute; }

        public (bool Admit, int Suppressed) Admit(string category, DateTime at) {
            lock (_state) {
                _state.TryGetValue(category, out var s);
                if (s.WindowStart == default || at - s.WindowStart >= TimeSpan.FromMinutes(1)) {
                    int dropped = s.Dropped;
                    _state[category] = (at, 1, 0);
                    return (true, dropped);
                }
                if (s.Count < _perMinute) {
                    _state[category] = (s.WindowStart, s.Count + 1, s.Dropped);
                    return (true, 0);
                }
                _state[category] = (s.WindowStart, s.Count, s.Dropped + 1);
                return (false, 0);
            }
        }
    }
}
