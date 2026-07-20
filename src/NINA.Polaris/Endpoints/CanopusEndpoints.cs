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

using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http.Features;
using NINA.Polaris.Services.External;

namespace NINA.Polaris.Endpoints;

/// <summary>HTTP surface for the local "On this server (SBC)" Canopus backend:
/// the Settings assistant panel polls /status, downloads the model+runtime, and
/// starts/stops the local server. The chat itself is served through the
/// /canopus/* reverse-proxy, not here.</summary>
public static class CanopusEndpoints {
    // Reverse-proxy to the "On this device" LLM (Ollama / LM Studio / llama.cpp)
    // so the browser talks same-origin and never needs a CORS grant on that
    // server. Only used when the browser is on the host machine (see the
    // loopback gate below), so forwarding to 127.0.0.1 targets the same Ollama.
    private static readonly HttpClient _deviceLlmHttp = new() { Timeout = TimeSpan.FromMinutes(10) };

    public static void MapCanopusEndpoints(this WebApplication app) {
        var g = app.MapGroup("/api/canopus");

        g.MapGet("/status", (CanopusServerService server, CanopusModelService models) =>
            Results.Ok(new {
                rid = CanopusModelService.Rid,
                running = server.Running,
                llamaRunning = server.LlamaRunning,
                agentRunning = server.AgentRunning,
                lastError = server.LastError,
                unavailableReason = server.UnavailableReason,
                serverDirPresent = server.ServerDirPresent,
                modelPresent = server.ModelPresent,
                runtimePresent = server.RuntimePresent,
                lastHealthCheckAt = server.LastHealthCheckAt,
                download = models.GetStatus()
            }));

        g.MapPost("/start", async (CanopusServerService server) => {
            var ok = await server.StartAsync();
            return ok ? Results.Ok(new { ok = true })
                      : Results.Conflict(new { error = server.LastError ?? "failed to start" });
        });

        g.MapPost("/stop", async (CanopusServerService server) => {
            await server.StopAsync();
            return Results.Ok(new { ok = true });
        });

        // Download the GGUF model + the arch-matched llama.cpp runtime.
        g.MapPost("/model/download", (CanopusModelService models) =>
            models.Start()
                ? Results.Accepted(value: models.GetStatus())
                : Results.Conflict(new { error = "A download is already running" }));

        g.MapGet("/model/download-status", (CanopusModelService models) =>
            Results.Ok(models.GetStatus()));

        g.MapPost("/model/cancel", (CanopusModelService models) => {
            models.Cancel();
            return Results.Ok(new { ok = true });
        });

        // The reduced tool catalog for the "On this device" tier — the in-browser
        // agent (canopus/client/agent.js) fetches it to know which tools to offer.
        g.MapGet("/catalog", () => {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory,
                "canopus", "shared", "tools", "catalog.local.json");
            return System.IO.File.Exists(path)
                ? Results.Content(System.IO.File.ReadAllText(path), "application/json")
                : Results.NotFound(new { error = "catalog not found" });
        });

