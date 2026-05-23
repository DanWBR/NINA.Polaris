using System.Diagnostics;
using System.Text;

namespace NINA.Polaris.Services.Sequencer.Instructions;

/// <summary>
/// Run an external script / binary. Honours <see cref="WaitForExit"/>
/// (default true) and <see cref="TimeoutSeconds"/>. Stdout/stderr are
/// logged at Information / Warning level.
/// </summary>
public class RunExternalScriptInstruction : SequenceInstruction {
    public override string Type => "RunExternalScript";
    public string Path { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string? WorkingDirectory { get; set; }
    public bool WaitForExit { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 60;

    public override IReadOnlyList<string> Validate() =>
        string.IsNullOrWhiteSpace(Path) ? new[] { "Path is empty" } : Array.Empty<string>();

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        var psi = new ProcessStartInfo {
            FileName = Path,
            Arguments = Arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = WorkingDirectory ?? System.IO.Path.GetDirectoryName(Path) ?? ""
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");

        if (!WaitForExit) {
            ctx.Logger.LogInformation("Spawned {Path} (pid {Pid}), not waiting", Path, p.Id);
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
        try {
            await p.WaitForExitAsync(cts.Token);
        } catch (OperationCanceledException) {
            try { p.Kill(true); } catch { }
            throw new TimeoutException($"External script {Path} did not exit within {TimeoutSeconds}s");
        }

        var stdout = await p.StandardOutput.ReadToEndAsync(ct);
        var stderr = await p.StandardError.ReadToEndAsync(ct);
        if (!string.IsNullOrWhiteSpace(stdout)) ctx.Logger.LogInformation("[{Name}] stdout: {Stdout}", Name, stdout.Trim());
        if (!string.IsNullOrWhiteSpace(stderr)) ctx.Logger.LogWarning("[{Name}] stderr: {Stderr}", Name, stderr.Trim());
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"External script {Path} exited with code {p.ExitCode}");
    }
}

/// <summary>
/// Fire an HTTP(S) request to an arbitrary URL with the given method / body /
/// headers. Useful for webhooks (notify Discord/Slack on sequence start,
/// trigger an external dashboard refresh, etc).
/// </summary>
public class SendHttpRequestInstruction : SequenceInstruction {
    public override string Type => "SendHttpRequest";
    public string Url { get; set; } = "";
    public string Method { get; set; } = "POST";
    public string? Body { get; set; }
    public string? ContentType { get; set; } = "application/json";
    public Dictionary<string, string> Headers { get; set; } = new();
    public int TimeoutSeconds { get; set; } = 30;

    public override IReadOnlyList<string> Validate() =>
        string.IsNullOrWhiteSpace(Url) ? new[] { "Url is empty" } : Array.Empty<string>();

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(2) };

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

        using var req = new HttpRequestMessage(new HttpMethod(Method), Url);
        if (Body != null) req.Content = new StringContent(Body, Encoding.UTF8, ContentType ?? "text/plain");
        foreach (var (k, v) in Headers) {
            if (!req.Headers.TryAddWithoutValidation(k, v))
                req.Content?.Headers.TryAddWithoutValidation(k, v);
        }
        using var resp = await _http.SendAsync(req, cts.Token);
        ctx.Logger.LogInformation("HTTP {Method} {Url} → {Status}", Method, Url, (int)resp.StatusCode);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {Method} {Url} → {(int)resp.StatusCode} {resp.ReasonPhrase}");
    }
}
