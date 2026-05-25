using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using NINA.Relay.Protocol;

namespace NINA.Polaris.Services;

/// <summary>
/// Outbound tunnel to a NINA Relay Server, enabling remote internet access
/// to this N.I.N.A. Polaris instance without inbound port-forwarding. The
/// client opens a single WebSocket to the configured relay URL, authenticates
/// with a bearer token, and then replays incoming HTTP requests (delivered
/// as RelayFrame.HttpRequest frames) against the local Kestrel via an
/// internal <see cref="HttpClient"/>.
///
/// Disabled by default; activate with <c>Relay:Enabled=true</c> +
/// <c>Relay:ServerUrl</c> (e.g. wss://relay.example.com/_tunnel) +
/// <c>Relay:Token</c>. The token has to match what the relay-server admin
/// configured under that tenant. Auto-reconnects with exponential backoff
/// on every drop.
/// </summary>
public class RelayClient : IHostedService, IDisposable {
    private readonly IConfiguration _config;
    private readonly ILogger<RelayClient> _logger;
    // GX-10b: the local HTTP listener moved off 5000 (now HTTPS-on-5000)
    // to 5080 by default. Read the configured port here so the tunnel
    // doesn't try to forward to a dead port — falls back to 5080 if
    // the setting is absent. The user-facing override is
    // Server:Http:Port in appsettings.json.
    private readonly HttpClient _local;
    private readonly int _localHttpPort;

    private CancellationTokenSource? _cts;
    private Task? _runner;

    // streamId → local ClientWebSocket bridging that browser-side WS through the tunnel
    private readonly ConcurrentDictionary<uint, LocalWsBridge> _localWs = new();

    public RelayClientState State { get; private set; } = RelayClientState.Disabled;
    public string? AssignedHostname { get; private set; }
    public string? LastError { get; private set; }

    public RelayClient(IConfiguration config, ILogger<RelayClient> logger) {
        _config = config;
        _logger = logger;
        _localHttpPort = config.GetValue("Server:Http:Port", 5080);
        _local = new HttpClient {
            BaseAddress = new Uri($"http://127.0.0.1:{_localHttpPort}"),
            Timeout = TimeSpan.FromSeconds(120),
        };
    }

    public Task StartAsync(CancellationToken cancellationToken) {
        if (!_config.GetValue("Relay:Enabled", false)) {
            _logger.LogInformation("Relay client disabled (set Relay:Enabled=true to enable)");
            State = RelayClientState.Disabled;
            return Task.CompletedTask;
        }

        var url = _config.GetValue<string>("Relay:ServerUrl");
        var token = _config.GetValue<string>("Relay:Token");
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(token)) {
            _logger.LogWarning("Relay enabled but ServerUrl/Token not configured");
            State = RelayClientState.MisconfigError;
            return Task.CompletedTask;
        }

