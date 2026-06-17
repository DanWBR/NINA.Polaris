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
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace NINA.Polaris.Services.External;

/// <summary>
/// Self-update for SBC (.deb) installs. Checks the project's GitHub releases
/// for a newer version, and — on the user's request, with their sudo password —
/// downloads the architecture-matched .deb and installs it.
///
/// <para>The install is the tricky part: the package's postinst restarts
/// <c>polaris.service</c>, i.e. it kills the very process serving this request
/// (systemd's default control-group kill takes the whole unit cgroup). So we do
/// NOT run apt as a child of our own process. Instead we hand the install to a
/// transient systemd scope via <c>sudo systemd-run</c>: apt then runs in its own
/// cgroup, unaffected when polaris.service restarts. The sudo password feeds
/// that single command over stdin and is never logged or persisted.</para>
///
/// <para>Only enabled on Linux .deb installs (the <c>/opt/polaris</c> layout).
/// On Windows / dev runs <see cref="IsSupported"/> is false and the UI hides
/// the feature.</para>
/// </summary>
public class UpdateService {
    private const string Repo = "DanWBR/NINA.Polaris";
    private const string LatestReleaseApi = "https://api.github.com/repos/" + Repo + "/releases/latest";

    private readonly ILogger<UpdateService> _logger;
    private readonly IHttpClientFactory _httpFactory;

    private readonly object _cacheLock = new();
    private UpdateCheckResult? _cached;
    private DateTime _cachedAtUtc = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

    public UpdateService(ILogger<UpdateService> logger, IHttpClientFactory httpFactory) {
        _logger = logger;
        _httpFactory = httpFactory;
    }

    /// <summary>True only on a Linux .deb install (systemd + /opt/polaris). The
    /// self-update flow assumes the packaged layout + service.</summary>
    public bool IsSupported =>
        OperatingSystem.IsLinux()
        && File.Exists("/opt/polaris/NINA.Polaris")
        && Directory.Exists("/run/systemd/system");

    /// <summary>Running version as a comparable 4-part System.Version.</summary>
    public static Version CurrentVersion {
        get {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v ?? new Version(0, 0, 0, 0);
        }
    }

    /// <summary>dpkg architecture string for the running process, used to pick
    /// the right release asset (polaris_VERSION_ARCH.deb).</summary>
    public static string DpkgArch => RuntimeInformation.ProcessArchitecture switch {
        Architecture.Arm64 => "arm64",
        Architecture.X64 => "amd64",
        Architecture.Arm => "armhf",
        Architecture.X86 => "i386",
        _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()
    };

