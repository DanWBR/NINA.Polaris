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
using System.Text;
using System.Text.RegularExpressions;

namespace NINA.Polaris.Services.External;

/// <summary>What this host's ffmpeg build can do, read out of the binary
/// itself. Probed once and kept, because it cannot change without the binary
/// changing, and <see cref="FfmpegService.Invalidate"/> covers that.</summary>
public sealed record FfmpegCapabilities {
    /// <summary>"6.1.1-3ubuntu5", or null when the banner was not recognised.</summary>
    public string? Version { get; init; }
    /// <summary>Raw <c>-encoders</c> output, for the caller to parse. Kept raw
    /// so this project stays free of any one feature's preference table.</summary>
    public string EncodersOutput { get; init; } = "";
    public string FiltersOutput { get; init; } = "";
    public DateTime ProbedUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>How a live ffmpeg run ended.</summary>
/// <param name="ExitCode">ffmpeg's exit code, or -1 when it had to be killed.</param>
/// <param name="Killed">True when it did not stop on its own and was killed.</param>
/// <param name="StderrTail">The last few kilobytes of diagnostics, progress
/// lines excluded, which is what an error message should be made of.</param>
public sealed record FfmpegRunResult(int ExitCode, bool Killed, string StderrTail);

/// <summary>
/// Wrapper around the external <c>ffmpeg</c> binary. ffmpeg is NOT bundled (it
/// is a soft <c>Recommends</c> of the .deb), so every caller has to cope with
/// it being absent: the time-lapse falls back to the self-contained GIF path,
/// and the broadcast refuses to start and says where it looked.
///
/// <para>Two very different jobs share the binary. <see cref="EncodeAsync"/> is
/// a batch converter: a directory of numbered stills in, one MP4 out, and it is
/// finished when the process is. <see cref="RunLiveAsync"/> is the opposite in
/// every respect that matters: frames arrive over stdin for as long as the
/// operator wants, nothing is on disk to restart from, and the only sign of
/// health is the progress ffmpeg prints as it goes.</para>
///
/// <para>Nothing here logs an argument list. A broadcast's arguments contain the
/// stream key, which is the credential for someone's YouTube channel, and a log
/// file travels: it is attached to issues, pasted into Discord and swept up by
/// the support bundle. Callers that want the command in a log pass a redacted
/// rendering of it themselves.</para>
/// </summary>
public sealed partial class FfmpegService {
    private readonly ILogger<FfmpegService> _logger;
    private readonly object _gate = new();
    private string? _cached;
    private bool _probed;
    private FfmpegCapabilities? _caps;
    private Task<FfmpegCapabilities?>? _capsProbe;
    private readonly Dictionary<string, bool> _encoderWorks = new(StringComparer.Ordinal);

    public FfmpegService(ILogger<FfmpegService> logger) => _logger = logger;

    /// <summary>Absolute path to the ffmpeg binary, or null when not installed.
    /// Probed once and cached.</summary>
    public string? BinaryPath {
        get {
            lock (_gate) {
                if (!_probed) { _cached = Locate(); _probed = true; }
                return _cached;
            }
        }
    }

    public bool IsAvailable => !string.IsNullOrEmpty(BinaryPath);

    /// <summary>Forget the binary and everything probed from it. For after an
    /// install: the operator following the "ffmpeg is not installed" panel
    /// should not have to restart Polaris to be believed.</summary>
    public void Invalidate() {
        lock (_gate) {
            _probed = false;
            _cached = null;
            _caps = null;
            _capsProbe = null;
            _encoderWorks.Clear();
        }
    }

    /// <summary>Every path that was tried, and whether it held a binary. This is
    /// what the Settings panel shows so the operator can see where to put an
    /// ffmpeg installed somewhere unusual, the same way Siril and ASTAP do.</summary>
    public IReadOnlyList<BinaryLocator.Candidate> EnumerateBinaryCandidates() =>
        BinaryLocator.Enumerate(null, WindowsCandidates, LinuxCandidates, MacCandidates, "ffmpeg");

    private static string? Locate() =>
        BinaryLocator.Find(null, WindowsCandidates, LinuxCandidates, MacCandidates, "ffmpeg");

