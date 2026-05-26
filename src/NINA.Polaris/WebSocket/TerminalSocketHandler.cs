using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace NINA.Polaris.WebSocket;

/// <summary>
/// WebSocket bridge between an xterm.js terminal in the browser and a
/// live SSH shell session opened via SSH.NET. One WebSocket = one SSH
/// session. The browser sends keystrokes as plain UTF-8 text frames;
/// the handler forwards them to the SSH ShellStream and pumps the
/// other direction (SSH stdout/stderr → WebSocket text frames) so
/// xterm renders them in real time.
///
/// Authentication is intentionally per-connection: the browser sends a
/// single JSON control frame on connect, { type:"auth", host, port,
/// user, password }, and credentials live only in memory for the
/// lifetime of the socket. Polaris never persists them to disk and
/// won't auto-reconnect; close the WebSocket and the credentials are
/// gone.
///
/// The whole endpoint is gated by a Terminal:Enabled config toggle
/// (default false) so a Polaris deploy without this opt-in returns
/// 404 and the surface stops existing.
/// </summary>
public static class TerminalSocketHandler {
    // Single-frame buffer for the auth handshake. Keystrokes that
    // follow are streamed without buffering.
    private const int AuthFrameMax = 8 * 1024;
    // Idle disconnect: if no traffic flows in either direction for
    // this long, the server closes the session. Default 10 min to
    // keep an accidental leftover from holding the SSH socket open.
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);
    // Polling cadence for SSH→WS pump. 50 ms is comfortable on a
    // Pi 4; lower and the CPU spike is wasteful, higher and the
    // typing feels laggy.
    private static readonly TimeSpan PumpInterval = TimeSpan.FromMilliseconds(50);

    private static readonly JsonSerializerOptions JsonOpts = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task Handle(HttpContext context) {
        if (!context.WebSockets.IsWebSocketRequest) {
            context.Response.StatusCode = 400;
            return;
        }

        var logger = context.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("TerminalSocketHandler");

        var config = context.RequestServices.GetRequiredService<IConfiguration>();
        if (!config.GetValue("Terminal:Enabled", false)) {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsync(
                "Remote terminal disabled. Set Terminal:Enabled=true in appsettings to enable.");
            return;
        }

        var ws = await context.WebSockets.AcceptWebSocketAsync();
        var ct = context.RequestAborted;

        SshClient? sshClient = null;
        ShellStream? shell = null;

        try {
            // ----- AUTH HANDSHAKE -----
            // Browser must send a single JSON frame with the SSH
            // creds before any keystroke. We refuse any other shape.
            var auth = await ReadAuthFrame(ws, ct);
            if (auth == null) {
                await CloseWithError(ws, "Auth frame missing or malformed.");
                return;
            }

            // ----- SSH CONNECT -----
            // SSH.NET ConnectionInfo can take password OR private-key
            // auth methods. We support password-only in v1, a key
            // upload would be a much bigger UI lift.
            var port = auth.Port > 0 ? auth.Port : 22;
            var connectionInfo = new Renci.SshNet.ConnectionInfo(
                auth.Host, port, auth.User,
                new PasswordAuthenticationMethod(auth.User, auth.Password ?? ""));
            connectionInfo.Timeout = TimeSpan.FromSeconds(10);

            sshClient = new SshClient(connectionInfo);
            try {
                sshClient.Connect();
            } catch (Exception sshEx) {
                logger.LogInformation("SSH connect to {Host}:{Port} as {User} failed: {Msg}",
                    auth.Host, port, auth.User, sshEx.Message);
                await CloseWithError(ws, "SSH connect failed: " + sshEx.Message);
                return;
            }

            // PTY size: 80×24 is a safe default; the client can resize
            // via a { type:"resize", cols, rows } JSON control frame
            // any time after auth.
            shell = sshClient.CreateShellStream(
                terminalName: "xterm-256color",
                columns: (uint)(auth.Cols > 0 ? auth.Cols : 80),
                rows: (uint)(auth.Rows > 0 ? auth.Rows : 24),
                width: 0, height: 0,  // pixel dims; 0 = let server pick
                bufferSize: 4096);

            await SendBanner(ws, $"Connected to {auth.User}@{auth.Host}:{port}\r\n", ct);

            // ----- BIDIRECTIONAL PUMP -----
            // Two cooperating loops sharing the same WebSocket:
            //   readFromWs: client keystrokes → SSH stdin
            //   pumpFromSsh: SSH stdout (polled) → client xterm
            // First one to error / close cancels the other via a
            // linked CTS.
            using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var lastActivity = DateTime.UtcNow;

            var readTask = ReadFromWsLoop(ws, shell, sessionCts, () => lastActivity = DateTime.UtcNow, logger);
            var pumpTask = PumpFromSshLoop(ws, shell, sessionCts, () => lastActivity = DateTime.UtcNow, logger);
            var idleTask = IdleWatchdog(sessionCts, () => lastActivity);

            await Task.WhenAny(readTask, pumpTask, idleTask);
            sessionCts.Cancel();
            try { await Task.WhenAll(readTask, pumpTask, idleTask); } catch { /* expected */ }
        } catch (Exception ex) {
            logger.LogWarning(ex, "Terminal session crashed");
            try { await CloseWithError(ws, "Internal error: " + ex.Message); } catch { }
        } finally {
            try { shell?.Dispose(); } catch { }
            try { sshClient?.Disconnect(); sshClient?.Dispose(); } catch { }
            if (ws.State == WebSocketState.Open) {
                try {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure,
                        "session ended", CancellationToken.None);
                } catch { }
            }
        }
    }

    // -----------------------------------------------------------------

    private record TerminalAuth {
        public string Host { get; init; } = "localhost";
        public int Port { get; init; } = 22;
        public string User { get; init; } = "";
        public string? Password { get; init; }
        public int Cols { get; init; }
        public int Rows { get; init; }
    }

    private record TerminalControl {
        public string Type { get; init; } = "";
        public int Cols { get; init; }
        public int Rows { get; init; }
    }

    private static async Task<TerminalAuth?> ReadAuthFrame(System.Net.WebSockets.WebSocket ws, CancellationToken ct) {
        var buf = new byte[AuthFrameMax];
        using var ms = new MemoryStream();
        WebSocketReceiveResult res;
        do {
            res = await ws.ReceiveAsync(buf, ct);
            if (res.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buf, 0, res.Count);
            if (ms.Length > AuthFrameMax) return null;
        } while (!res.EndOfMessage);

        try {
            return JsonSerializer.Deserialize<TerminalAuth>(ms.ToArray(), JsonOpts);
        } catch {
            return null;
        }
    }

    private static async Task SendBanner(System.Net.WebSockets.WebSocket ws, string text, CancellationToken ct) {
        var bytes = Encoding.UTF8.GetBytes(text);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private static async Task CloseWithError(System.Net.WebSockets.WebSocket ws, string message) {
        try {
            if (ws.State == WebSocketState.Open) {
                var bytes = Encoding.UTF8.GetBytes("\r\n[31m" + message + "[0m\r\n");
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
                await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, message,
                    CancellationToken.None);
            }
        } catch { /* best-effort */ }
    }

    private static async Task ReadFromWsLoop(
        System.Net.WebSockets.WebSocket ws,
        ShellStream shell,
        CancellationTokenSource sessionCts,
        Action markActivity,
        ILogger logger) {
        var buf = new byte[4096];
        try {
            while (!sessionCts.IsCancellationRequested && ws.State == WebSocketState.Open) {
                var res = await ws.ReceiveAsync(buf, sessionCts.Token);
                if (res.MessageType == WebSocketMessageType.Close) break;
                if (res.Count == 0) continue;

                markActivity();
                var text = Encoding.UTF8.GetString(buf, 0, res.Count);

                // JSON control frames stay one-shot: { type: "resize", cols, rows }
                // or { type: "input", data: "..." }. Anything else is treated
                // as raw input bytes (the common case, xterm just sends UTF-8
                // bytes per keystroke without wrapping).
                if (text.Length > 0 && text[0] == '{') {
                    TerminalControl? ctl = null;
                    try { ctl = JsonSerializer.Deserialize<TerminalControl>(text, JsonOpts); }
                    catch { /* fall through to raw write */ }
                    if (ctl != null && ctl.Type == "resize" && ctl.Cols > 0 && ctl.Rows > 0) {
                        try {
                            // SSH.NET 2024.x exposes resize on the underlying
                            // Channel via SendWindowChangeRequest. ShellStream
                            // has no direct method, so go through the channel.
                            var channelProp = shell.GetType()
                                .GetProperty("Channel",
                                    System.Reflection.BindingFlags.NonPublic
                                    | System.Reflection.BindingFlags.Instance);
                            var channel = channelProp?.GetValue(shell);
                            var resize = channel?.GetType().GetMethod("SendWindowChangeRequest");
                            resize?.Invoke(channel, new object[] {
                                (uint)ctl.Cols, (uint)ctl.Rows, (uint)0, (uint)0
                            });
                        } catch (Exception rEx) {
                            logger.LogDebug(rEx, "PTY resize failed (non-fatal)");
                        }
                        continue;
                    }
                }

                // Raw keystroke bytes → SSH stdin.
                shell.Write(text);
                shell.Flush();
            }
        } catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception ex) {
            logger.LogDebug(ex, "WS→SSH read loop ended");
        } finally {
            sessionCts.Cancel();
        }
    }

    private static async Task PumpFromSshLoop(
        System.Net.WebSockets.WebSocket ws,
        ShellStream shell,
        CancellationTokenSource sessionCts,
        Action markActivity,
        ILogger logger) {
        var buf = new byte[4096];
        try {
            while (!sessionCts.IsCancellationRequested && ws.State == WebSocketState.Open) {
                if (shell.DataAvailable) {
                    var n = shell.Read(buf, 0, buf.Length);
                    if (n > 0) {
                        markActivity();
                        await ws.SendAsync(
                            new ArraySegment<byte>(buf, 0, n),
                            WebSocketMessageType.Text, true, sessionCts.Token);
                    }
                } else {
                    await Task.Delay(PumpInterval, sessionCts.Token);
                }
            }
        } catch (OperationCanceledException) { /* normal shutdown */ }
        catch (SshConnectionException ex) {
            logger.LogInformation("SSH session ended: {Msg}", ex.Message);
            try { await CloseWithError(ws, "SSH connection closed."); } catch { }
        } catch (Exception ex) {
            logger.LogDebug(ex, "SSH→WS pump ended");
        } finally {
            sessionCts.Cancel();
        }
    }

    private static async Task IdleWatchdog(
        CancellationTokenSource sessionCts,
        Func<DateTime> getLastActivity) {
        try {
            while (!sessionCts.IsCancellationRequested) {
                await Task.Delay(TimeSpan.FromSeconds(30), sessionCts.Token);
                if (DateTime.UtcNow - getLastActivity() > IdleTimeout) {
                    sessionCts.Cancel();
                    return;
                }
            }
        } catch (OperationCanceledException) { /* normal shutdown */ }
    }
}
