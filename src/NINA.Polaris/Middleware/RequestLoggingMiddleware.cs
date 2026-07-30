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
using Microsoft.AspNetCore.Http;
using NINA.Polaris.Services.Logging;

namespace NINA.Polaris.Middleware;

/// <summary>
/// DBGLOG-3: emit one <see cref="LogEntry"/> per HTTP request into
/// the singleton <see cref="LogService"/> ring buffer. Captures
/// method, path, status, duration (ms), remote IP. Skips static
/// assets so the buffer doesn't fill with .css/.js/.png requests,
/// and skips the logs endpoint itself so client→server log
/// forwarding doesn't produce its own HTTP entries (feedback loop).
///
/// Position in the pipeline: BEFORE <see cref="AuthMiddleware"/>,
/// so 401 rejections still produce a log row.
/// </summary>
public class RequestLoggingMiddleware {
    private readonly RequestDelegate _next;
    private readonly LogService _log;

    public RequestLoggingMiddleware(RequestDelegate next, LogService log) {
        _next = next;
        _log = log;
    }

    public async Task InvokeAsync(HttpContext ctx) {
        var path = ctx.Request.Path.Value ?? string.Empty;
        if (ShouldSkip(path)) { await _next(ctx); return; }

        var sw = Stopwatch.StartNew();
        Exception? captured = null;
        // Correlation id, on every response. The client logs it next to its own
        // line, so a report of "it said HTTP 500" can be tied to the exact
        // server entry instead of guessing from timestamps.
        var requestId = ctx.TraceIdentifier;
        if (!ctx.Response.HasStarted) ctx.Response.Headers["X-Polaris-Request-Id"] = requestId;
        try {
            await _next(ctx);
        } catch (Exception ex) {
            captured = ex;
            // An unhandled exception used to reach Kestrel, which answers 500
            // with an EMPTY BODY. The client then showed the operator a toast
            // reading exactly "HTTP 500:" with nothing after the colon, and the
            // cause (a duplicate JSON key, in the field report) was visible
            // only in the server log. Answer with something they can act on and
            // quote back. The message is the exception's own text, which is a
            // deliberate trade: this is a single-operator LAN appliance, and a
            // diagnosis they can paste beats hiding the reason.
            if (!ctx.Response.HasStarted) {
                ctx.Response.Clear();
                ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.Headers["X-Polaris-Request-Id"] = requestId;
                var payload = System.Text.Json.JsonSerializer.Serialize(new {
                    error = ex.Message,
                    exceptionType = ex.GetType().FullName,
                    requestId
                });
                try { await ctx.Response.WriteAsync(payload); } catch { /* client gone */ }
            } else {
                // Headers already flushed (a streaming response): nothing to
                // write, so let it surface as a broken response and be logged.
                throw;
            }
        } finally {
            sw.Stop();
            var status = ctx.Response?.StatusCode ?? 0;
            var level = SelectLevel(status, captured, ctx.Request.Method);
            var qIdx = path.IndexOf('?');
            var pathOnly = qIdx >= 0 ? path[..qIdx] : path;
            try {
                _log.Append(new LogEntry(
                    Id: 0,
                    At: DateTime.UtcNow,
                    Level: level,
                    Source: "http",
                    // The request id goes in the message, not only in a field,
                    // so it survives a copy/paste of the log panel.
                    Message: $"{ctx.Request.Method} {pathOnly} {status} "
                           + $"{sw.Elapsed.TotalMilliseconds:F1}ms"
                           + (captured != null || status >= 500 ? $" [{requestId}]" : ""),
                    Method: ctx.Request.Method,
                    Path: pathOnly,
                    Status: status,
                    DurationMs: sw.Elapsed.TotalMilliseconds,
                    RemoteIp: ctx.Connection.RemoteIpAddress?.ToString(),
                    ExceptionType: captured?.GetType().FullName,
                    ExceptionMsg: captured?.Message));
            } catch {
                // never let logging tear down the request pipeline
            }
        }
    }

    /// <summary>Static-asset and self-reference skip list. Static files
    /// would flood the buffer with no diagnostic value (the user can
    /// always inspect cache/network in DevTools). The /api/logs* skip
    /// is the loop-breaker: <c>POST /api/logs/client</c> from the
    /// frontend would otherwise produce both a client entry AND an
    /// http entry per call.</summary>
    private static bool ShouldSkip(string path) {
        if (string.IsNullOrEmpty(path)) return false;
        if (path.StartsWith("/api/logs", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.StartsWith("/css/", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.StartsWith("/js/", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.StartsWith("/img/", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.StartsWith("/fonts/", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.StartsWith("/screenshots/", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.StartsWith("/sky/data/", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.StartsWith("/sky/js/", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.StartsWith("/data/", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Equals("/manifest.webmanifest", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string SelectLevel(int status, Exception? ex, string method) {
        if (ex != null) return "error";
        // HTTP/2 clients negotiate WebSockets via an extended CONNECT
        // (RFC 8441). Kestrel answers 501 to plain CONNECT requests, which
        // is the correct, expected response — not an application fault. Don't
        // surface it as a red error (it floods the log on startup when the
        // noVNC/embedded clients probe). Treat any CONNECT as informational.
        if (string.Equals(method, "CONNECT", StringComparison.OrdinalIgnoreCase))
            return "info";
        return status switch {
            >= 500 => "error",
            >= 400 => "warn",
            _      => "info",
        };
    }
}