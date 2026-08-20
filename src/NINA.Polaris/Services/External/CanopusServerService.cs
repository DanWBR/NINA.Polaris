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
using System.Net.Http;
using System.Net.Sockets;

namespace NINA.Polaris.Services.External;

/// <summary>
/// CANOPUS: manages the local "On this server (SBC)" assistant backend — the
/// self-hosted, keyless tier that runs entirely on the Polaris host so any phone
/// or tablet is a thin client. It owns TWO loopback child processes:
///
///   1. llama.cpp <c>llama-server</c> (Qwen3-4B Q4_0) — the LLM. Launched with
///      <c>--mmap 0</c> (load-bearing: Android/embedded page-cache reclaim makes
///      the mmap'd path ~340x slower — see canopus-eval/MOBILE.md) and half the
///      cores, leaving the rest for the rig.
///   2. the OPEN Canopus agent (<c>canopus/server/local_server.py</c> via uvicorn)
///      — serves the chat client + a local-tier manifest + the agent WebSocket,
///      pointed at llama-server through <c>CANOPUS_LOCAL_LLM_URL</c>.
///
/// Polaris reverse-proxies both under <c>/canopus/*</c> (loopback only, gated by
/// the same auth layer as /indi-web). Same lifecycle shape as
/// <see cref="IndiWebManagerService"/>: dual DI registration (singleton for the
/// endpoints + hosted for the auto-start / health loop), TCP health probes, an
/// AutoStart toggle (default OFF — the model is heavy), and a clear reason string
/// when the host can't run it.
/// </summary>
public sealed class CanopusServerService : BackgroundService {
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly CanopusModelService _models;
    private readonly ILogger<CanopusServerService> _logger;

    private Process? _llama;
    private Process? _agent;

    public int LlamaPort { get; }
    public int AgentPort { get; }
    public bool LlamaRunning { get; private set; }
    public bool AgentRunning { get; private set; }
    public bool Running => LlamaRunning && AgentRunning;
    public string? LastError { get; private set; }
    public DateTime? LastHealthCheckAt { get; private set; }

    public CanopusServerService(IConfiguration config, IWebHostEnvironment env,
                                CanopusModelService models,
                                ILogger<CanopusServerService> logger) {
        _config = config;
        _env = env;
        _models = models;
        _logger = logger;
        LlamaPort = _config.GetValue("Canopus:LlamaPort", 8791);
        AgentPort = _config.GetValue("Canopus:AgentPort", 8790);
    }

    // ---- host readiness -------------------------------------------------
    /// <summary>The open Canopus Python server directory (local_server.py). Ships
    /// beside the app; overridable for a dev checkout.</summary>
    public string ServerDir {
        get {
            var cfg = _config.GetValue<string?>("Canopus:ServerDir", null);
            if (!string.IsNullOrWhiteSpace(cfg)) return cfg!;
            foreach (var root in new[] { _env.ContentRootPath, AppContext.BaseDirectory }) {
                var p = Path.Combine(root, "canopus", "server");
                if (File.Exists(Path.Combine(p, "local_server.py"))) return p;
            }
            return Path.Combine(_env.ContentRootPath, "canopus", "server");
        }
    }

    public string PythonPath => _config.GetValue<string?>("Canopus:PythonPath", null)
        ?? (OperatingSystem.IsWindows() ? "python" : "python3");

    public bool ServerDirPresent => File.Exists(Path.Combine(ServerDir, "local_server.py"));
    public bool ModelPresent => _models.ModelPresent;
    public bool RuntimePresent => _models.RuntimePresent
        || File.Exists(_models.LlamaServerPath); // an override may point outside the data dir

