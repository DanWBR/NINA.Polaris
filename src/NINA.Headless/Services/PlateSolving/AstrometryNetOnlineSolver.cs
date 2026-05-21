using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NINA.Headless.Services.PlateSolving;

/// <summary>
/// nova.astrometry.net online plate solver. Slow (round-trip + queue) but
/// rock-solid for blind solves with no pixel scale or pointing hints. Useful
/// fallback when ASTAP/PlateSolve3 fail.
///
/// Flow:
///   1. POST /api/login        → session key
///   2. POST /api/upload (multipart) image + JSON request → subid
///   3. POST /api/submissions/{subid} until job assigned → jobid
///   4. GET  /api/jobs/{jobid} until "success" or "failure"
///   5. GET  /api/jobs/{jobid}/calibration → RA/Dec/scale/rotation
///
/// Requires a free API key from nova.astrometry.net (Profile → API Key),
/// configured via PlateSolve:AstrometryApiKey.
/// </summary>
public class AstrometryNetOnlineSolver : IPlateSolver {
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly IConfiguration _config;
    private readonly ILogger<AstrometryNetOnlineSolver> _logger;
    private string? _sessionKey;

    public AstrometryNetOnlineSolver(IConfiguration config, ILogger<AstrometryNetOnlineSolver> logger) {
        _config = config;
        _logger = logger;
    }

    public string Id => "astrometry-net-online";
    public string DisplayName => "Astrometry.net (online)";
    public bool SupportsBlindSolve => true;

    public string ApiKey => _config.GetValue("PlateSolve:AstrometryApiKey", "")!;
    public string BaseUrl => _config.GetValue("PlateSolve:AstrometryBaseUrl",
        "https://nova.astrometry.net/api")!;

    public bool IsAvailable => !string.IsNullOrEmpty(ApiKey);

    public async Task<PlateSolveResult> SolveAsync(string fitsPath, PlateSolveOptions options, CancellationToken ct = default) {
        if (!IsAvailable) return PlateSolveResult.Failed("Astrometry.net API key not configured");
        if (!File.Exists(fitsPath)) return PlateSolveResult.Failed("FITS file not found: " + fitsPath);

        try {
            // 1. Login
            await LoginAsync(ct);
            if (string.IsNullOrEmpty(_sessionKey)) return PlateSolveResult.Failed("Astrometry.net login failed");

            // 2. Upload
            var subId = await UploadAsync(fitsPath, options, ct);
            if (subId == null) return PlateSolveResult.Failed("Astrometry.net upload failed");

            // 3. Poll for job assignment + completion
            var jobId = await WaitForJobAsync(subId.Value, ct);
            if (jobId == null) return PlateSolveResult.Failed("Astrometry.net never assigned a job (queue too long?)");

            var status = await WaitForJobStatusAsync(jobId.Value, ct);
            if (status != "success") return PlateSolveResult.Failed($"Astrometry.net job status: {status}");

            // 4. Fetch calibration
            var calUrl = $"{BaseUrl}/jobs/{jobId}/calibration/";
            var cal = await Http.GetFromJsonAsync<NovaCalibration>(calUrl, ct);
            if (cal == null) return PlateSolveResult.Failed("Astrometry.net returned no calibration");

            return new PlateSolveResult {
                Success = true,
                SolverUsed = Id,
                RaDeg = cal.Ra,
                RaHours = cal.Ra / 15.0,
                DecDeg = cal.Dec,
                ScaleArcsecPerPixel = cal.PixScale,
                RotationDeg = cal.Orientation
            };
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            _logger.LogWarning(ex, "Astrometry.net solve failed");
            return PlateSolveResult.Failed(ex.Message);
        }
    }

    private async Task LoginAsync(CancellationToken ct) {
        var json = JsonSerializer.Serialize(new { apikey = ApiKey });
        using var form = new FormUrlEncodedContent(new[] {
            new KeyValuePair<string, string>("request-json", json)
        });
        using var resp = await Http.PostAsync($"{BaseUrl}/login", form, ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<NovaLoginResponse>(cancellationToken: ct);
        if (body?.Status == "success") _sessionKey = body.Session;
    }

    private async Task<int?> UploadAsync(string fitsPath, PlateSolveOptions options, CancellationToken ct) {
        var requestObj = new Dictionary<string, object?> {
            ["session"] = _sessionKey,
            ["allow_commercial_use"] = "d",
            ["allow_modifications"] = "d",
            ["publicly_visible"] = "n"
        };
        if (options.HintRa.HasValue && options.HintDec.HasValue) {
            requestObj["center_ra"] = options.HintRa.Value * 15.0;
            requestObj["center_dec"] = options.HintDec.Value;
            requestObj["radius"] = Math.Max(1, options.SearchRadiusDeg);
        }
        if (options.ScaleArcsecPerPixel > 0) {
            requestObj["scale_units"] = "arcsecperpix";
            requestObj["scale_type"] = "ev";
            requestObj["scale_est"] = options.ScaleArcsecPerPixel;
            requestObj["scale_err"] = 10;
        }

        var json = JsonSerializer.Serialize(requestObj);
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(json), "request-json");
        await using var fs = File.OpenRead(fitsPath);
        var fileContent = new StreamContent(fs);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file", Path.GetFileName(fitsPath));

        using var resp = await Http.PostAsync($"{BaseUrl}/upload", content, ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<NovaUploadResponse>(cancellationToken: ct);
        return body?.Status == "success" ? body.SubId : null;
    }

    private async Task<int?> WaitForJobAsync(int subId, CancellationToken ct) {
        for (int i = 0; i < 60; i++) {           // up to ~5 min
            ct.ThrowIfCancellationRequested();
            var resp = await Http.GetFromJsonAsync<NovaSubmission>($"{BaseUrl}/submissions/{subId}", ct);
            if (resp?.Jobs != null && resp.Jobs.Count > 0 && resp.Jobs[0].HasValue) {
                return resp.Jobs[0]!.Value;
            }
            await Task.Delay(5000, ct);
        }
        return null;
    }

    private async Task<string> WaitForJobStatusAsync(int jobId, CancellationToken ct) {
        for (int i = 0; i < 120; i++) {          // up to ~10 min
            ct.ThrowIfCancellationRequested();
            var resp = await Http.GetFromJsonAsync<NovaJob>($"{BaseUrl}/jobs/{jobId}", ct);
            var status = resp?.Status ?? "";
            if (status == "success" || status == "failure") return status;
            await Task.Delay(5000, ct);
        }
        return "timeout";
    }

    private class NovaLoginResponse {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("session")] public string? Session { get; set; }
    }
    private class NovaUploadResponse {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("subid")] public int SubId { get; set; }
    }
    private class NovaSubmission {
        [JsonPropertyName("jobs")] public List<int?>? Jobs { get; set; }
    }
    private class NovaJob {
        [JsonPropertyName("status")] public string? Status { get; set; }
    }
    private class NovaCalibration {
        [JsonPropertyName("ra")] public double Ra { get; set; }
        [JsonPropertyName("dec")] public double Dec { get; set; }
        [JsonPropertyName("pixscale")] public double PixScale { get; set; }
        [JsonPropertyName("orientation")] public double Orientation { get; set; }
        [JsonPropertyName("radius")] public double Radius { get; set; }
    }
}
