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
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace NINA.Ascom.Com;

/// <summary>A failure reported by the out-of-process driver host, carrying the
/// driver's real HRESULT so the adapter can rebuild the same clean message the
/// in-process path produces via <see cref="AscomComActivation.ConnectFailed"/>.</summary>
[SupportedOSPlatform("windows")]
public sealed class AscomHostException : Exception {
    public string Kind { get; }
    public AscomHostException(string kind, string message, int hr) : base(message) {
        Kind = kind;
        HResult = hr;
    }
}

/// <summary>
/// WINEXIT-2 (#650): launches the <c>NINA.Ascom.Host</c> child process and
/// marshals ASCOM member access to it over a newline-delimited JSON protocol
/// on stdin/stdout. The child owns the COM object on its own STA + message
/// pump, so:
///
/// <list type="bullet">
///   <item>a 32-bit-only driver runs in the 32-bit (win-x86) child, which the
///   64-bit host cannot load in-process; and</item>
///   <item>a driver that dies with a corrupted-state / native crash takes down
///   only the child — the reader sees the pipe close and every pending and
///   future call fails cleanly, the Polaris host survives.</item>
/// </list>
///
/// <para>One channel hosts exactly one driver. Requests are correlated by id so
/// the class is safe to call concurrently, though ASCOM's STA serialises the
/// actual work in the child.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AscomHostChannel : IDisposable {
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Process _proc;
    private readonly StreamWriter _stdin;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private long _nextId;
    private volatile bool _dead;
    private string _deadReason = "the ASCOM driver host is not running";

    public bool Dead => _dead;

    private AscomHostChannel(Process proc) {
        _proc = proc;
        _stdin = proc.StandardInput;
        _ = Task.Run(ReadLoopAsync);
        _ = Task.Run(ErrLoopAsync);
    }

    /// <summary>Path to the packaged child exe for the wanted bitness, or null
    /// when it is not present (e.g. a build that didn't stage it).</summary>
    public static string? ExePath(bool wantX86) {
        var rid = wantX86 ? "win-x86" : "win-x64";
        var p = Path.Combine(AppContext.BaseDirectory, "ascom-host", rid, "NINA.Ascom.Host.exe");
        return File.Exists(p) ? p : null;
    }

    /// <summary>True when the child exe for the wanted bitness is available.</summary>
    public static bool IsAvailable(bool wantX86) => ExePath(wantX86) != null;

    /// <summary>Spawn the child host for the wanted bitness. Throws when the
    /// exe is missing (caller should have checked <see cref="IsAvailable"/>).</summary>
    public static AscomHostChannel Start(bool wantX86) {
        var exe = ExePath(wantX86)
            ?? throw new FileNotFoundException(
                $"ASCOM driver host ({(wantX86 ? "win-x86" : "win-x64")}) is not packaged.");
        var psi = new ProcessStartInfo {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        AscomComActivation.Note($"host launch {Path.GetFileName(exe)} ({(wantX86 ? "x86" : "x64")})");
        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start the ASCOM driver host process.");
        return new AscomHostChannel(proc);
    }

    // ── protocol ops ─────────────────────────────────────────────────────

    public Task ActivateAsync(string progId, CancellationToken ct = default)
        => VoidAsync(new() { ["op"] = "activate", ["progId"] = progId }, ct);

    public Task SetAsync(string member, object value, string vt, CancellationToken ct = default)
        => VoidAsync(new() { ["op"] = "set", ["member"] = member, ["value"] = value, ["vt"] = vt }, ct);

    public Task SetupAsync(CancellationToken ct = default)
        => VoidAsync(new() { ["op"] = "setup" }, ct);

    public async Task<bool> GetBoolAsync(string member, CancellationToken ct = default)
        => (await ValueAsync(member, ct).ConfigureAwait(false)).GetBoolean();

    public async Task<int> GetIntAsync(string member, CancellationToken ct = default)
        => (await ValueAsync(member, ct).ConfigureAwait(false)).GetInt32();

    public async Task<string> GetStringAsync(string member, CancellationToken ct = default) {
        var v = await ValueAsync(member, ct).ConfigureAwait(false);
        return v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : v.ToString();
    }

    public async Task<string[]> GetStringArrayAsync(string member, CancellationToken ct = default) {
        var v = await ValueAsync(member, ct).ConfigureAwait(false);
        if (v.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        var list = new List<string>(v.GetArrayLength());
        foreach (var e in v.EnumerateArray()) list.Add(e.GetString() ?? "");
        return list.ToArray();
    }

    private async Task<JsonElement> ValueAsync(string member, CancellationToken ct) {
        var resp = await RequestAsync(new() { ["op"] = "get", ["member"] = member }, ct).ConfigureAwait(false);
        return resp.TryGetProperty("value", out var v) ? v.Clone() : default;
    }

    private async Task VoidAsync(Dictionary<string, object?> payload, CancellationToken ct) {
        await RequestAsync(payload, ct).ConfigureAwait(false);
    }

    // ── transport ────────────────────────────────────────────────────────

    private async Task<JsonElement> RequestAsync(Dictionary<string, object?> payload, CancellationToken ct) {
        if (_dead) throw new AscomHostException("dead", _deadReason, unchecked((int)0x800706BA));
        long id = Interlocked.Increment(ref _nextId);
        payload["id"] = id;
        var line = JsonSerializer.Serialize(payload);

        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try {
            await _stdin.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
            await _stdin.FlushAsync(ct).ConfigureAwait(false);
        } catch (Exception ex) {
            _pending.TryRemove(id, out _);
            MarkDead($"failed to write to the ASCOM driver host: {ex.Message}");
            throw new AscomHostException("dead", _deadReason, unchecked((int)0x800706BA));
        } finally {
            _writeLock.Release();
        }

        using (ct.Register(() => tcs.TrySetCanceled(ct))) {
            var resp = await tcs.Task.ConfigureAwait(false);
            // ok=false → rebuild the driver's error with its HRESULT preserved.
            if (resp.TryGetProperty("ok", out var okEl) && !okEl.GetBoolean()) {
                var msg = resp.TryGetProperty("error", out var e) ? (e.GetString() ?? "driver error") : "driver error";
                var hr = resp.TryGetProperty("hr", out var h) && h.TryGetInt32(out var hv) ? hv : 0;
                var kind = resp.TryGetProperty("kind", out var k) ? (k.GetString() ?? "com") : "com";
                if (kind == "bitness") throw new NotSupportedException(msg);
                throw new AscomHostException(kind, msg, hr);
            }
            return resp;
        }
    }

    private async Task ReadLoopAsync() {
        try {
            string? line;
            while ((line = await _proc.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null) {
                if (line.Length == 0) continue;
                try {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var id)
                        && _pending.TryRemove(id, out var tcs)) {
                        tcs.TrySetResult(root.Clone());
                    }
                } catch { /* a non-JSON line is diagnostic noise; ignore */ }
            }
        } catch { /* pipe closed */ }
        int? code = null;
        try { if (_proc.HasExited) code = _proc.ExitCode; } catch { }
        MarkDead(code is int c
            ? $"the ASCOM driver host exited (code {c}) — the driver may have crashed."
            : "the ASCOM driver host closed unexpectedly — the driver may have crashed.");
    }

    private async Task ErrLoopAsync() {
        try {
            string? line;
            while ((line = await _proc.StandardError.ReadLineAsync().ConfigureAwait(false)) != null) {
                if (line.Length > 0) AscomComActivation.Note($"[host] {line}");
            }
        } catch { }
    }

    private void MarkDead(string reason) {
        if (_dead) return;
        _dead = true;
        _deadReason = reason;
        AscomComActivation.Note("host DEAD: " + reason);
        foreach (var kv in _pending) {
            if (_pending.TryRemove(kv.Key, out var tcs))
                tcs.TrySetException(new AscomHostException("dead", reason, unchecked((int)0x800706BA)));
        }
    }

    public void Dispose() {
        // Ask the child to release the driver and exit; then make sure it's gone.
        if (!_dead) {
            try {
                var line = JsonSerializer.Serialize(new Dictionary<string, object?> {
                    ["id"] = Interlocked.Increment(ref _nextId), ["op"] = "dispose"
                });
                _stdin.WriteLine(line);
                _stdin.Flush();
            } catch { }
        }
        try { _stdin.Close(); } catch { }
        try { if (!_proc.WaitForExit(2000)) _proc.Kill(entireProcessTree: true); } catch { }
        try { _proc.Dispose(); } catch { }
        try { _writeLock.Dispose(); } catch { }
    }
}