    /// <summary>Query GitHub releases (cached 30 min) and report whether a newer
    /// version exists for this host's architecture. Never throws — network /
    /// rate-limit errors return a result with <c>Error</c> set.</summary>
    public async Task<UpdateCheckResult> CheckAsync(bool force, CancellationToken ct) {
        if (!IsSupported)
            return new UpdateCheckResult { Supported = false, CurrentVersion = CurrentVersion.ToString() };

        lock (_cacheLock) {
            if (!force && _cached != null && DateTime.UtcNow - _cachedAtUtc < CacheTtl)
                return _cached;
        }

        var result = new UpdateCheckResult {
            Supported = true,
            CurrentVersion = CurrentVersion.ToString(),
            Arch = DpkgArch
        };
        try {
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(15);
            using var req = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            // GitHub requires a User-Agent; the versioned media type pins the API.
            req.Headers.UserAgent.ParseAdd("NINA.Polaris-Updater");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) {
                result.Error = $"GitHub returned {(int)resp.StatusCode}";
                return CacheAndReturn(result);
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            result.LatestVersion = NormalizeTag(tag);
            result.ReleaseName = root.TryGetProperty("name", out var n) ? n.GetString() : tag;
            result.ReleaseNotes = root.TryGetProperty("body", out var b) ? b.GetString() : null;
            result.PublishedAt = root.TryGetProperty("published_at", out var p) ? p.GetString() : null;
            result.HtmlUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() : null;

            // Find the .deb asset matching this architecture: polaris_*_<arch>.deb
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array) {
                foreach (var a in assets.EnumerateArray()) {
                    var name = a.TryGetProperty("name", out var an) ? an.GetString() : null;
                    if (string.IsNullOrEmpty(name)) continue;
                    if (name.EndsWith($"_{DpkgArch}.deb", StringComparison.OrdinalIgnoreCase)
                            && name.StartsWith("polaris_", StringComparison.OrdinalIgnoreCase)) {
                        result.AssetName = name;
                        result.AssetUrl = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                        result.AssetSize = a.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                        break;
                    }
                }
            }

            result.UpdateAvailable =
                Version.TryParse(result.LatestVersion, out var latest)
                && latest > CurrentVersion
                && !string.IsNullOrEmpty(result.AssetUrl);
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Update check failed");
            result.Error = ex.Message;
        }
        return CacheAndReturn(result);
    }

    private UpdateCheckResult CacheAndReturn(UpdateCheckResult r) {
        lock (_cacheLock) { _cached = r; _cachedAtUtc = DateTime.UtcNow; }
        return r;
    }

    /// <summary>Strip a leading 'v' from a release tag (v3.3.0.1042 → 3.3.0.1042).</summary>
    private static string? NormalizeTag(string? tag) =>
        string.IsNullOrEmpty(tag) ? tag
        : (tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tag[1..] : tag);

    /// <summary>
    /// Download the latest .deb and kick off the install in a transient systemd
    /// scope (survives the service restart the package triggers). Returns
    /// (true, null) once the install has been launched; the client then polls
    /// for the server to come back on the new version. (false, reason) on a bad
    /// sudo password or a setup problem.
    ///
    /// The asset URL is resolved server-side from GitHub, never taken from the
    /// caller, so the install target can't be redirected.
    /// </summary>
    // Fixed path the .deb is staged to. Must match polaris-self-update.sh in
    // the .deb packaging. polaris home is /home/polaris on the .deb install
    // (set by postinst), and the polaris user can write its own cache dir.
    private static readonly string CacheDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
    private static string DebStagePath => Path.Combine(CacheDir, "polaris-update.deb");

    public async Task<(bool ok, string? error)> InstallAsync(CancellationToken ct) {
        if (!IsSupported) return (false, "Self-update is only available on a Linux .deb install.");

        var check = await CheckAsync(force: true, ct);
        if (!check.UpdateAvailable || string.IsNullOrEmpty(check.AssetUrl))
            return (false, check.Error ?? "No update available to install.");

        // 1. Download the .deb to the fixed staging path the updater unit reads.
        //
        //    The download is deliberately NOT tied to the request abort token
        //    (ct): a multi-tens-of-MB .deb over a slow SBC/mobile link easily
        //    outlasts the browser's fetch timeout, and if the client aborts we
        //    do not want to kill an in-progress install (it left the staged
        //    .deb half-written and surfaced a "task was canceled" error). Bound
        //    the download by its own 10-minute timeout instead so a genuinely
        //    stuck transfer still fails cleanly.
        using var dlCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var dlToken = dlCts.Token;
        try {
            Directory.CreateDirectory(CacheDir);
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromMinutes(10);
            using var dl = new HttpRequestMessage(HttpMethod.Get, check.AssetUrl);
            dl.Headers.UserAgent.ParseAdd("NINA.Polaris-Updater");
            using var resp = await http.SendAsync(dl, HttpCompletionOption.ResponseHeadersRead, dlToken);
            if (!resp.IsSuccessStatusCode)
                return (false, $"Download failed: HTTP {(int)resp.StatusCode}");
            await using (var fs = File.Create(DebStagePath))
                await resp.Content.CopyToAsync(fs, dlToken);
            _logger.LogInformation("Update: downloaded {Asset} ({Bytes} bytes) to {Path}",
                check.AssetName, new FileInfo(DebStagePath).Length, DebStagePath);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Update download failed");
            try { File.Delete(DebStagePath); } catch { }
            return (false, "Download failed: " + ex.Message);
        }

        // 2. Start the on-demand updater unit. The polaris user is authorized
        //    to start exactly this unit, passwordless, by 50-polaris-update.rules
        //    (manage-units → polaris-self-update.service). The unit runs apt in
        //    its own cgroup, so the polaris.service restart the install triggers
        //    won't kill it. --no-block returns once the job is enqueued.
        try {
            var psi = new ProcessStartInfo {
                FileName = "systemctl",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("start");
            psi.ArgumentList.Add("--no-block");
            psi.ArgumentList.Add("polaris-self-update.service");

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start systemctl");

            // Not linked to ct for the same reason as the download above: once
            // we have the .deb staged, launching the install must not be undone
            // by a client that has already navigated away / timed out.
            using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var stderr = await proc.StandardError.ReadToEndAsync(waitCts.Token);
            await proc.WaitForExitAsync(waitCts.Token);

            if (proc.ExitCode != 0) {
                try { File.Delete(DebStagePath); } catch { }
                var msg = (stderr ?? "").Contains("authentication", StringComparison.OrdinalIgnoreCase)
                    || (stderr ?? "").Contains("not authorized", StringComparison.OrdinalIgnoreCase)
                    ? "Not authorized to install the update. The PolicyKit rule "
                        + "(50-polaris-update.rules) may be missing — reinstall the .deb."
                    : $"Install launch failed (exit {proc.ExitCode}). {stderr}".Trim();
                _logger.LogWarning("Update install launch failed: {Err}", msg);
                return (false, msg);
            }

            _logger.LogInformation("Update: install unit started; service will restart on completion.");
            return (true, null);
        } catch (Exception ex) {
            try { File.Delete(DebStagePath); } catch { }
            _logger.LogWarning(ex, "Update install launch error");
            return (false, "Install launch error: " + ex.Message);
        }
    }
}

/// <summary>Result of an update check. <c>Supported</c> is false off a Linux
/// .deb install; the UI hides the feature then.</summary>
public class UpdateCheckResult {
    public bool Supported { get; set; }
    public bool UpdateAvailable { get; set; }
    public string CurrentVersion { get; set; } = "";
    public string? LatestVersion { get; set; }
    public string? ReleaseName { get; set; }
    public string? ReleaseNotes { get; set; }
    public string? PublishedAt { get; set; }
    public string? HtmlUrl { get; set; }
    public string? Arch { get; set; }
    public string? AssetName { get; set; }
    public string? AssetUrl { get; set; }
    public long AssetSize { get; set; }
    public string? Error { get; set; }
}
