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

/// <summary>
/// Thin wrapper around the external <c>ffmpeg</c> binary, used to encode a
/// directory of numbered still frames into an H.264 MP4. ffmpeg is NOT bundled
/// (it is a soft <c>Recommends</c> of the .deb); when it is absent the caller
/// falls back to the self-contained GIF path, so this service only ever
/// gates/produces the MP4. Mirrors the binary-detection + Process.Start pattern
/// used by <c>SirilService</c> / <c>AstapSolver</c>.
/// </summary>
public sealed partial class FfmpegService {
    private readonly ILogger<FfmpegService> _logger;
    private string? _cached;
    private bool _probed;

    public FfmpegService(ILogger<FfmpegService> logger) => _logger = logger;

    /// <summary>Absolute path to the ffmpeg binary, or null when not installed.
    /// Probed once and cached.</summary>
    public string? BinaryPath {
        get {
            if (!_probed) { _cached = Locate(); _probed = true; }
            return _cached;
        }
    }

    public bool IsAvailable => !string.IsNullOrEmpty(BinaryPath);

    private static string? Locate() {
        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        // PATH first (apt/homebrew installs land here), then common absolutes.
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var sep = OperatingSystem.IsWindows() ? ';' : ':';
        foreach (var dir in path.Split(sep, StringSplitOptions.RemoveEmptyEntries)) {
            try { var p = Path.Combine(dir.Trim(), exe); if (File.Exists(p)) return p; } catch { }
        }
        string[] common = OperatingSystem.IsWindows()
            ? new[] { @"C:\ffmpeg\bin\ffmpeg.exe", @"C:\Program Files\ffmpeg\bin\ffmpeg.exe" }
            : new[] { "/usr/bin/ffmpeg", "/usr/local/bin/ffmpeg", "/opt/homebrew/bin/ffmpeg", "/snap/bin/ffmpeg" };
        foreach (var c in common) if (File.Exists(c)) return c;
        return null;
    }

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
}
