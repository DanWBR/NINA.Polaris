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

using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Hosting;

namespace NINA.Polaris.Services.Logging;

/// <summary>
/// One readable log per capture run (PLAN, AUTORUN, ADV), written into the
/// capture folder beside the night's frames and sent to the storage target
/// with them.
///
/// <para>A plan left running overnight that saved nothing left nothing to look
/// at either: the debug log lives in a ring buffer, the file copy of it is
/// opt-in and stays on the host, and the host was powered off by morning. The
/// run log is written whether or not a frame is saved, starts with the
/// minutes before the run (connections, the start itself), ends with how the
/// run finished or why it did not start, and reaches the share every few
/// minutes while it runs and once more before a power-off.</para>
///
/// <para>Where: <c>{capture folder}/{rig}/logs/{night}</c> (or
/// <c>{rig}/{night}/logs</c> with a night-first layout), see
/// <see cref="ImageWriterService.SessionLogSubDir"/>. Without a capture folder
/// it goes to the host's own log folder and is not pushed.</para>
///
/// <para>One run at a time: a plan drives the ADV engine, and the engine's
/// own <see cref="Begin"/> then returns null, so the plan owns the log.</para>
/// </summary>
public sealed class SessionRunLogService : BackgroundService {
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);
    /// <summary>How often a growing log is sent while the run is going. A host
    /// that loses power mid-run leaves at most this much off the share.</summary>
    internal static readonly TimeSpan PushInterval = TimeSpan.FromMinutes(5);
    /// <summary>How much of the log from before the run goes at the top: the
    /// connections and the start are usually where a run that never began
    /// went wrong.</summary>
    internal static readonly TimeSpan LeadIn = TimeSpan.FromMinutes(10);

    private readonly LogService _log;
    private readonly ProfileService _profiles;
    private readonly IServiceProvider _services;
    private readonly ILogger<SessionRunLogService> _logger;

    private readonly object _gate = new();
    private readonly object _fileGate = new();
    private readonly ConcurrentQueue<LogEntry> _pending = new();
    private readonly SemaphoreSlim _pushGate = new(1, 1);
    private volatile Session? _open;
    private Task _lastPush = Task.CompletedTask;
    private bool _pushFailing;

    /// <summary>An open run log. Opaque to callers: they hand it back to
    /// <see cref="End"/>.</summary>
    public sealed class Session {
        internal Session(string kind, string name, string path, bool pushable) {
            Kind = kind; Name = name; Path = path; Pushable = pushable;
        }
        public string Kind { get; }
        public string Name { get; }
        public string Path { get; }
        internal bool Pushable { get; }
        internal long LeadInLastId { get; set; }
        internal DateTime StartedUtc { get; } = DateTime.UtcNow;
        internal DateTime LastPushUtc { get; set; } = DateTime.UtcNow;
        internal bool Closed { get; set; }
        /// <summary>Guide logs copied into the run's folder.</summary>
        internal List<string> GuideCopies { get; } = new();
        /// <summary>Length of each file at its last send.</summary>
        internal Dictionary<string, long> Pushed { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public SessionRunLogService(LogService log, ProfileService profiles, IServiceProvider services,
                                ILogger<SessionRunLogService> logger, PowerService? power = null) {
        _log = log;
        _profiles = profiles;
        _services = services;
        _logger = logger;
        _log.Appended += OnAppended;
        power?.OnBeforePowerOff(BeforePowerOffAsync);
    }

    /// <summary>The last send, for tests.</summary>
    internal Task LastSync { get { lock (_gate) return _lastPush; } }

    /// <summary>The log of the run in progress, or null.</summary>
    public string? CurrentPath => _open?.Path;

    private void OnAppended(LogEntry e) {
        // Inline on every log call: enqueue and return, nothing else.
        if (_open != null && Include(e)) _pending.Enqueue(e);
    }

    /// <summary>HTTP traffic is a request per poll; only its failures belong
    /// in a run log. Everything the server and the system say is kept.</summary>
    internal static bool Include(LogEntry e) {
        var lvl = (e.Level ?? "").ToLowerInvariant();
        bool problem = lvl is "warn" or "error" or "critical";
        return e.Source switch {
            "http" => problem,
            "client" => problem,
            _ => true
        };
    }

    /// <summary>Open the log of a run. Returns null when another run's log is
    /// already open (a plan driving the ADV engine) or the file cannot be
    /// created; <see cref="End"/> takes null and does nothing with it.</summary>
    public Session? Begin(string kind, string name, IEnumerable<string>? summary = null) {
        lock (_gate) {
            if (_open != null) return null;
            Session s;
            try {
                s = Create(kind, name);
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Run log could not be created");
                return null;
            }
            // Open first, then read the lead-in: anything logged in between is
            // both in the snapshot and in the queue, and the Id cut drops it
            // from the queue.
            _open = s;
            var lead = _log.Snapshot();
            s.LeadInLastId = lead.Count > 0 ? lead[^1].Id : 0;
            var sb = new StringBuilder();
            foreach (var line in Header(s, summary)) sb.Append(line).Append('\n');
            var since = DateTime.UtcNow - LeadIn;
            var before = lead.Where(e => e.At >= since && Include(e)).ToList();
            sb.Append('\n').Append(CultureInfo.InvariantCulture,
                $"---- the {LeadIn.TotalMinutes:F0} minutes before the run ----\n");
            if (before.Count == 0) sb.Append("(nothing logged)\n");
            foreach (var e in before) sb.Append(Format(e));
            sb.Append("\n---- the run ----\n");
            Append(s, sb.ToString());
            _logger.LogInformation("Run log: {Path}", s.Path);
            return s;
        }
    }

    /// <summary>Close a run's log with how it ended, and send it. Safe to call
    /// twice and with null.</summary>
    public void End(Session? s, string outcome, string? error = null) {
        if (s == null) return;
        lock (_gate) {
            if (s.Closed) return;
            // The closing line goes through the logger so it is in the debug
            // log too, then the queue is written out before the footer.
            if (string.IsNullOrWhiteSpace(error))
                _logger.LogInformation("Run log closed: {Kind} '{Name}' {Outcome}", s.Kind, s.Name, outcome);
            else
                _logger.LogWarning("Run log closed: {Kind} '{Name}' {Outcome}: {Error}", s.Kind, s.Name, outcome, error);
            Flush(s);
            s.Closed = true;
            if (ReferenceEquals(_open, s)) _open = null;
            while (_pending.TryDequeue(out _)) { }
            var dur = DateTime.UtcNow - s.StartedUtc;
            var sb = new StringBuilder("\n---- end ----\n");
            sb.Append(CultureInfo.InvariantCulture,
                $"Finished: {DateTime.Now:yyyy-MM-dd HH:mm:ss} ({(int)dur.TotalHours:00}:{dur.Minutes:00}:{dur.Seconds:00} after the start)\n");
            sb.Append("Outcome: ").Append(outcome).Append('\n');
            if (!string.IsNullOrWhiteSpace(error)) sb.Append("Error: ").Append(error).Append('\n');
            Append(s, sb.ToString());
            // On the pool: a connect to a share that is down blocks for its
            // timeout, and this runs inside an engine's teardown.
            _lastPush = Task.Run(() => PushAsync(s, force: true, CancellationToken.None));
        }
    }

    /// <summary>The power-off hook: close the run that is still open (an
    /// Autorun powers off from inside its run) and wait for the last send.</summary>
    private async Task BeforePowerOffAsync(CancellationToken ct) {
        var s = _open;
        if (s != null) End(s, "host powering off");
        Task last;
        lock (_gate) last = _lastPush;
        await last.WaitAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        try {
            while (!stoppingToken.IsCancellationRequested) {
                await Task.Delay(FlushInterval, stoppingToken);
                var s = _open;
                if (s == null) continue;
                lock (_gate) { if (!s.Closed) Flush(s); }
                if (s.Pushable && DateTime.UtcNow - s.LastPushUtc >= PushInterval) {
                    s.LastPushUtc = DateTime.UtcNow;
                    await PushAsync(s, force: false, stoppingToken);
                }
            }
        } catch (OperationCanceledException) {
            // host stopping
        } finally {
            // Polaris stopping (a restart, an update) ends the run with it.
            var s = _open;
            if (s != null) {
                End(s, "Polaris stopped");
                try { await _lastPush.WaitAsync(TimeSpan.FromSeconds(15)); } catch { /* best effort */ }
            }
        }
    }

    private void Flush(Session s) {
        if (_pending.IsEmpty) return;
        var sb = new StringBuilder();
        while (_pending.TryDequeue(out var e)) {
            if (e.Id <= s.LeadInLastId) continue;
            sb.Append(Format(e));
        }
        if (sb.Length > 0) Append(s, sb.ToString());
    }

    private void Append(Session s, string text) {
        lock (_fileGate) {
            try { File.AppendAllText(s.Path, text, new UTF8Encoding(false)); }
            catch (Exception ex) {
                // Not through the logger: that would queue another line for
                // the file that just failed.
                Console.Error.WriteLine($"[RunLog] write to {s.Path} failed: {ex.Message}");
            }
        }
    }

    /// <summary>Bring the guide logs into the run's folder, then send whatever
    /// changed since the last send: the run log and those guide logs.</summary>
    private async Task PushAsync(Session s, bool force, CancellationToken ct) {
        if (!s.Pushable) return;
        await _pushGate.WaitAsync(ct);
        try {
            MirrorGuideLogs(s);
            var push = _services.GetService<StoragePushService>();
            if (push == null || !push.Enabled) return;
            var files = new List<string> { s.Path };
            files.AddRange(s.GuideCopies);
            var changed = new List<(string path, long len)>();
            foreach (var f in files) {
                long len;
                try { len = new FileInfo(f).Length; } catch { continue; }
                s.Pushed.TryGetValue(f, out var was);
                if (len != was || (force && f == s.Path)) changed.Add((f, len));
            }
            if (changed.Count == 0) return;
            await push.UploadNowAsync(changed.Select(c => c.path).ToList(), ct);
            foreach (var (path, len) in changed) s.Pushed[path] = len;
            if (_pushFailing) _logger.LogInformation("Run log reached the storage target again");
            _pushFailing = false;
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            // Once per failing stretch: a share that is down for the night
            // must not add a warning to the log every five minutes.
            if (!_pushFailing)
                _logger.LogWarning("Run log could not be sent to the storage target: {Err}", ex.Message);
            _pushFailing = true;
        } finally {
            _pushGate.Release();
        }
    }

    /// <summary>Copy the guide logs written during the run into
    /// <c>{run log folder}/guide</c>, so they reach the storage target beside
    /// the run log: the PHD2-format log of the native guider, its debug log,
    /// and the guide log of an external PHD2 on this host. Each copy is
    /// refreshed while its source grows.</summary>
    private void MirrorGuideLogs(Session s) {
        var since = s.StartedUtc - LeadIn;
        var dest = Path.Combine(Path.GetDirectoryName(s.Path)!, "guide");
        foreach (var src in GuideLogSources(since)) {
            try {
                var target = Path.Combine(dest, src.Name);
                if (File.Exists(target) && new FileInfo(target).Length == src.Length) {
                    if (!s.GuideCopies.Contains(target)) s.GuideCopies.Add(target);
                    continue;
                }
                Directory.CreateDirectory(dest);
                // The guider keeps the file open for writing; share it.
                using (var from = new FileStream(src.FullName, FileMode.Open, FileAccess.Read,
                                                 FileShare.ReadWrite | FileShare.Delete))
                using (var to = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.Read)) {
                    from.CopyTo(to);
                }
                if (!s.GuideCopies.Contains(target)) s.GuideCopies.Add(target);
            } catch (Exception ex) {
                Console.Error.WriteLine($"[RunLog] guide log copy {src.FullName} failed: {ex.Message}");
            }
        }
    }

    private IEnumerable<FileInfo> GuideLogSources(DateTime sinceUtc) {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirs = new List<(string dir, string pattern)> {
            (GuideSessionLogService.GuideLogDir, "PHD2_GuideLog_*.txt"),
            (GuideSessionLogService.GuideLogDir, "guiding_debug_*.log"),
        };
        foreach (var d in GuideSessionLogService.ExternalPhd2LogDirs(_profiles.Active?.Phd2GuideLogDir))
            dirs.Add((d, "PHD2_GuideLog_*.txt"));
        foreach (var (dir, pattern) in dirs) {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
            FileInfo[] files;
            try { files = new DirectoryInfo(dir).GetFiles(pattern); } catch { continue; }
            // Newest first, so a live PHD2 log wins over an older copy of it.
            foreach (var f in files.OrderByDescending(f => f.LastWriteTimeUtc)) {
                if (f.LastWriteTimeUtc < sinceUtc) continue;
                if (seen.Add(f.Name)) yield return f;
            }
        }
    }

    private Session Create(string kind, string name) {
        var p = _profiles.Active;
        var root = p?.ImageOutputDir ?? "";
        var local = DateTime.Now;
        var night = ImageWriterService.SessionDateForLocal(local);
        string dir;
        bool pushable;
        if (!string.IsNullOrWhiteSpace(root)) {
            var rig = _profiles.ActiveEquipmentProfile?.Name ?? "Default";
            dir = Path.Combine(root, ImageWriterService.SessionLogSubDir(rig, p?.ImageFolderLayout, night));
            pushable = true;
        } else {
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NINA.Polaris", "logs", "runs");
            pushable = false;
        }
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, FileName(kind, name, local));
        for (int n = 2; File.Exists(path); n++)
            path = Path.Combine(dir, Path.GetFileNameWithoutExtension(FileName(kind, name, local)) + $"_{n}.log");
        return new Session(kind, string.IsNullOrWhiteSpace(name) ? kind : name, path, pushable);
    }

    internal static string FileName(string kind, string name, DateTime local) {
        var n = string.IsNullOrWhiteSpace(name) ? "" : "_" + SafeName(name);
        return string.Create(CultureInfo.InvariantCulture, $"{kind}{n}_{local:yyyy-MM-dd_HHmmss}.log");
    }

    internal static string SafeName(string name) {
        var sb = new StringBuilder();
        foreach (var c in name.Trim()) {
            if (char.IsLetterOrDigit(c) || c is '-' or '_' or '.') sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
            if (sb.Length >= 60) break;
        }
        return sb.ToString().Trim('_', '.');
    }

    private IEnumerable<string> Header(Session s, IEnumerable<string>? summary) {
        var p = _profiles.Active;
        var ver = typeof(SessionRunLogService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        var plus = ver.IndexOf('+');
        if (plus > 0) ver = ver[..plus];
        var now = DateTime.Now;
        var zone = TimeZoneInfo.Local;
        yield return $"Polaris {ver} run log";
        yield return $"Run: {s.Kind.ToUpperInvariant()} \"{s.Name}\"";
        var off = zone.GetUtcOffset(now);
        var offText = (off < TimeSpan.Zero ? "-" : "+") + off.Duration().ToString("hh':'mm", CultureInfo.InvariantCulture);
        yield return string.Create(CultureInfo.InvariantCulture,
            $"Started: {now:yyyy-MM-dd HH:mm:ss} local, {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC ({zone.Id}, UTC{offText})");
        yield return $"Host: {Environment.MachineName}";
        yield return $"Rig: {_profiles.ActiveEquipmentProfile?.Name ?? "Default"}";
        yield return string.IsNullOrWhiteSpace(p?.ImageOutputDir)
            ? "Capture folder: NOT SET, frames are not saved"
            : $"Capture folder: {p!.ImageOutputDir}";
        yield return p?.StoragePushEnabled == true
            ? $"Storage push: on ({(p.StorageKind ?? "smb").ToLowerInvariant()})"
            : "Storage push: off";
        yield return "Times below are local.";
        if (summary != null) {
            yield return "";
            foreach (var line in summary) yield return line;
        }
    }

    internal static string Format(LogEntry e) {
        var sb = new StringBuilder();
        var lvl = (e.Level ?? "info").ToUpperInvariant();
        var src = ShortCategory(e.Category) ?? e.Source;
        sb.Append(CultureInfo.InvariantCulture, $"{e.At.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff} {lvl,-8} [{src}] ");
        sb.Append((e.Message ?? "").Replace("\r", "").Replace("\n", "\n    ")).Append('\n');
        if (!string.IsNullOrEmpty(e.ExceptionType)) {
            sb.Append("    ").Append(e.ExceptionType);
            if (!string.IsNullOrEmpty(e.ExceptionMsg)) sb.Append(": ").Append(e.ExceptionMsg);
            sb.Append('\n');
            if (!string.IsNullOrEmpty(e.StackTrace)) {
                foreach (var line in e.StackTrace.Replace("\r", "").Split('\n'))
                    if (!string.IsNullOrWhiteSpace(line)) sb.Append("      ").Append(line.Trim()).Append('\n');
            }
        }
        return sb.ToString();
    }

    private static string? ShortCategory(string? category) {
        if (string.IsNullOrEmpty(category)) return null;
        var dot = category.LastIndexOf('.');
        return dot >= 0 && dot < category.Length - 1 ? category[(dot + 1)..] : category;
    }
}
