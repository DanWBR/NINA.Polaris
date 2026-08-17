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
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace NINA.Ascom.Com;

/// <summary>Failure reported by, or caused by the death of, the out-of-process
/// driver host. Carries the driver's HRESULT when it came from a driver error.</summary>
[SupportedOSPlatform("windows")]
public sealed class AscomHostException : Exception {
    public AscomHostException(string message, int hr = 0) : base(message) { HResult = hr; }
}

/// <summary>
/// WINEXIT-2: parent side of the out-of-process ASCOM filter-wheel host. Starts
/// the driver host by re-launching THIS Polaris exe with <c>--ascom-com-host</c>
/// (self-relaunch — zero extra packaging), and marshals member access to it over
/// a newline-delimited JSON protocol on stdin/stdout. A driver crash is an OS
/// process exit the reader turns into a clean <see cref="AscomHostException"/>,
/// so the API server survives. See <see cref="AscomComHostRunner"/>.
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

    /// <summary>Re-launch this Polaris exe as the ASCOM driver host. Handles both
    /// the published apphost exe and a <c>dotnet &lt;dll&gt;</c> (debug) launch.</summary>
    public static AscomHostChannel Start() {
        var procPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Environment.ProcessPath is unavailable to self-host the ASCOM driver.");
        var entryDll = Assembly.GetEntryAssembly()?.Location;
        var procName = Path.GetFileNameWithoutExtension(procPath);
        var psi = new ProcessStartInfo {
            FileName = procPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        if (string.Equals(procName, "dotnet", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(entryDll)) {
            psi.ArgumentList.Add(entryDll);
        }
        psi.ArgumentList.Add("--ascom-com-host");

        AscomComActivation.Note($"fw host launch self {Path.GetFileName(psi.FileName)} --ascom-com-host");
        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start the ASCOM driver host process.");
        return new AscomHostChannel(proc);
    }

    // ── protocol ops ─────────────────────────────────────────────────────

    public Task ActivateAsync(string progId, CancellationToken ct = default)
        => VoidAsync(new() { ["op"] = "activate", ["progId"] = progId }, ct);

    public Task SetAsync(string member, object value, CancellationToken ct = default)
        => VoidAsync(new() { ["op"] = "set", ["member"] = member, ["value"] = value }, ct);

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

    private async Task VoidAsync(Dictionary<string, object?> payload, CancellationToken ct)
        => await RequestAsync(payload, ct).ConfigureAwait(false);

    // ── transport ────────────────────────────────────────────────────────

    private async Task<JsonElement> RequestAsync(Dictionary<string, object?> payload, CancellationToken ct) {
        if (_dead) throw new AscomHostException(_deadReason, unchecked((int)0x800706BA));
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
            throw new AscomHostException(_deadReason, unchecked((int)0x800706BA));
        } finally {
            _writeLock.Release();
        }

        using (ct.Register(() => tcs.TrySetCanceled(ct))) {
            var resp = await tcs.Task.ConfigureAwait(false);
            if (resp.TryGetProperty("ok", out var okEl) && !okEl.GetBoolean()) {
                var msg = resp.TryGetProperty("error", out var e) ? (e.GetString() ?? "driver error") : "driver error";
                var hr = resp.TryGetProperty("hr", out var h) && h.TryGetInt32(out var hv) ? hv : 0;
                throw new AscomHostException(msg, hr);
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
                } catch { /* non-JSON diagnostic line */ }
            }
        } catch { /* pipe closed */ }
        int? code = null;
        try { if (_proc.HasExited) code = _proc.ExitCode; } catch { }
        MarkDead(code is int c
            ? $"the ASCOM driver crashed (host exited, code {c})."
            : "the ASCOM driver host closed unexpectedly (the driver may have crashed).");
    }

    private async Task ErrLoopAsync() {
        try {
            string? line;
            while ((line = await _proc.StandardError.ReadLineAsync().ConfigureAwait(false)) != null) {
                if (line.Length > 0) AscomComActivation.Note($"[fw-host] {line}");
            }
        } catch { }
    }

    private void MarkDead(string reason) {
        if (_dead) return;
        _dead = true;
        _deadReason = reason;
        AscomComActivation.Note("fw host DEAD: " + reason);
        foreach (var kv in _pending) {
            if (_pending.TryRemove(kv.Key, out var tcs))
                tcs.TrySetException(new AscomHostException(reason, unchecked((int)0x800706BA)));
        }
    }

    public void Dispose() {
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