    private static readonly string[] WindowsCandidates = {
        @"C:\ffmpeg\bin\ffmpeg.exe",
        @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
        @"C:\Program Files (x86)\ffmpeg\bin\ffmpeg.exe"
    };

    private static readonly string[] LinuxCandidates = {
        "/usr/bin/ffmpeg", "/usr/local/bin/ffmpeg", "/snap/bin/ffmpeg"
    };

    private static readonly string[] MacCandidates = {
        "/opt/homebrew/bin/ffmpeg", "/usr/local/bin/ffmpeg", "/usr/bin/ffmpeg"
    };

    // --- Capabilities -----------------------------------------------

    /// <summary>
    /// What the binary reports it can encode and filter. Null when ffmpeg is not
    /// installed. Two short runs, once per process; concurrent callers share the
    /// one probe rather than spawning a handful of ffmpegs at startup.
    /// </summary>
    public Task<FfmpegCapabilities?> GetCapabilitiesAsync(CancellationToken ct = default) {
        lock (_gate) {
            if (_caps != null) return Task.FromResult<FfmpegCapabilities?>(_caps);
            // Deliberately not passing ct into the shared probe: one caller
            // walking away must not cancel the probe the others are waiting on.
            _capsProbe ??= ProbeAsync();
            return _capsProbe;
        }
    }

    private async Task<FfmpegCapabilities?> ProbeAsync() {
        var bin = BinaryPath;
        if (bin == null) return null;
        try {
            // No -hide_banner on this one: the banner carries the version, and
            // asking for it separately would be a third spawn.
            var encoders = await CaptureAsync(bin, new[] { "-encoders" }).ConfigureAwait(false);
            var filters = await CaptureAsync(bin, new[] { "-hide_banner", "-filters" }).ConfigureAwait(false);
            var caps = new FfmpegCapabilities {
                Version = VersionRegex().Match(encoders) is { Success: true } m ? m.Groups[1].Value : null,
                EncodersOutput = encoders,
                FiltersOutput = filters
            };
            lock (_gate) { _caps = caps; }
            _logger.LogInformation("ffmpeg {Version} at {Path}", caps.Version ?? "(unknown version)", bin);
            return caps;
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Could not read what ffmpeg supports");
            lock (_gate) { _capsProbe = null; }   // let the next caller try again
            return null;
        }
    }