        _cts = new CancellationTokenSource();
        _runner = Task.Run(() => RunWithReconnectAsync(url, token, _cts.Token));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) {
        _cts?.Cancel();
        return _runner ?? Task.CompletedTask;
    }

    private async Task RunWithReconnectAsync(string url, string token, CancellationToken ct) {
        var backoff = TimeSpan.FromSeconds(2);
        var maxBackoff = TimeSpan.FromMinutes(1);

        while (!ct.IsCancellationRequested) {
            try {
                State = RelayClientState.Connecting;
                await RunOnceAsync(url, token, ct);
                // Clean exit → reset backoff
                backoff = TimeSpan.FromSeconds(2);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                break;
            } catch (Exception ex) {
                LastError = ex.Message;
                _logger.LogWarning("Relay client connection error: {Msg} — retrying in {Sec}s",
                    ex.Message, backoff.TotalSeconds);
                State = RelayClientState.Reconnecting;
            }

            try { await Task.Delay(backoff, ct); } catch { break; }
            backoff = TimeSpan.FromSeconds(Math.Min(maxBackoff.TotalSeconds, backoff.TotalSeconds * 1.5));
        }
        State = RelayClientState.Disabled;
    }

    private async Task RunOnceAsync(string url, string token, CancellationToken ct) {
        using var ws = new ClientWebSocket();

        // Optional client certificate (mTLS). The relay matches the cert's
        // SHA-1 thumbprint against the tenant's ClientCertThumbprint field.
        var certPath = _config.GetValue<string?>("Relay:ClientCertPath");
        var certPw = _config.GetValue<string?>("Relay:ClientCertPassword");
        if (!string.IsNullOrEmpty(certPath) && File.Exists(certPath)) {
            try {
                var cert = string.IsNullOrEmpty(certPw)
                    ? System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(certPath, null)
                    : System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(certPath, certPw);
                ws.Options.ClientCertificates ??= new System.Security.Cryptography.X509Certificates.X509CertificateCollection();
                ws.Options.ClientCertificates.Add(cert);
                _logger.LogInformation("Loaded relay client certificate {Subject} (thumbprint {Tp})",
                    cert.Subject, cert.Thumbprint);
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Could not load Relay:ClientCertPath {Path}", certPath);
            }
        }

        await ws.ConnectAsync(new Uri(url), ct);
        _logger.LogInformation("Relay tunnel WebSocket connected to {Url}", url);

        // ---- Auth handshake ----
        await SendAsync(ws, RelayFrame.Build(RelayFrame.Auth, 0, token), ct);
        var first = await ReadFrameAsync(ws, ct) ?? throw new IOException("Relay closed before AuthOk");
        var (op, _, payload) = RelayFrame.Parse(first);
        if (op == RelayFrame.AuthFail) {
            var reason = Encoding.UTF8.GetString(payload.Span);
            State = RelayClientState.AuthFailed;
            throw new InvalidOperationException($"Relay rejected auth: {reason}");
        }
        if (op != RelayFrame.AuthOk) throw new InvalidOperationException($"Unexpected first frame opcode 0x{op:X2}");
        AssignedHostname = Encoding.UTF8.GetString(payload.Span);
        State = RelayClientState.Connected;
        _logger.LogInformation("Relay tunnel ready; published as {Hostname}", AssignedHostname);

        // ---- Receive loop ----
        var sendLock = new SemaphoreSlim(1, 1);
        try {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open) {
                var frame = await ReadFrameAsync(ws, ct);
                if (frame == null) break;
                var (rop, sid, rpayload) = RelayFrame.Parse(frame.Value);
                _ = HandleFrameAsync(ws, sendLock, rop, sid, rpayload, ct);
            }
        } finally {
            // Tear down any local WS bridges that were tied to this tunnel
            foreach (var kv in _localWs.ToArray()) {
                try { kv.Value.Local.Abort(); } catch { }
            }
            _localWs.Clear();
        }
    }

    private async Task HandleFrameAsync(ClientWebSocket ws, SemaphoreSlim sendLock,
        byte op, uint sid, ReadOnlyMemory<byte> payload, CancellationToken ct) {
        try {
            switch (op) {
                case RelayFrame.HttpRequest:
                    await ForwardHttpAsync(ws, sendLock, sid, payload, ct);
                    break;
                case RelayFrame.WsOpen:
                    await OpenLocalWsAsync(ws, sendLock, sid, payload, ct);
                    break;
                case RelayFrame.WsMessage:
                    await ForwardWsMessageToLocalAsync(sid, payload);
                    break;
                case RelayFrame.WsClose:
                    await CloseLocalWsAsync(sid, payload);
                    break;
                case RelayFrame.Ping:
                    await SendAsync(ws, sendLock, RelayFrame.BuildEmpty(RelayFrame.Pong), ct);
                    break;
                case RelayFrame.Pong:
                    /* heartbeat ack */
                    break;
                default:
                    _logger.LogDebug("Relay unknown opcode 0x{Op:X2}", op);
                    break;
            }
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Relay frame handler crashed (sid={Sid})", sid);
        }
    }

    private async Task ForwardHttpAsync(ClientWebSocket ws, SemaphoreSlim sendLock,
        uint sid, ReadOnlyMemory<byte> payload, CancellationToken ct) {
        var (method, pathAndQuery, headers, body) = HttpRequestFrame.Parse(payload);

        using var req = new HttpRequestMessage(new HttpMethod(method), pathAndQuery);
        if (body.Length > 0) {
            req.Content = new ByteArrayContent(body.ToArray());
        }
        foreach (var h in headers) {
            // Try adding as a request header; if that fails (e.g. content-type),
            // route it to the content headers instead.
            if (!req.Headers.TryAddWithoutValidation(h.Key, h.Value)) {
                req.Content?.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }
        }

        HttpResponseMessage resp;
        try {
            resp = await _local.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Local replay failed for {Method} {Path}", method, pathAndQuery);
            await SendHttpErrorAsync(ws, sendLock, sid, 502, "Bad Gateway: " + ex.Message, ct);
            return;
        }

        var respBody = await resp.Content.ReadAsByteArrayAsync(ct);
        var respHeaders = new List<KeyValuePair<string, string>>();
        foreach (var h in resp.Headers) foreach (var v in h.Value)
            respHeaders.Add(new(h.Key, v));
        foreach (var h in resp.Content.Headers) foreach (var v in h.Value)
            respHeaders.Add(new(h.Key, v));

        var responsePayload = HttpResponseFrame.Serialise((int)resp.StatusCode, respHeaders, respBody);
        await SendAsync(ws, sendLock, RelayFrame.Build(RelayFrame.HttpResponse, sid, responsePayload), ct);
        resp.Dispose();
    }

    // ---- WebSocket-over-tunnel: tunnel → local Kestrel ----

    /// <summary>
    /// Open a local WebSocket to ws://127.0.0.1:5000&lt;path&gt; for a streamId
    /// requested by the relay, send WsOpenAck (empty payload = success, otherwise
    /// error text), and start pumping messages local→tunnel until either side
    /// closes.
    /// </summary>
    private async Task OpenLocalWsAsync(ClientWebSocket tunnel, SemaphoreSlim sendLock,
        uint sid, ReadOnlyMemory<byte> payload, CancellationToken ct) {
        var path = WsOpenFrame.Parse(payload);
        var localWs = new ClientWebSocket();
        // Use a derived ws:// URI from the local HTTP base address.
        // GX-10b: HTTP port is now configurable (default 5080, loopback
        // only) — same value the HttpClient above uses.
        var localUri = new Uri($"ws://127.0.0.1:{_localHttpPort}{path}");
        try {
            await localWs.ConnectAsync(localUri, ct);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Local WS connect failed for {Path}", path);
            // WsOpenAck with non-empty payload = failure reason
            await SendAsync(tunnel, sendLock,
                RelayFrame.Build(RelayFrame.WsOpenAck, sid, ex.Message), ct);
            try { localWs.Dispose(); } catch { }
            return;
        }

        var bridge = new LocalWsBridge(localWs);
        _localWs[sid] = bridge;

        // Acknowledge success (empty payload)
        await SendAsync(tunnel, sendLock,
            RelayFrame.BuildEmpty(RelayFrame.WsOpenAck, sid), ct);

        // Start local → tunnel pump in the background
        _ = Task.Run(() => PumpLocalToTunnelAsync(tunnel, sendLock, sid, bridge, ct));
    }

    private async Task PumpLocalToTunnelAsync(ClientWebSocket tunnel, SemaphoreSlim sendLock,
        uint sid, LocalWsBridge bridge, CancellationToken ct) {
        try {
            var buffer = new byte[64 * 1024];
            while (bridge.Local.State == WebSocketState.Open && !ct.IsCancellationRequested) {
                WebSocketReceiveResult r;
                using var ms = new MemoryStream();
                do {
                    r = await bridge.Local.ReceiveAsync(buffer, ct);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    ms.Write(buffer, 0, r.Count);
                    if (ms.Length > 8 * 1024 * 1024) throw new InvalidDataException("WS frame too large (>8MB)");
                } while (!r.EndOfMessage);

                if (r.MessageType == WebSocketMessageType.Close) break;

                var msgType = r.MessageType == WebSocketMessageType.Text
                    ? WsMessageFrame.TypeText
                    : WsMessageFrame.TypeBinary;
                var payload = WsMessageFrame.Serialise(msgType, ms.ToArray());
                await SendAsync(tunnel, sendLock,
                    RelayFrame.Build(RelayFrame.WsMessage, sid, payload), ct);
            }
        } catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) {
            // Normal disconnect
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Local→tunnel WS pump crashed on stream {Sid}", sid);
        } finally {
            // Tell the relay our local WS closed
            try {
                await SendAsync(tunnel, sendLock,
                    RelayFrame.BuildEmpty(RelayFrame.WsClose, sid), CancellationToken.None);
            } catch { }
            _localWs.TryRemove(sid, out _);
            try { bridge.Local.Dispose(); } catch { }
        }
    }

    private async Task ForwardWsMessageToLocalAsync(uint sid, ReadOnlyMemory<byte> payload) {
        if (!_localWs.TryGetValue(sid, out var bridge)) return;
        try {
            var (type, body) = WsMessageFrame.Parse(payload);
            var msgType = type == WsMessageFrame.TypeText
                ? WebSocketMessageType.Text
                : WebSocketMessageType.Binary;
            await bridge.SendLock.WaitAsync();
            try {
                if (bridge.Local.State == WebSocketState.Open) {
                    await bridge.Local.SendAsync(body.ToArray(), msgType, true, CancellationToken.None);
                }
            } finally {
                bridge.SendLock.Release();
            }
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Forward tunnel→local WS failed on stream {Sid}", sid);
            _localWs.TryRemove(sid, out _);
            try { bridge.Local.Abort(); } catch { }
        }
    }

    private Task CloseLocalWsAsync(uint sid, ReadOnlyMemory<byte> payload) {
        if (!_localWs.TryRemove(sid, out var bridge)) return Task.CompletedTask;
        var reason = payload.Length > 0 ? Encoding.UTF8.GetString(payload.Span) : "Relay closed stream";
        return Task.Run(async () => {
            try {
                if (bridge.Local.State == WebSocketState.Open || bridge.Local.State == WebSocketState.CloseReceived) {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await bridge.Local.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, cts.Token);
                }
            } catch { }
            try { bridge.Local.Dispose(); } catch { }
        });
    }

    private static async Task SendHttpErrorAsync(ClientWebSocket ws, SemaphoreSlim sendLock,
        uint sid, int status, string message, CancellationToken ct) {
        var bytes = Encoding.UTF8.GetBytes(message);
        var headers = new List<KeyValuePair<string, string>> {
            new("Content-Type", "text/plain; charset=utf-8")
        };
        var payload = HttpResponseFrame.Serialise(status, headers, bytes);
        await SendAsync(ws, sendLock, RelayFrame.Build(RelayFrame.HttpResponse, sid, payload), ct);
    }

    private static async Task SendAsync(ClientWebSocket ws, byte[] data, CancellationToken ct) {
        await ws.SendAsync(data, WebSocketMessageType.Binary, true, ct);
    }

    private static async Task SendAsync(ClientWebSocket ws, SemaphoreSlim sendLock, byte[] data, CancellationToken ct) {
        await sendLock.WaitAsync(ct);
        try { await ws.SendAsync(data, WebSocketMessageType.Binary, true, ct); }
        finally { sendLock.Release(); }
    }

    private static async Task<ReadOnlyMemory<byte>?> ReadFrameAsync(ClientWebSocket ws, CancellationToken ct) {
        using var ms = new MemoryStream();
        var buf = new byte[16 * 1024];
        while (true) {
            var r = await ws.ReceiveAsync(buf, ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buf, 0, r.Count);
            if (r.EndOfMessage) break;
            if (ms.Length > 64 * 1024 * 1024) throw new InvalidDataException("relay frame too large");
        }
        return ms.ToArray();
    }

    public void Dispose() {
        _cts?.Cancel();
        _local.Dispose();
    }
}

public enum RelayClientState {
    Disabled,
    MisconfigError,
    Connecting,
    Connected,
    Reconnecting,
    AuthFailed
}

/// <summary>
/// One local-side WebSocket whose frames are bridged through the relay tunnel
/// for a particular stream ID.
/// </summary>
internal class LocalWsBridge {
    public ClientWebSocket Local { get; }
    public SemaphoreSlim SendLock { get; } = new(1, 1);
    public LocalWsBridge(ClientWebSocket local) { Local = local; }
}