        // Curated Ollama model catalog for the "On this device" tier — the host's
        // model manager offers these for one-click download and recommends the
        // largest that fits the user's VRAM. Static JSON (see ollama-models.json).
        g.MapGet("/device-models", () => {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory,
                "canopus", "shared", "models", "ollama-models.json");
            return System.IO.File.Exists(path)
                ? Results.Content(System.IO.File.ReadAllText(path), "application/json")
                : Results.NotFound(new { error = "model catalog not found" });
        });

        // Stream the GGUF the host already has to the phone over the LAN (the
        // mobile on-device backend downloads it once). Range-enabled so the
        // plugin's resume works. 404 when the host has no model.
        g.MapGet("/model.gguf", (CanopusModelService models) => {
            var path = models.ModelPath;
            return string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)
                ? Results.NotFound(new { error = "no model on this host" })
                : Results.File(path, "application/octet-stream",
                               fileDownloadName: System.IO.Path.GetFileName(path),
                               enableRangeProcessing: true);
        });

        // Same-origin reverse proxy to the user's local LLM for the "On this
        // device" tier, so the browser never needs a CORS grant on Ollama/LM
        // Studio/llama.cpp. Forwards /device-llm/{port}/{path} to
        // http://127.0.0.1:{port}/{path}. Gated to a loopback client: only when
        // the browser runs on the host machine is the host's 127.0.0.1 the same
        // Ollama the browser means by "this device"; otherwise the client falls
        // back to a direct (CORS) fetch. 421 signals "not the same machine".
        g.MapMethods("/device-llm/{port:int}/{**rest}", new[] { "GET", "POST", "DELETE" },
            async (HttpContext ctx, int port, string? rest) => {
                var ip = ctx.Connection.RemoteIpAddress;
                var loopback = ip != null && (IPAddress.IsLoopback(ip)
                    || (ip.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(ip.MapToIPv4())));
                if (!loopback) {
                    ctx.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
                    await ctx.Response.WriteAsJsonAsync(new { error = "not-same-host" });
                    return;
                }
                if (port < 1 || port > 65535) { ctx.Response.StatusCode = StatusCodes.Status400BadRequest; return; }

                var target = $"http://127.0.0.1:{port}/{rest}{ctx.Request.QueryString.Value}";
                using var req = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), target);
                if (HttpMethods.IsPost(ctx.Request.Method) || HttpMethods.IsDelete(ctx.Request.Method)) {
                    var ms = new MemoryStream();
                    await ctx.Request.Body.CopyToAsync(ms);
                    ms.Position = 0;
                    if (ms.Length > 0) {
                        req.Content = new StreamContent(ms);
                        if (MediaTypeHeaderValue.TryParse(ctx.Request.ContentType, out var mt))
                            req.Content.Headers.ContentType = mt;
                    }
                }
                HttpResponseMessage resp;
                try {
                    resp = await _deviceLlmHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
                } catch (Exception e) {
                    ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
                    await ctx.Response.WriteAsJsonAsync(new { error = "unreachable", detail = e.Message });
                    return;
                }
                ctx.Response.StatusCode = (int)resp.StatusCode;
                var ct = resp.Content.Headers.ContentType?.ToString();
                if (!string.IsNullOrEmpty(ct)) ctx.Response.ContentType = ct;
                // Stream the body through (Ollama's /api/pull emits NDJSON progress).
                ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
                await resp.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
            });

        // Manifest for the "On this device" tier: the FOSS host loads this, injects
        // the local LLM url/model from Settings, and embeds the client from
        // /canopus-client. The tool allowlist is derived from catalog.local.json so
        // the host's tool executor only permits what the local agent can call.
        g.MapGet("/device-manifest", (IConfiguration cfg, CanopusModelService models) => Results.Ok(new {
            version = 1,
            tier = "device",
            product = new { name = "Canopus Assistant (device)", iconEmoji = "🔭",
                            iconUrl = "/canopus-client/img/canopus-icon.png" },
            iframe = new {
                url = "/canopus-client/index.html",
                origin = (string?)null,   // same-origin; the host uses its own origin
                sandbox = "allow-scripts allow-forms allow-popups allow-same-origin allow-modals",
                fabIconEmoji = "🔭",
                fabIconUrl = "/canopus-client/img/canopus-icon.png",
                fabLabel = "Canopus",
            },
            allowlist = DeviceAllowlist(),
            // GGUF source for the mobile app's on-device backend (polaris-llama):
            // the phone downloads this once. Config-driven; null until a source is
            // set (a release asset, or the host's own model-serving endpoint). See
            // MOBILE-LLM-C for serving the file the host already downloads.
            mobileModel = MobileModel(cfg, models),
        }));
    }

    // GGUF source the mobile app downloads for its on-device backend. Prefer the
    // model the host already has (the phone pulls it over the LAN via /model.gguf);
    // else a configured URL (Canopus:MobileModelUrl); else null -> UI shows
    // "not configured". The url may be host-relative; the client makes it absolute.
    private static object? MobileModel(IConfiguration cfg, CanopusModelService models) {
        if (models.ModelPresent) {
            try {
                var fi = new System.IO.FileInfo(models.ModelPath);
                return new {
                    url = "/api/canopus/model.gguf",
                    bytes = fi.Length,
                    name = System.IO.Path.GetFileNameWithoutExtension(fi.Name),
                    sha256 = (string?)null,
                };
            } catch { /* fall through to config */ }
        }
        var url = cfg["Canopus:MobileModelUrl"];
        if (string.IsNullOrWhiteSpace(url)) return null;
        long.TryParse(cfg["Canopus:MobileModelBytes"], out var bytes);
        return new {
            url,
            bytes,
            name = cfg["Canopus:MobileModelName"] ?? "Qwen3 4B (Q4_0)",
            sha256 = cfg["Canopus:MobileModelSha256"],
        };
    }

    // Derive the tool-call allowlist from catalog.local.json (mirror of the Python
    // local_server _build_allowlist), so the device manifest can never drift from
    // the tools the in-browser agent actually offers.
    private static List<object> DeviceAllowlist() {
        var allow = new List<object>();
        var seen = new HashSet<string>();
        void Add(string method, string? p) {
            if (string.IsNullOrEmpty(p)) return;
            method = method.ToUpperInvariant();
            if (seen.Add(method + " " + p)) allow.Add(new { method, pathPattern = p });
        }
        try {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory,
                "canopus", "shared", "tools", "catalog.local.json");
            using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
            foreach (var t in doc.RootElement.GetProperty("tools").EnumerateArray()) {
                if (t.TryGetProperty("polaris", out var pol))
                    Add(pol.TryGetProperty("method", out var m) ? m.GetString() ?? "GET" : "GET",
                        pol.TryGetProperty("path", out var pp) ? pp.GetString() : null);
                if (t.TryGetProperty("image", out var img))
                    Add(img.TryGetProperty("method", out var m) ? m.GetString() ?? "GET" : "GET",
                        img.TryGetProperty("path", out var pp) ? pp.GetString() : null);
                if (t.TryGetProperty("poll", out var poll))
                    Add("GET", poll.TryGetProperty("statusPath", out var sp) ? sp.GetString() : null);
            }
        } catch { /* no catalog -> empty allowlist (device tier just won't act) */ }
        return allow;
    }
}