    /// <summary>Null when the host can start the local tier; otherwise a short,
    /// user-facing reason (what to install / download) shown in Settings.</summary>
    public string? UnavailableReason {
        get {
            if (!ServerDirPresent)
                return "Canopus server files are missing from this install.";
            if (!ModelPresent)
                return "The local model isn't downloaded yet. Download it to enable the on-server assistant.";
            if (!RuntimePresent)
                return _models.RuntimeAvailableForArch
                    ? "The local runtime (llama-server) isn't downloaded yet."
                    : $"No prebuilt llama-server is published for this board ({CanopusModelService.Rid}). " +
                      "Set Canopus:LlamaServerPath to one you built.";
            return null;
        }
    }

    // ---- background loop ------------------------------------------------
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        // Stagger after boot so the heavier services settle first.
        try { await Task.Delay(TimeSpan.FromSeconds(4), stoppingToken); }
        catch (TaskCanceledException) { return; }

        if (_config.GetValue("Canopus:AutoStart", false) && UnavailableReason == null) {
            _logger.LogInformation("CanopusServerService: AutoStart enabled, launching local backend");
            try { await StartAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogWarning(ex, "Canopus auto-start failed"); }
        }

        while (!stoppingToken.IsCancellationRequested) {
            try { await ProbeHealthAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogDebug(ex, "Canopus health probe failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
        await StopAsync();
    }

    // ---- start / stop ---------------------------------------------------
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Hides BackgroundService.StartAsync on purpose (like IndiWebManagerService):
    // the hosted lifecycle still runs via the base StartAsync -> ExecuteAsync
    // through IHostedService; this overload is what the endpoints call to launch
    // the child processes on demand and get a success bool back.
    public new async Task<bool> StartAsync(CancellationToken ct = default) {
        await _gate.WaitAsync(ct);
        try {
            var reason = UnavailableReason;
            if (reason != null) { LastError = reason; return false; }

            if (!await StartLlamaAsync(ct)) return false;
            if (!await StartAgentAsync(ct)) { StopLlama(); return false; }
            LastError = null;
            return true;
        } finally {
            _gate.Release();
        }
    }

    private async Task<bool> StartLlamaAsync(CancellationToken ct) {
        if (await ProbePortAsync(LlamaPort, ct)) { LlamaRunning = true; return true; }

        // Thread count for generation. Fewer threads leave headroom for the rig
        // (guiding runs continuously); more threads speed up each turn. On the
        // Radxa Q6A the morning bench measured ~5 t/s generation at 8 threads with
        // Polaris alive, vs roughly half that at 4 — so `Canopus:Threads` is
        // exposed to tune the trade-off. Default: leave two cores for the rig.
        var threads = _config.GetValue("Canopus:Threads", Math.Max(1, Environment.ProcessorCount - 2));
        var ctx = _config.GetValue("Canopus:ContextSize", 8192);
        var exe = _models.LlamaServerPath;
        var model = _models.ModelPath;
        // --no-mmap: keep weights resident (Android/embedded page-cache reclaim
        // makes the mmap'd path ~340x slower — canopus-eval/MOBILE.md). --jinja:
        // use the model's tool template so llama-server returns native OpenAI
        // tool_calls. (Validated against llama-server b10058 on the Q6A: the flag
        // is --no-mmap, NOT the `--mmap 0` that llama-bench takes.)
        var args = $"-m \"{model}\" --host 127.0.0.1 --port {LlamaPort} " +
                   $"--no-mmap -c {ctx} -t {threads} --jinja";
        _logger.LogInformation("Spawning llama-server: {Exe} {Args}", exe, args);
        try {
            _llama = Process.Start(new ProcessStartInfo {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe) is { Length: > 0 } d
                    ? d : Directory.GetCurrentDirectory(),
            });
        } catch (Exception ex) {
            LastError = $"Failed to launch llama-server: {ex.Message}";
            _logger.LogWarning(ex, "llama-server launch failed");
            return false;
        }
        if (_llama == null) { LastError = "llama-server: Process.Start returned null"; return false; }

        // The model load is the slow part (weights read + prefill warmup); allow
        // generous slack on SBC-class flash.
        for (int i = 0; i < 120; i++) {
            try { await Task.Delay(1000, ct); } catch (TaskCanceledException) { return false; }
            if (_llama.HasExited) {
                LastError = $"llama-server exited early (code {_llama.ExitCode}).";
                return false;
            }
            if (await ProbePortAsync(LlamaPort, ct)) { LlamaRunning = true; LastError = null; return true; }
        }
        LastError = "llama-server started but never began listening.";
        return false;
    }

    private async Task<bool> StartAgentAsync(CancellationToken ct) {
        if (await ProbePortAsync(AgentPort, ct)) { AgentRunning = true; return true; }

        var psi = new ProcessStartInfo {
            FileName = PythonPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = ServerDir,
        };
        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add("uvicorn");
        psi.ArgumentList.Add("local_server:app");
        psi.ArgumentList.Add("--host");
        psi.ArgumentList.Add("127.0.0.1");
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add(AgentPort.ToString());
        psi.Environment["CANOPUS_LOCAL_LLM_URL"] = $"http://127.0.0.1:{LlamaPort}";
        psi.Environment["CANOPUS_BASE_PATH"] = "/canopus";
        // Use the reduced catalog + lean system prompt: the small local model can't
        // ingest the full 29-tool catalog fast enough on an SBC.
        psi.Environment["CANOPUS_LOCAL_TIER"] = "1";

        _logger.LogInformation("Spawning Canopus agent: {Py} -m uvicorn local_server:app (cwd {Dir})",
            PythonPath, ServerDir);
        try {
            _agent = Process.Start(psi);
        } catch (Exception ex) {
            LastError = $"Failed to launch the Canopus agent ({PythonPath}): {ex.Message}. " +
                        "Install Python 3 and 'pip install -r requirements.txt' in canopus/server.";
            _logger.LogWarning(ex, "Canopus agent launch failed");
            return false;
        }
        if (_agent == null) { LastError = "Canopus agent: Process.Start returned null"; return false; }

        for (int i = 0; i < 40; i++) {
            try { await Task.Delay(500, ct); } catch (TaskCanceledException) { return false; }
            if (_agent.HasExited) {
                LastError = $"Canopus agent exited early (code {_agent.ExitCode}). " +
                            "Check Python deps: pip install -r canopus/server/requirements.txt";
                return false;
            }
            if (await ProbePortAsync(AgentPort, ct)) { AgentRunning = true; LastError = null; return true; }
        }
        LastError = "Canopus agent started but never began listening.";
        return false;
    }

    public async Task<bool> StopAsync() {
        await _gate.WaitAsync();
        try { StopAgent(); StopLlama(); LastError = null; return true; }
        finally { _gate.Release(); }
    }

    private void StopAgent() => Kill(ref _agent, "Canopus agent", () => AgentRunning = false);
    private void StopLlama() => Kill(ref _llama, "llama-server", () => LlamaRunning = false);

    private void Kill(ref Process? proc, string name, Action clearFlag) {
        var p = proc; proc = null;
        clearFlag();
        if (p == null) return;
        try {
            if (!p.HasExited) { p.Kill(entireProcessTree: true); p.WaitForExit(5000); }
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Failed to stop {Name}", name);
        } finally {
            p.Dispose();
        }
    }

    // ---- health ---------------------------------------------------------
    private async Task ProbeHealthAsync(CancellationToken ct) {
        // A process we own that has died out of band clears its flag immediately;
        // otherwise fall back to the TCP probe (catches an external kill).
        LlamaRunning = _llama is { HasExited: false } && await ProbePortAsync(LlamaPort, ct);
        AgentRunning = _agent is { HasExited: false } && await ProbePortAsync(AgentPort, ct);
        LastHealthCheckAt = DateTime.UtcNow;
    }

    private static Task<bool> ProbePortAsync(int port, CancellationToken ct) =>
        NetProbe.TryConnectAsync(System.Net.IPAddress.Loopback.ToString(), port, 500, ct);
}