    /// <summary>Run ffmpeg for its output and nothing else. Short arguments,
    /// short timeout, no side effects.</summary>
    private static async Task<string> CaptureAsync(string bin, IReadOnlyList<string> args) {
        var psi = new ProcessStartInfo {
            FileName = bin,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        // The banner goes to stderr and the lists go to stdout; both are read so
        // neither pipe can fill and wedge the process.
        var stdout = proc.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = proc.StandardError.ReadToEndAsync(timeout.Token);
        try {
            await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("ffmpeg did not answer a capability query.");
        }
        return await stdout.ConfigureAwait(false) + "\n" + await stderr.ConfigureAwait(false);
    }

    /// <summary>
    /// Does this encoder actually work on this host?
    ///
    /// <para>Being listed by <c>-encoders</c> is not the same as being usable.
    /// A build carries every encoder it was compiled with, and whether the
    /// hardware behind one is present, driven and free is a question only the
    /// encoder can answer. On Linux a missing device node gives it away; on
    /// Windows there is nothing to look at. This machine lists
    /// <c>h264_qsv</c>, picks it, and gets "Could not open encoder before EOF"
    /// on the first frame, which for a live broadcast is a restart loop that
    /// never settles.</para>
    ///
    /// <para>So it is tried: two tenths of a second of black, encoded and
    /// thrown away. Cached, because the answer cannot change while the process
    /// lives, and <see cref="Invalidate"/> covers a driver install.</para>
    /// </summary>
    public async Task<bool> CanEncodeAsync(string encoder, CancellationToken ct = default) {
        if (string.IsNullOrWhiteSpace(encoder)) return false;
        lock (_gate) { if (_encoderWorks.TryGetValue(encoder, out var known)) return known; }

        var bin = BinaryPath;
        if (bin == null) return false;
        var ok = false;
        try {
            var psi = new ProcessStartInfo {
                FileName = bin, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            foreach (var a in new[] {
                "-hide_banner", "-loglevel", "error", "-nostdin",
                "-f", "lavfi", "-i", "color=c=black:s=320x240:d=0.2",
                "-c:v", encoder, "-f", "null", "-" }) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var stdout = proc.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = proc.StandardError.ReadToEndAsync(timeout.Token);
            try {
                await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                ok = proc.ExitCode == 0;
            } catch (OperationCanceledException) {
                // An encoder that hangs on a fifth of a second of black is no
                // more usable than one that refuses outright.
                try { proc.Kill(entireProcessTree: true); } catch { }
            }
            if (!ok) {
                _logger.LogInformation("The {Encoder} encoder is listed but not usable here: {Why}",
                    encoder, LastLine(await stderr.ConfigureAwait(false)));
            }
            _ = stdout;
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Could not test the {Encoder} encoder", encoder);
        }
        lock (_gate) { _encoderWorks[encoder] = ok; }
        return ok;
    }

    private static string LastLine(string? text) {
        if (string.IsNullOrWhiteSpace(text)) return "(it said nothing)";
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? text.Trim() : lines[^1];
    }

    // --- Live run ---------------------------------------------------

    /// <summary>
    /// Run ffmpeg with frames arriving over stdin, for as long as the caller
    /// keeps writing them.
    ///
    /// <para><paramref name="pumpFrames"/> owns the input: it writes raw frames
    /// to the stream it is handed and returns when the broadcast is over. That
    /// return is the graceful stop. Closing stdin makes ffmpeg flush, write the
    /// MP4 index and exit on its own, which is the difference between a
    /// recording that plays and a file with no moov atom. Only after
    /// <paramref name="drainTimeout"/> is the process killed.</para>
    ///
    /// <para>Cancelling <paramref name="ct"/> is the hard stop: stdin closes, a
    /// couple of seconds are allowed for the same tidy exit, then the process
    /// tree goes. The token is also cancelled for the pump when ffmpeg exits on
    /// its own, so a pump that checks it stops writing into a dead pipe.</para>
    ///
    /// <para>Never throws for a non-zero exit. A broadcast has a watchdog above
    /// it that has to decide whether to restart, and it needs the code and the
    /// diagnostics rather than an exception.</para>
    /// </summary>
    /// <param name="args">The complete argument list. Passed through
    /// <c>ArgumentList</c>, so nothing here is quoted or escaped and a path with
    /// a space in it is simply one argument.</param>
    /// <param name="onProgress">Called for every reading ffmpeg prints, on the
    /// stderr reader's thread. Keep it short and non-blocking.</param>
    /// <param name="redactedCommandForLog">What to write in the log instead of
    /// the arguments. The caller redacts, because only the caller knows which of
    /// its arguments is a secret.</param>
    /// <summary>
    /// Pull one frame from a stream and return it as a JPEG.
    ///
    /// <para>For the corner picture when it points at an RTSP camera, which
    /// is what most IP and all-sky cameras actually speak: they have no
    /// snapshot URL at all, or it is behind a login while the RTSP stream is
    /// open. HTTP cannot fetch one, so ffmpeg connects, takes a frame and
    /// leaves.</para>
    ///
    /// <para>TCP transport rather than the default UDP: over WiFi a UDP frame
    /// arrives torn often enough to be the normal case, and a torn JPEG is a
    /// corner picture full of grey blocks. One frame every few seconds is not
    /// worth the latency UDP would save.</para>
    /// </summary>
    public async Task<byte[]?> GrabFrameAsync(string url, TimeSpan timeout, CancellationToken ct = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        var bin = BinaryPath;
        if (bin == null) return null;

        var psi = new ProcessStartInfo {
            FileName = bin,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var a in GrabArgs(url)) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        if (!proc.Start()) return null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        try {
            using var ms = new MemoryStream();
            var copy = proc.StandardOutput.BaseStream.CopyToAsync(ms, linked.Token);
            var errTask = proc.StandardError.ReadToEndAsync(linked.Token);
            await copy.ConfigureAwait(false);
            await proc.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            var bytes = ms.ToArray();
            if (bytes.Length < 512) {
                var err = (await errTask.ConfigureAwait(false)) ?? "";
                _logger.LogDebug("ffmpeg returned no frame from {Url}: {Err}",
                    Redact(url), err.Length > 300 ? err[^300..] : err);
                return null;
            }
            return bytes;
        } catch (OperationCanceledException) {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return null;
        } finally {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
    }

    /// <summary>The argument list for one frame. Separated so the shape is
    /// testable without a camera on the other end.</summary>
    public static List<string> GrabArgs(string url) => new() {
        "-hide_banner", "-loglevel", "error",
        "-rtsp_transport", "tcp",
        // Give up rather than retry for ever on a camera that has gone away.
        "-rw_timeout", "4000000",
        "-i", url,
        "-frames:v", "1",
        "-q:v", "4",
        "-f", "image2",
        "-c:v", "mjpeg",
        "pipe:1",
    };

    /// <summary>An RTSP URL very often carries the camera password in it, and
    /// this one gets logged.</summary>
    public static string Redact(string url) {
        var i = url.IndexOf("://", StringComparison.Ordinal);
        if (i < 0) return url;
        var at = url.IndexOf('@', i);
        return at < 0 ? url : url[..(i + 3)] + "<credentials>" + url[at..];
    }

    public async Task<FfmpegRunResult> RunLiveAsync(
            IReadOnlyList<string> args,
            Func<Stream, CancellationToken, Task> pumpFrames,
            Action<FfmpegProgress>? onProgress = null,
            string? redactedCommandForLog = null,
            TimeSpan? drainTimeout = null,
            CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(pumpFrames);
        var bin = BinaryPath ?? throw new InvalidOperationException("ffmpeg is not installed on this host.");

        var psi = new ProcessStartInfo {
            FileName = bin,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        // Cancelled by the caller, and also the moment ffmpeg goes away, so the
        // pump is not left writing into a pipe with nothing on the other end.
        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        proc.Exited += (_, _) => { try { pumpCts.Cancel(); } catch { } };

        var tail = new StringBuilder();
        if (!proc.Start()) throw new InvalidOperationException("ffmpeg did not start.");
        _logger.LogInformation("ffmpeg live run started (pid {Pid}): {Command}",
            proc.Id, redactedCommandForLog ?? "(arguments withheld)");

        var stderrTask = ReadStderrAsync(proc, tail, onProgress);
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();   // drained so the pipe cannot fill

        try {
            await pumpFrames(proc.StandardInput.BaseStream, pumpCts.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            // Expected: either the operator stopped it or ffmpeg exited first.
        } catch (IOException ex) {
            // ffmpeg went away mid-frame. Not an error here; the exit code and
            // the diagnostics below say what actually happened.
            _logger.LogDebug(ex, "ffmpeg closed its input");
        }

        // Closing stdin is the request to finish. Everything after this is about
        // giving it enough time to, and no more.
        try { proc.StandardInput.Close(); } catch { }

        var grace = ct.IsCancellationRequested
            ? TimeSpan.FromSeconds(2)
            : drainTimeout ?? TimeSpan.FromSeconds(10);
        var killed = false;
        if (!await WaitForExitAsync(proc, grace).ConfigureAwait(false)) {
            killed = true;
            _logger.LogWarning("ffmpeg did not exit within {Seconds:0.#}s of its input closing; killing it",
                grace.TotalSeconds);
            try { proc.Kill(entireProcessTree: true); } catch { }
            await WaitForExitAsync(proc, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }

        // The last lines of stderr are the ones that say why, so they are worth
        // a moment even on a kill.
        try { await stderrTask.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { }
        try { await stdoutTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch { }

        int code;
        try { code = proc.ExitCode; } catch { code = -1; }
        return new FfmpegRunResult(killed ? -1 : code, killed, tail.ToString().Trim());
    }

    /// <summary>
    /// Read stderr to the end, splitting off the progress readings.
    ///
    /// <para>Read by hand rather than through <c>BeginErrorReadLine</c>: a
    /// broadcast has to notice within seconds that the numbers stopped arriving,
    /// and the event-based reader is happy to sit on a partial line for as long
    /// as ffmpeg has nothing to add.</para>
    /// </summary>
    private static async Task ReadStderrAsync(Process proc, StringBuilder tail, Action<FfmpegProgress>? onProgress) {
        var parser = new FfmpegProgressParser();
        var reader = proc.StandardError;
        while (true) {
            string? line;
            try { line = await reader.ReadLineAsync().ConfigureAwait(false); } catch { break; }
            if (line == null) break;
            if (parser.TryFeed(line, out var reading)) {
                // Progress output, complete or not. Either way it is not a
                // diagnostic and must not end up in the error message.
                if (reading != null && onProgress != null) { try { onProgress(reading); } catch { } }
                continue;
            }
            // Something ffmpeg wanted to say.
            lock (tail) {
                tail.AppendLine(line);
                if (tail.Length > 4000) tail.Remove(0, tail.Length - 4000);
            }
        }
    }

    private static async Task<bool> WaitForExitAsync(Process proc, TimeSpan timeout) {
        using var cts = new CancellationTokenSource(timeout);
        try { await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false); return true; }
        catch (OperationCanceledException) { return false; }
        catch { return true; }   // already gone
    }

    // --- Batch encode -----------------------------------------------

    /// <summary>Encode <paramref name="framesDir"/>/<paramref name="pattern"/>
    /// (e.g. <c>frame_%05d.png</c>) into an MP4 at <paramref name="outPath"/>.
    /// Forces even dimensions (libx264 + yuv420p requirement) and faststart.
    /// <paramref name="onFrame"/> receives the encoder's frame counter for
    /// progress. Throws if ffmpeg is missing or exits non-zero.</summary>
    public async Task EncodeAsync(string framesDir, string pattern, int fps, string outPath,
                                  Action<int>? onFrame = null, CancellationToken ct = default) {
        var bin = BinaryPath ?? throw new InvalidOperationException("ffmpeg is not installed on this host.");
        fps = Math.Clamp(fps, 1, 120);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

        var input = Path.Combine(framesDir, pattern);
        // -start_number 0: our frames are frame_00000.jpg upward.
        var args = $"-y -framerate {fps} -start_number 0 -i \"{input}\" " +
                   "-vf \"scale=trunc(iw/2)*2:trunc(ih/2)*2\" " +
                   "-c:v libx264 -pix_fmt yuv420p -crf 18 -preset medium -movflags +faststart " +
                   $"\"{outPath}\"";

        var psi = new ProcessStartInfo {
            FileName = bin,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var proc = new Process { StartInfo = psi };
        var tail = new StringBuilder();
        proc.ErrorDataReceived += (_, e) => {
            if (e.Data == null) return;
            // ffmpeg writes progress + diagnostics to stderr; parse frame= and
            // keep a tail for error reporting.
            var m = FrameRegex().Match(e.Data);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var n)) onFrame?.Invoke(n);
            tail.AppendLine(e.Data);
            if (tail.Length > 4000) tail.Remove(0, tail.Length - 4000);
        };

        proc.Start();
        proc.BeginErrorReadLine();
        _ = proc.StandardOutput.ReadToEndAsync(ct); // drain stdout so the pipe never blocks

        try {
            await proc.WaitForExitAsync(ct);
        } catch (OperationCanceledException) {
            try { proc.Kill(entireProcessTree: true); } catch { }
            try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
            throw;
        }

        if (proc.ExitCode != 0) {
            var msg = tail.ToString();
            _logger.LogWarning("ffmpeg exited {Code}: {Tail}", proc.ExitCode, msg);
            throw new InvalidOperationException(
                "ffmpeg failed" + (string.IsNullOrWhiteSpace(msg) ? "." : ": " + msg.Trim()[^Math.Min(300, msg.Trim().Length)..]));
        }
    }

    [GeneratedRegex(@"frame=\s*(\d+)")]
    private static partial Regex FrameRegex();

    [GeneratedRegex(@"ffmpeg version (\S+)")]
    private static partial Regex VersionRegex();
}
