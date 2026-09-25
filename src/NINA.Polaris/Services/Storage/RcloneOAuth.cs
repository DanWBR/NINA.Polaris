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

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using NINA.Polaris.Services.External;

namespace NINA.Polaris.Services.Storage;

/// <summary>
/// The text handling for the browser sign in, kept pure so it can be tested
/// without launching anything.
/// </summary>
public static class RcloneOAuthOutput {
    /// <summary>The link rclone prints, which points at its OWN local server
    /// (127.0.0.1:53682), not at the provider. Hitting it is what produces the
    /// provider's consent URL.</summary>
    public static string? ParseAuthLink(string? text) {
        if (string.IsNullOrEmpty(text)) return null;
        var m = Regex.Match(text, @"http://127\.0\.0\.1:\d+/auth\?[^\s""']+");
        return m.Success ? m.Value : null;
    }

    /// <summary>The token rclone prints between its paste markers.</summary>
    public static string? ParseToken(string? stdout) {
        if (string.IsNullOrEmpty(stdout)) return null;
        foreach (var raw in stdout.Split('\n')) {
            var line = raw.Trim();
            if (line.StartsWith('{') && line.EndsWith('}') && line.Contains("access_token")) {
                return line;
            }
        }
        return null;
    }

    /// <summary>
    /// The operator pastes the address their browser landed on, which is the
    /// provider's redirect to 127.0.0.1 and therefore a page that could not
    /// load on their device. All we need from it is the query string, which we
    /// replay against the listener running here.
    /// </summary>
    public static bool TryExtractCallbackQuery(string? pasted, out string query, out string error) {
        query = ""; error = "";
        var text = (pasted ?? "").Trim();
        if (text.Length == 0) { error = "Paste the address your browser ended up on."; return false; }

        // Accept a whole URL, a bare query string, or one with a leading "?".
        int q = text.IndexOf('?');
        var candidate = q >= 0 ? text[(q + 1)..] : text;
        candidate = candidate.Trim().TrimStart('?');
        // A pasted address sometimes arrives with a trailing fragment.
        int hash = candidate.IndexOf('#');
        if (hash >= 0) candidate = candidate[..hash];

        if (candidate.Length == 0) { error = "That address has no sign in result in it."; return false; }

        // A wrapped or partly selected paste comes first, because it is the
        // common mistake and "no sign in result in it" would misdescribe it.
        if (candidate.Any(c => char.IsControl(c) || c == ' ')) {
            error = "That address looks truncated or wrapped. Copy it again in one piece.";
            return false;
        }

        bool hasCode = Regex.IsMatch(candidate, @"(^|&)code=[^&]+");
        bool hasError = Regex.IsMatch(candidate, @"(^|&)error=[^&]+");
        if (hasError) {
            var m = Regex.Match(candidate, @"(^|&)error=([^&]+)");
            error = "The provider refused the sign in: " + Uri.UnescapeDataString(m.Groups[2].Value);
            return false;
        }
        if (!hasCode) {
            error = "That address has no sign in result in it. Copy the whole address, including everything after the question mark.";
            return false;
        }
        query = candidate;
        return true;
    }
}

/// <summary>
/// Drives <c>rclone authorize</c> so the operator can sign in from whatever
/// browser they already have Polaris open in.
///
/// rclone's own headless recipe is "install rclone on a second computer, run
/// authorize there, paste the token here", which is a lot to ask of someone
/// with a phone in a field. What actually happens is simpler than it looks:
/// rclone runs a little web server on the HOST at 127.0.0.1:53682, and the
/// only thing that must reach it is the provider's redirect. So Polaris runs
/// the authorize here, hands the operator the provider's consent URL, and
/// replays the address their browser lands on against that local server. One
/// copy and paste of something already on their screen, no second rclone.
///
/// When the browser IS on the host (a mini PC), even that is unnecessary: the
/// local link works directly and the flow finishes by itself.
/// </summary>
public sealed class RcloneOAuthService : IDisposable {
    public enum Phase { Idle, Starting, WaitingForUser, Completing, Done, Failed }

    private readonly RcloneService _rclone;
    private readonly ILogger<RcloneOAuthService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private Process? _proc;
    private readonly StringBuilder _stdout = new();
    private readonly StringBuilder _stderr = new();
    private CancellationTokenSource? _timeout;

    public Phase State { get; private set; } = Phase.Idle;
    public string? ProviderType { get; private set; }
    public string? LocalAuthUrl { get; private set; }
    public string? ConsentUrl { get; private set; }
    public string? Token { get; private set; }
    public string? Error { get; private set; }

    /// <summary>rclone waits for the callback forever; we do not.</summary>
    public static readonly TimeSpan SignInWindow = TimeSpan.FromMinutes(15);

    public RcloneOAuthService(RcloneService rclone, ILogger<RcloneOAuthService> logger) {
        _rclone = rclone;
        _logger = logger;
    }

    // Deliberately not IHttpClientFactory: the whole point of the first call
    // is to read the Location header, and a factory client follows redirects,
    // so the header was gone by the time we looked and the consent URL came
    // back null. Two short-lived requests to 127.0.0.1 need no pooling.
    private static HttpClient NewClient(bool followRedirects) {
        var handler = new HttpClientHandler { AllowAutoRedirect = followRedirects };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
    }

