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
using System.Security.Cryptography;
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

    /// <summary>Running version as the X.Y.Z string shown in the UI. .NET
    /// normalises the assembly version to 4 parts (0.85.1.0); we only ever
    /// release with 3-part tags (0.85.1), so drop the trailing build/revision
    /// component for display. Comparisons still use the full <see
    /// cref="CurrentVersion"/> object, so this is purely cosmetic.</summary>
    public static string CurrentVersionShort {
        get {
            var v = CurrentVersion;
            return $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
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
            return new UpdateCheckResult { Supported = false, CurrentVersion = CurrentVersionShort };

        lock (_cacheLock) {
            if (!force && _cached != null && DateTime.UtcNow - _cachedAtUtc < CacheTtl)
                return _cached;
        }

        var result = new UpdateCheckResult {
            Supported = true,
            CurrentVersion = CurrentVersionShort,
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
            var (assetName, assetUrl, assetSize) = PickArchAsset(root);
            result.AssetName = assetName;
            result.AssetUrl = assetUrl;
            result.AssetSize = assetSize;

            result.UpdateAvailable =
                Version.TryParse(result.LatestVersion, out var latest)
                && latest > CurrentVersion
                && !string.IsNullOrEmpty(result.AssetUrl);

            // Changelog: list the commits between the installed version's tag
            // and this release's tag (best-effort; the release body is just
            // download/install instructions so we don't surface it).
            if (result.UpdateAvailable && !string.IsNullOrEmpty(tag)) {
                try {
                    result.Commits = await FetchCommitsAsync(http, tag!, ct);
                } catch (Exception ex) {
                    _logger.LogDebug(ex, "Update changelog fetch failed");
                }
            }
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

    /// <summary>Pick the architecture-matched .deb asset (polaris_*_&lt;arch&gt;.deb)
    /// from a GitHub release JSON element. Returns (null,null,0) when none.
    /// Shared by the latest-release check and the releases-list (rollback).</summary>
    private static (string? name, string? url, long size) PickArchAsset(JsonElement release) {
        if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array) {
            foreach (var a in assets.EnumerateArray()) {
                var name = a.TryGetProperty("name", out var an) ? an.GetString() : null;
                if (string.IsNullOrEmpty(name)) continue;
                if (name.EndsWith($"_{DpkgArch}.deb", StringComparison.OrdinalIgnoreCase)
                        && name.StartsWith("polaris_", StringComparison.OrdinalIgnoreCase)) {
                    var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    var size = a.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                    return (name, url, size);
                }
            }
        }
        return (null, null, 0);
    }

    private readonly object _releasesCacheLock = new();
    private List<UpdateRelease>? _cachedReleases;
    private DateTime _releasesCachedAtUtc = DateTime.MinValue;

    /// <summary>List recent GitHub releases (cached 30 min) with this host's
    /// architecture asset resolved, so the UI can offer a rollback (or forward
    /// reinstall) to any of them. Each entry is tagged <c>relation</c>
    /// (newer/current/older vs. the running version) and <c>installable</c>
    /// (an arch-matched .deb exists). Never throws; on error returns an empty
    /// list. Off a .deb install returns empty (the feature is hidden).</summary>
    public async Task<List<UpdateRelease>> ListReleasesAsync(int max, bool force, CancellationToken ct) {
        if (!IsSupported) return new();
        if (max <= 0) max = 15;
        if (max > 100) max = 100;

        lock (_releasesCacheLock) {
            if (!force && _cachedReleases != null
                    && DateTime.UtcNow - _releasesCachedAtUtc < CacheTtl)
                return _cachedReleases;
        }

        var list = new List<UpdateRelease>();
        try {
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(15);
            var url = $"https://api.github.com/repos/{Repo}/releases?per_page={max}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("NINA.Polaris-Updater");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return list;   // don't cache a failure

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;

            var current = CurrentVersion;
            foreach (var rel in doc.RootElement.EnumerateArray()) {
                var rawTag = rel.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
                var tag = NormalizeTag(rawTag);
                if (string.IsNullOrEmpty(tag)) continue;
                var (assetName, assetUrl, assetSize) = PickArchAsset(rel);

                string relation = "older";
                bool isCurrent = false;
                if (Version.TryParse(tag, out var v)) {
                    var cmp = v.CompareTo(current);
                    isCurrent = cmp == 0;
                    relation = cmp > 0 ? "newer" : (cmp == 0 ? "current" : "older");
                } else if (string.Equals(tag, CurrentVersionShort, StringComparison.OrdinalIgnoreCase)) {
                    isCurrent = true; relation = "current";
                }

                list.Add(new UpdateRelease {
                    Tag = tag,
                    Name = rel.TryGetProperty("name", out var n) ? n.GetString() : rawTag,
                    PublishedAt = rel.TryGetProperty("published_at", out var p) ? p.GetString() : null,
                    HtmlUrl = rel.TryGetProperty("html_url", out var h) ? h.GetString() : null,
                    Prerelease = rel.TryGetProperty("prerelease", out var pr) && pr.ValueKind == JsonValueKind.True,
                    AssetName = assetName,
                    AssetUrl = assetUrl,
                    AssetSize = assetSize,
                    Relation = relation,
                    IsCurrent = isCurrent,
                    Installable = !string.IsNullOrEmpty(assetUrl)
                });
            }
            lock (_releasesCacheLock) { _cachedReleases = list; _releasesCachedAtUtc = DateTime.UtcNow; }
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Release list fetch failed");
        }
        return list;
    }

    /// <summary>List commits between the installed version's tag and the
    /// release's <paramref name="headTag"/> via the GitHub compare API,
    /// newest first. Returns an empty list if the compare can't be resolved
    /// (404 = the installed build has no matching tag, e.g. a dev build).
    /// <para>The base tag is derived from the running version. This is the
    /// fiddly bit: the release tags are 3-part (<c>v0.84.8</c>) but
    /// <see cref="CurrentVersion"/> comes from the assembly version, which
    /// .NET normalises to 4 parts (<c>0.84.8.0</c>). A naive <c>"v" +
    /// CurrentVersion</c> yields <c>v0.84.8.0</c>, which doesn't exist as a
    /// tag → 404 → "changelog unavailable". So we try several candidate base
    /// tags (4-part and the trailing-zero-trimmed 3-/2-part forms) and use
    /// the first the compare API resolves.</para></summary>
    private async Task<List<UpdateCommit>> FetchCommitsAsync(HttpClient http, string headTag, CancellationToken ct) {
        var prefix = headTag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? "v" : "";

        foreach (var baseTag in CandidateBaseTags(prefix, CurrentVersion)) {
            if (string.Equals(baseTag, headTag, StringComparison.OrdinalIgnoreCase))
                continue;
            var commits = await TryCompareAsync(http, baseTag, headTag, ct);
            if (commits != null) return commits;   // resolved (may be empty)
        }
        return new();   // no candidate base tag existed on the remote
    }

    /// <summary>Candidate base-tag spellings for the running version, most-
    /// specific first: 4-part (v0.84.8.0), then trailing-zero-trimmed forms
    /// (v0.84.8, v0.84). De-duplicated, prefix applied.</summary>
    public static IEnumerable<string> CandidateBaseTags(string prefix, Version v) {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string Tag(string body) => prefix + body;

        var full = v.ToString();                                   // 0.84.8.0
        var threePart = $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}"; // 0.84.8
        var twoPart = $"{v.Major}.{v.Minor}";                      // 0.84

        foreach (var body in new[] { full, threePart, twoPart }) {
            var tag = Tag(body);
            if (seen.Add(tag)) yield return tag;
        }
    }

    /// <summary>Run one GitHub compare. Returns the parsed (filtered) commit
    /// list when the base..head pair resolves, or <c>null</c> when the API
    /// can't resolve it (e.g. 404 because the base tag doesn't exist) so the
    /// caller can fall back to the next candidate.</summary>
    private async Task<List<UpdateCommit>?> TryCompareAsync(
            HttpClient http, string baseTag, string headTag, CancellationToken ct) {
        var url = $"https://api.github.com/repos/{Repo}/compare/{baseTag}...{headTag}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("NINA.Polaris-Updater");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("commits", out var commits)
                || commits.ValueKind != JsonValueKind.Array)
            return new();

        var list = new List<UpdateCommit>();
        foreach (var c in commits.EnumerateArray()) {
            var sha = c.TryGetProperty("sha", out var s) ? (s.GetString() ?? "") : "";
            var message = c.TryGetProperty("commit", out var cm)
                && cm.TryGetProperty("message", out var msg) ? (msg.GetString() ?? "") : "";
            if (string.IsNullOrWhiteSpace(message)) continue;
            // Skip the automated "Bump version to x" commits — noise to the user.
            if (message.StartsWith("Bump version to", StringComparison.OrdinalIgnoreCase)) continue;

            var lines = message.Replace("\r\n", "\n").Split('\n');
            var subject = lines[0].Trim();
            // Body = everything after the subject, minus trailer/boilerplate
            // lines (co-author + the Claude Code generation footer).
            var bodyLines = lines.Skip(1)
                .Where(l => !l.TrimStart().StartsWith("Co-Authored-By:", StringComparison.OrdinalIgnoreCase)
                         && !l.TrimStart().StartsWith("Co-authored-by:", StringComparison.OrdinalIgnoreCase)
                         && !l.Contains("Generated with", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var body = string.Join("\n", bodyLines).Trim();
            list.Add(new UpdateCommit(sha.Length >= 7 ? sha[..7] : sha, subject, body));
        }
        // GitHub returns base..head oldest-first; show newest changes on top.
        list.Reverse();
        return list;
    }

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

        var (dlOk, dlErr) = await DownloadAndStageAsync(check.AssetUrl!, check.AssetName, ct);
        if (!dlOk) return (false, dlErr);

        // Hand off to the on-demand updater unit (shared with the offline
        // sideload path below).
        return await LaunchUpdaterUnitAsync();
    }

    /// <summary>
    /// Install a SPECIFIC release by tag — the rollback (or forward-reinstall)
    /// path. The asset URL is resolved server-side from the releases list (never
    /// taken from the caller), then downloaded + installed via the same machinery
    /// as <see cref="InstallAsync"/>. There is intentionally NO "must be newer"
    /// gate: the installer runs apt with <c>--allow-downgrades</c>, so installing
    /// an older .deb is exactly how the rollback works.
    /// </summary>
    public async Task<(bool ok, string? error)> InstallVersionAsync(string tag, CancellationToken ct) {
        if (!IsSupported) return (false, "Self-update is only available on a Linux .deb install.");
        if (string.IsNullOrWhiteSpace(tag)) return (false, "No version specified.");

        var wanted = NormalizeTag(tag.Trim());
        var releases = await ListReleasesAsync(max: 100, force: true, ct);
        var target = releases.FirstOrDefault(r =>
            string.Equals(r.Tag, wanted, StringComparison.OrdinalIgnoreCase));
        if (target == null)
            return (false, $"Version {wanted} was not found in the recent releases.");
        if (string.IsNullOrEmpty(target.AssetUrl))
            return (false, $"Version {wanted} has no {DpkgArch} package to install.");

        var (dlOk, dlErr) = await DownloadAndStageAsync(target.AssetUrl!, target.AssetName, ct);
        if (!dlOk) return (false, dlErr);

        _logger.LogInformation("Rollback/reinstall: staging {Asset} for version {Tag}", target.AssetName, wanted);
        return await LaunchUpdaterUnitAsync();
    }

    /// <summary>Download a release asset to the fixed staging path the updater
    /// unit reads. Shared by the latest-update, version-targeted (rollback), and
    /// — indirectly — offline paths.
    /// <para>The download is deliberately NOT tied to the request abort token: a
    /// multi-tens-of-MB .deb over a slow SBC/mobile link easily outlasts the
    /// browser's fetch timeout, and if the client aborts we do not want to kill
    /// an in-progress install (it left the staged .deb half-written). Bound it by
    /// its own 10-minute timeout instead.</para></summary>
    private async Task<(bool ok, string? error)> DownloadAndStageAsync(string assetUrl, string? assetName, CancellationToken ct) {
        using var dlCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var dlToken = dlCts.Token;
        try {
            Directory.CreateDirectory(CacheDir);
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromMinutes(10);
            using var dl = new HttpRequestMessage(HttpMethod.Get, assetUrl);
            dl.Headers.UserAgent.ParseAdd("NINA.Polaris-Updater");
            using var resp = await http.SendAsync(dl, HttpCompletionOption.ResponseHeadersRead, dlToken);
            if (!resp.IsSuccessStatusCode)
                return (false, $"Download failed: HTTP {(int)resp.StatusCode}");
            await using (var fs = File.Create(DebStagePath))
                await resp.Content.CopyToAsync(fs, dlToken);
            _logger.LogInformation("Update: downloaded {Asset} ({Bytes} bytes) to {Path}",
                assetName, new FileInfo(DebStagePath).Length, DebStagePath);
            return (true, null);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Update download failed");
            try { File.Delete(DebStagePath); } catch { }
            return (false, "Download failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Offline / "relay" install. The SBC has no internet but the operator's
    /// phone/tablet does, so the browser downloads the architecture-matched .deb
    /// from GitHub over its own (4G/5G) link and POSTs the bytes here. We stage
    /// them to the same path the updater unit reads and hand off to the same
    /// install machinery as <see cref="InstallAsync"/>.
    ///
    /// <para>Because the package no longer comes from a server-resolved GitHub
    /// URL, integrity is verified before anything privileged runs:
    /// the browser also reads the asset's <c>sha256</c> digest from the GitHub
    /// API (over TLS) and passes it as <paramref name="expectedSha256Hex"/>; we
    /// recompute the hash of the received bytes and refuse to install on a
    /// mismatch. As a second gate, <c>dpkg-deb</c> must report the package name
    /// <c>polaris</c> with a version newer than the running one. So a tampered
    /// upload (wrong bytes) or an unrelated/old package is rejected even though
    /// the SBC itself never reached GitHub.</para>
    /// </summary>
    public async Task<(bool ok, string? error)> InstallFromUploadAsync(
            Stream deb, long expectedSize, string? expectedSha256Hex, CancellationToken ct,
            bool allowDowngrade = false) {
        if (!IsSupported) return (false, "Self-update is only available on a Linux .deb install.");
        if (string.IsNullOrWhiteSpace(expectedSha256Hex))
            return (false, "Missing the expected SHA-256 digest — cannot verify the package.");

        var wantHex = expectedSha256Hex.Trim();
        if (wantHex.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            wantHex = wantHex["sha256:".Length..];
        wantHex = wantHex.ToLowerInvariant();

        // 1. Stream the upload to the staging path, hashing + counting as we go
        //    so an oversized or corrupt transfer fails without buffering it all
        //    in memory (the .deb is ~80 MB on an SBC with little RAM).
        long written = 0;
        string gotHex;
        try {
            Directory.CreateDirectory(CacheDir);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buf = new byte[128 * 1024];
            await using (var fs = File.Create(DebStagePath)) {
                int n;
                while ((n = await deb.ReadAsync(buf, ct)) > 0) {
                    written += n;
                    // Guard against a runaway upload: cap at the declared size
                    // (+1 MB slack) so a lying Content-Length can't fill the disk.
                    if (expectedSize > 0 && written > expectedSize + 1024 * 1024) {
                        throw new InvalidOperationException("Upload exceeded the declared size.");
                    }
                    sha.AppendData(buf, 0, n);
                    await fs.WriteAsync(buf.AsMemory(0, n), ct);
                }
            }
            gotHex = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        } catch (Exception ex) {
            try { File.Delete(DebStagePath); } catch { }
            _logger.LogWarning(ex, "Update upload staging failed");
            return (false, "Upload failed: " + ex.Message);
        }

        // 2. Integrity: byte count + SHA-256 must match what the browser read
        //    from the GitHub API.
        if (expectedSize > 0 && written != expectedSize) {
            try { File.Delete(DebStagePath); } catch { }
            return (false, $"Size mismatch: received {written} bytes, expected {expectedSize}.");
        }
        if (!string.Equals(gotHex, wantHex, StringComparison.Ordinal)) {
            try { File.Delete(DebStagePath); } catch { }
            _logger.LogWarning("Update upload SHA-256 mismatch (got {Got}, want {Want})", gotHex, wantHex);
            return (false, "Checksum mismatch — the uploaded package is corrupt or not the genuine release. Aborted.");
        }

        // 3. dpkg sanity: must be the 'polaris' package; version newer than ours
        //    UNLESS this is an explicit rollback (allowDowngrade).
        var (sane, sanityErr) = await VerifyDebPackageAsync(DebStagePath, allowDowngrade, ct);
        if (!sane) {
            try { File.Delete(DebStagePath); } catch { }
            return (false, sanityErr);
        }

        _logger.LogInformation("Update: sideloaded {Bytes} bytes (sha256 ok), launching installer.", written);
        return await LaunchUpdaterUnitAsync();
    }

    /// <summary>Read the staged .deb's control fields with <c>dpkg-deb</c> and
    /// confirm it is the <c>polaris</c> package at a version newer than the one
    /// running. Defence-in-depth behind the SHA-256 check: stops an authenticated
    /// client sideloading an unrelated or downgrade package.</summary>
    private async Task<(bool ok, string? error)> VerifyDebPackageAsync(string path, bool allowDowngrade, CancellationToken ct) {
        try {
            var pkg = (await RunCaptureAsync("dpkg-deb", new[] { "-f", path, "Package" }, ct)).Trim();
            var ver = (await RunCaptureAsync("dpkg-deb", new[] { "-f", path, "Version" }, ct)).Trim();
            if (!string.Equals(pkg, "polaris", StringComparison.OrdinalIgnoreCase))
                return (false, $"Uploaded package is '{pkg}', not 'polaris'. Aborted.");
            // Debian versions can carry an epoch / revision; take the upstream
            // numeric core for a System.Version compare (best-effort). The
            // "must be newer" gate is skipped for an explicit rollback, where
            // installing an OLDER version is the whole point.
            if (!allowDowngrade) {
                var core = new string(ver.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
                if (Version.TryParse(core, out var v) && v <= CurrentVersion)
                    return (false, $"Uploaded version {ver} is not newer than the installed {CurrentVersion}. Aborted.");
            }
            return (true, null);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "dpkg-deb verification failed");
            return (false, "Could not verify the package with dpkg-deb: " + ex.Message);
        }
    }

    /// <summary>Run a process and return its stdout, throwing on a non-zero
    /// exit. Used for the short, bounded dpkg-deb field reads.</summary>
    private static async Task<string> RunCaptureAsync(string file, string[] args, CancellationToken ct) {
        var psi = new ProcessStartInfo {
            FileName = file, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {file}");
        using var to = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var link = CancellationTokenSource.CreateLinkedTokenSource(ct, to.Token);
        var stdout = await proc.StandardOutput.ReadToEndAsync(link.Token);
        await proc.WaitForExitAsync(link.Token);
        if (proc.ExitCode != 0) {
            var err = await proc.StandardError.ReadToEndAsync(CancellationToken.None);
            throw new InvalidOperationException($"{file} exited {proc.ExitCode}: {err}".Trim());
        }
        return stdout;
    }

    /// <summary>Start the on-demand updater unit. The polaris user is authorized
    /// to start exactly this unit, passwordless, by 50-polaris-update.rules
    /// (manage-units → polaris-self-update.service). The unit runs apt in its own
    /// cgroup, so the polaris.service restart the install triggers won't kill it.
    /// Shared by the online (<see cref="InstallAsync"/>) and offline-sideload
    /// (<see cref="InstallFromUploadAsync"/>) paths once the .deb is staged.</summary>
    private async Task<(bool ok, string? error)> LaunchUpdaterUnitAsync() {
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

            // Not linked to a request token: once the .deb is staged, launching
            // the install must not be undone by a client that has already
            // navigated away / timed out.
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

    /// <summary>Lightweight, internet-free host facts the browser needs to drive
    /// the offline-sideload flow: whether self-update applies here, the running
    /// version, and the dpkg architecture to match the right release asset.
    /// Unlike <see cref="CheckAsync"/> this never touches the network.</summary>
    public UpdateLocalInfo LocalInfo() => new() {
        Supported = IsSupported,
        CurrentVersion = CurrentVersionShort,
        Arch = DpkgArch,
        Repo = Repo
    };
}

/// <summary>Offline host facts for the browser-relay update flow (no network).</summary>
public class UpdateLocalInfo {
    public bool Supported { get; set; }
    public string CurrentVersion { get; set; } = "";
    public string Arch { get; set; } = "";
    public string Repo { get; set; } = "";
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
    /// <summary>Commits between the installed version's tag and the release
    /// tag, newest first. The UI shows these as the changelog instead of the
    /// release body (which is download/install instructions). Empty when the
    /// compare can't be resolved (e.g. the installed build has no tag).</summary>
    public List<UpdateCommit> Commits { get; set; } = new();
}

/// <summary>One commit in the update changelog. <see cref="Subject"/> is the
/// first line of the message; <see cref="Body"/> is the rest (trailers like
/// Co-Authored-By stripped).</summary>
public record UpdateCommit(string Sha, string Subject, string Body);

/// <summary>One GitHub release in the rollback/version-history list, with the
/// architecture-matched .deb resolved for this host. <see cref="Relation"/> is
/// "newer" / "current" / "older" vs. the running version; <see cref="Installable"/>
/// is false when no arch-matched asset exists (the row is shown but disabled).</summary>
public class UpdateRelease {
    public string Tag { get; set; } = "";
    public string? Name { get; set; }
    public string? PublishedAt { get; set; }
    public string? HtmlUrl { get; set; }
    public bool Prerelease { get; set; }
    public string? AssetName { get; set; }
    public string? AssetUrl { get; set; }
    public long AssetSize { get; set; }
    public string Relation { get; set; } = "older";
    public bool IsCurrent { get; set; }
    public bool Installable { get; set; }
}