    /// <summary>Launch the authorize and work out where to send the operator.</summary>
    public async Task<(bool Ok, string? Error)> StartAsync(string type, CancellationToken ct = default) {
        if (!RcloneArgs.IsProviderType(type)) return (false, "Unknown provider.");
        if (!RcloneArgs.NeedsBrowserSignIn(type)) return (false, "That provider does not use a browser sign in.");
        var exe = _rclone.BinaryPath;
        if (exe == null) return (false, "rclone is not installed on this host.");

        await _lock.WaitAsync(ct);
        try {
            KillLocked();
            _stdout.Clear(); _stderr.Clear();
            Token = null; Error = null; ConsentUrl = null; LocalAuthUrl = null;
            ProviderType = type;
            State = Phase.Starting;

            var psi = new ProcessStartInfo {
                FileName = exe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var a in RcloneArgs.Authorize(type)) psi.ArgumentList.Add(a);

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (_stdout) _stdout.AppendLine(e.Data); };
            proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) lock (_stderr) _stderr.AppendLine(e.Data); };
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            _proc = proc;

            // The link appears on stderr within a moment of starting.
            string? link = null;
            for (int i = 0; i < 50 && link == null; i++) {
                await Task.Delay(100, ct);
                lock (_stderr) link = RcloneOAuthOutput.ParseAuthLink(_stderr.ToString());
                if (proc.HasExited) break;
            }
            if (link == null) {
                string err;
                lock (_stderr) err = _stderr.ToString();
                KillLocked();
                State = Phase.Failed;
                Error = RcloneOutput.JoinErrors(err.Split('\n')) ?? "rclone did not offer a sign in link.";
                return (false, Error);
            }
            LocalAuthUrl = link;

            // Ask the local server where it would send a browser: that
            // redirect IS the provider's consent page, and it is the only part
            // the operator's own browser can reach.
            try {
                using var http = NewClient(followRedirects: false);
                using var resp = await http.GetAsync(link, ct);
                var loc = resp.Headers.Location?.ToString();
                if (!string.IsNullOrWhiteSpace(loc)) ConsentUrl = loc;
            } catch (Exception ex) {
                _logger.LogDebug(ex, "Could not resolve the consent URL from the local authorize server");
            }

            State = Phase.WaitingForUser;
            _timeout = new CancellationTokenSource(SignInWindow);
            _ = ExpireLaterAsync(_timeout.Token);
            return (true, null);
        } finally {
            _lock.Release();
        }
    }

    private async Task ExpireLaterAsync(CancellationToken ct) {
        try { await Task.Delay(SignInWindow, ct); } catch (OperationCanceledException) { return; }
        await _lock.WaitAsync(CancellationToken.None);
        try {
            if (State == Phase.WaitingForUser) {
                KillLocked();
                State = Phase.Failed;
                Error = "The sign in window expired. Start again.";
            }
        } finally { _lock.Release(); }
    }

    /// <summary>Replay the address the operator's browser landed on against the
    /// local listener, then collect the token rclone prints.</summary>
    public async Task<(bool Ok, string? Token, string? Error)> CompleteAsync(string pastedUrl,
                                                                             CancellationToken ct = default) {
        await _lock.WaitAsync(ct);
        try {
            if (State != Phase.WaitingForUser || _proc == null || LocalAuthUrl == null)
                return (false, null, "No sign in is in progress. Start one first.");
            var proc0 = _proc;

            // An empty paste means the browser reached the listener by itself,
            // which is what happens when Polaris is open ON the host: there is
            // nothing to replay, only a token to collect.
            bool replay = !string.IsNullOrWhiteSpace(pastedUrl);
            string query = "";
            if (replay && !RcloneOAuthOutput.TryExtractCallbackQuery(pastedUrl, out query, out var why))
                return (false, null, why);

            State = Phase.Completing;

            if (replay) {
                var baseUri = new Uri(LocalAuthUrl);
                var callback = $"{baseUri.Scheme}://{baseUri.Authority}/?{query}";
                try {
                    using var http = NewClient(followRedirects: true);
                    using var resp = await http.GetAsync(callback, ct);
                    _logger.LogDebug("rclone authorize callback replayed: {Status}", (int)resp.StatusCode);
                } catch (Exception ex) {
                    State = Phase.WaitingForUser;
                    return (false, null, "Could not hand the sign in back to rclone: " + ex.Message);
                }
            } else if (!proc0.HasExited) {
                // Nothing came back yet: the operator has not finished on the
                // provider's page. Say so instead of hanging for 30 seconds.
                State = Phase.WaitingForUser;
                return (false, null, "The sign in has not come back yet. Finish it in the browser tab that opened.");
            }

            // rclone prints the token and exits.
            var proc = proc0;
            try {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(TimeSpan.FromSeconds(30));
                await proc.WaitForExitAsync(wait.Token);
            } catch (OperationCanceledException) {
                State = Phase.WaitingForUser;
                return (false, null, "rclone did not finish the sign in. Check the address you pasted.");
            }

            string outText;
            lock (_stdout) outText = _stdout.ToString();
            var token = RcloneOAuthOutput.ParseToken(outText);
            if (token == null) {
                string err;
                lock (_stderr) err = _stderr.ToString();
                State = Phase.Failed;
                Error = RcloneOutput.JoinErrors(err.Split('\n')) ?? "rclone did not return a token.";
                CleanupLocked();
                return (false, null, Error);
            }

            Token = token;
            State = Phase.Done;
            CleanupLocked();
            return (true, token, null);
        } finally {
            _lock.Release();
        }
    }

    public async Task CancelAsync() {
        await _lock.WaitAsync();
        try { KillLocked(); State = Phase.Idle; Error = null; }
        finally { _lock.Release(); }
    }

    private void KillLocked() {
        try {
            if (_proc != null && !_proc.HasExited) _proc.Kill(entireProcessTree: true);
        } catch { /* already gone */ }
        CleanupLocked();
    }

    private void CleanupLocked() {
        try { _proc?.Dispose(); } catch { }
        _proc = null;
        try { _timeout?.Cancel(); _timeout?.Dispose(); } catch { }
        _timeout = null;
    }

    public void Dispose() {
        KillLocked();
        _lock.Dispose();
    }
}
