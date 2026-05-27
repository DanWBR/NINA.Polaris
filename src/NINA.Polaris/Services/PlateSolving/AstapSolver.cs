using System.Diagnostics;
using System.Globalization;
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageData;

namespace NINA.Polaris.Services.PlateSolving;

/// <summary>
/// ASTAP solver, fastest local option, hint-driven by default. Same logic
/// that used to live inline in PlateSolveService.
/// </summary>
public class AstapSolver : IPlateSolver {
    private readonly IConfiguration _config;
    private readonly ILogger<AstapSolver> _logger;

    public AstapSolver(IConfiguration config, ILogger<AstapSolver> logger) {
        _config = config;
        _logger = logger;
    }

    public string Id => "astap";
    public string DisplayName => "ASTAP";
    public bool SupportsBlindSolve => true;

    public string SolverPath =>
        _config.GetValue("PlateSolve:AstapPath", GetDefaultAstapPath())!;

    public bool IsAvailable => !string.IsNullOrEmpty(SolverPath) && File.Exists(SolverPath);

    public async Task<PlateSolveResult> SolveAsync(string fitsPath, PlateSolveOptions options, CancellationToken ct = default) {
        if (!IsAvailable) return PlateSolveResult.Failed("ASTAP not found at: " + SolverPath);
        if (!File.Exists(fitsPath)) return PlateSolveResult.Failed("FITS file not found: " + fitsPath);

        // ASTAP only accepts 2-D FITS images. Multi-channel inputs
        // (NAXIS=3, e.g. the RGB master produced by ChannelCombine)
        // make it exit non-zero with no useful output. Detect that
        // here, write a single-channel proxy (green plane, highest
        // SNR for star detection in most OSC + filter wheel
        // pipelines), solve the proxy, then stamp the WCS back into
        // the original multi-channel FITS.
        if (NeedsProxyForSolve(fitsPath, out var originalChannels)) {
            return await SolveViaProxyAsync(fitsPath, options,
                originalChannels, ct);
        }

        var args = BuildArgs(fitsPath, options);
        _logger.LogInformation("Plate solving {File} with ASTAP: {Args}", fitsPath, args);

        try {
            using var proc = StartProcess(SolverPath, args);
            var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = await proc.StandardError.ReadToEndAsync(ct);

            var timeout = TimeSpan.FromSeconds(_config.GetValue("PlateSolve:TimeoutSeconds", 120));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            try { await proc.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return PlateSolveResult.Failed("ASTAP timed out");
            }

            _logger.LogDebug("ASTAP exit code: {Code}, stdout: {Out}", proc.ExitCode, stdout);

            if (proc.ExitCode != 0 && proc.ExitCode != 2) {
                // ASTAP writes almost everything to stdout, not stderr,
                // so surfacing only stderr leaves the user with a bare
                // "ASTAP failed (exit N):" and no clue why. Fold the
                // tail of stdout into the error so the UI / tests get
                // an actionable message.
                var detail = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim()
                           : !string.IsNullOrWhiteSpace(stdout) ? Tail(stdout, 1500)
                           : "(no output)";
                return PlateSolveResult.Failed(
                    $"ASTAP failed (exit {proc.ExitCode}): {detail}");
            }

            return ParseIniResult(fitsPath);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            _logger.LogError(ex, "ASTAP plate solve failed");
            return PlateSolveResult.Failed(ex.Message);
        }
    }

    private static Process StartProcess(string fileName, string args) {
        var psi = new ProcessStartInfo {
            FileName = fileName,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        var p = new Process { StartInfo = psi };
        p.Start();
        return p;
    }

    /// <summary>Public for unit-testing the CLI argument formatting.</summary>
    public string BuildArgs(string fitsPath, PlateSolveOptions options) {
        var args = $"-f \"{fitsPath}\"";

        if (options.SearchRadiusDeg > 0 && options.HintRa.HasValue && options.HintDec.HasValue) {
            args += $" -ra {options.HintRa.Value.ToString(CultureInfo.InvariantCulture)}";
            args += $" -spd {(options.HintDec.Value + 90).ToString(CultureInfo.InvariantCulture)}";
            args += $" -r {options.SearchRadiusDeg.ToString(CultureInfo.InvariantCulture)}";
        }
        if (options.FovDeg > 0)
            args += $" -fov {options.FovDeg.ToString(CultureInfo.InvariantCulture)}";
        if (options.Downsample > 0)
            args += $" -z {options.Downsample}";

        args += " -update";
        return args;
    }

    /// <summary>Public so unit tests can drop in a synthetic .ini next to a fake FITS path.</summary>
    public PlateSolveResult ParseIniResult(string fitsPath) {
        var iniPath = Path.ChangeExtension(fitsPath, ".ini");
        if (!File.Exists(iniPath)) return PlateSolveResult.Failed("ASTAP .ini result file not found");

        try {
            var lines = File.ReadAllLines(iniPath);
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var line in lines) {
                var eqIdx = line.IndexOf('=');
                if (eqIdx <= 0) continue;
                dict[line[..eqIdx].Trim()] = line[(eqIdx + 1)..].Trim();
            }

            if (!dict.TryGetValue("PLTSOLVD", out var solved) ||
                !solved.Equals("T", StringComparison.OrdinalIgnoreCase)) {
                return PlateSolveResult.Failed("Plate solve did not converge");
            }

            var result = new PlateSolveResult { Success = true, SolverUsed = Id };

            if (dict.TryGetValue("CRVAL1", out var raStr) &&
                double.TryParse(raStr, CultureInfo.InvariantCulture, out var raDeg)) {
                result.RaDeg = raDeg;
                result.RaHours = raDeg / 15.0;
            }
            if (dict.TryGetValue("CRVAL2", out var decStr) &&
                double.TryParse(decStr, CultureInfo.InvariantCulture, out var decDeg))
                result.DecDeg = decDeg;
            if (dict.TryGetValue("CDELT1", out var scaleStr) &&
                double.TryParse(scaleStr, CultureInfo.InvariantCulture, out var scale))
                result.ScaleArcsecPerPixel = Math.Abs(scale) * 3600;
            if (dict.TryGetValue("CROTA1", out var rotStr) &&
                double.TryParse(rotStr, CultureInfo.InvariantCulture, out var rot))
                result.RotationDeg = rot;

            _logger.LogInformation(
                "ASTAP solve: RA={Ra:F4}h, Dec={Dec:F4}°, Scale={Scale:F2}\"/px, Rot={Rot:F1}°",
                result.RaHours, result.DecDeg, result.ScaleArcsecPerPixel, result.RotationDeg);

            CleanupTempFiles(fitsPath);
            return result;
        } catch (Exception ex) {
            return PlateSolveResult.Failed("Failed to parse ASTAP result: " + ex.Message);
        }
    }

    private static void CleanupTempFiles(string fitsPath) {
        var basePath = Path.ChangeExtension(fitsPath, null);
        foreach (var ext in new[] { ".ini", ".wcs" }) {
            try {
                var path = basePath + ext;
                if (File.Exists(path)) File.Delete(path);
            } catch { }
        }
    }

    private static string GetDefaultAstapPath() {
        if (OperatingSystem.IsWindows()) return @"C:\Program Files\astap\astap_cli.exe";
        return "/usr/bin/astap_cli";
    }

    private static string Tail(string s, int maxChars) {
        s = s.Replace("\r", "").Trim();
        if (s.Length <= maxChars) return s;
        return "..." + s[^maxChars..];
    }

    /// <summary>
    /// True when the FITS at <paramref name="fitsPath"/> has more
    /// than one image plane (NAXIS=3) and therefore needs a
    /// single-channel proxy for ASTAP to swallow. Cheap: only the
    /// FITS header is parsed, not the pixel block.
    /// </summary>
    private static bool NeedsProxyForSolve(string fitsPath, out int channels) {
        channels = 1;
        try {
            using var fs = File.OpenRead(fitsPath);
            var headers = FITSReader.ReadHeadersOnly(fs);
            if (!headers.TryGetValue("NAXIS3", out var n3) ||
                !int.TryParse(n3.Value, out var ch) || ch <= 1) {
                return false;
            }
            channels = ch;
            return true;
        } catch {
            // Header read failed — let the normal solve path produce
            // the canonical error message.
            return false;
        }
    }

    /// <summary>
    /// Solve a multi-channel FITS by projecting one plane (green,
    /// the highest-SNR band in OSC + Mono-LRGB workflows) into a
    /// temp single-channel FITS, running the regular solve on the
    /// temp, then writing the resulting WCS keywords back into the
    /// original FITS so the caller's view of plate-solved state
    /// matches what the single-channel path would produce.
    /// </summary>
    private async Task<PlateSolveResult> SolveViaProxyAsync(
            string originalPath, PlateSolveOptions options,
            int channels, CancellationToken ct) {
        BaseImageData full;
        using (var fs = File.OpenRead(originalPath)) {
            full = FITSReader.Read(fs);
        }

        int w = full.Properties.Width;
        int h = full.Properties.Height;
        int planeLen = w * h;
        if (full.Data.Length < planeLen * channels) {
            return PlateSolveResult.Failed(
                $"Multi-channel FITS truncated: expected {planeLen * channels} " +
                $"pixels for {channels}×{w}×{h}, got {full.Data.Length}.");
        }

        // Channel order in the plane-sequential layout FITSWriter
        // emits is R, G, B (matches FITSReader's expectation). Pull
        // the second plane for the proxy.
        int greenIdx = Math.Min(1, channels - 1);
        var greenPlane = new ushort[planeLen];
        Array.Copy(full.Data, greenIdx * planeLen, greenPlane, 0, planeLen);

        // Log per-plane stats so a failed solve is debuggable
        // without re-running the pipeline. Min / max / mean tell us
        // immediately if the plane is too dim, too bright, or
        // truncated.
        long sum = 0;
        ushort lo = ushort.MaxValue, hi = 0;
        for (int i = 0; i < greenPlane.Length; i++) {
            var v = greenPlane[i];
            sum += v;
            if (v < lo) lo = v;
            if (v > hi) hi = v;
        }
        var planeStats = $"plane={greenIdx}/{channels} min={lo} max={hi} " +
            $"mean={(double)sum / greenPlane.Length:F1} " +
            $"({w}x{h} {full.Properties.BitDepth}-bit)";
        _logger.LogInformation("ASTAP proxy stats: {Stats}", planeStats);

        // Put the proxy in the user's temp dir, not next to the
        // source. ASTAP writes the .ini result file next to the FITS
        // it solved; dropping it into the source directory pollutes
        // the user's image library and risks colliding with rescan
        // (FrameLibrary scans recursively for *.fits / *.fit).
        var proxyPath = Path.Combine(Path.GetTempPath(),
            "astap-proxy-" + Path.GetFileNameWithoutExtension(originalPath) +
            "-" + Guid.NewGuid().ToString("N")[..8] + ".fits");
        bool deleteProxyOnExit = true;
        try {
            var proxyProps = new ImageProperties {
                Width = w,
                Height = h,
                BitDepth = full.Properties.BitDepth,
                IsBayered = false,
            };
            var proxyMeta = new ImageMetaData {
                Camera = full.MetaData.Camera,
                Telescope = full.MetaData.Telescope,
                Observer = full.MetaData.Observer,
                Target = full.MetaData.Target,
                Exposure = full.MetaData.Exposure,
                CreationTime = full.MetaData.CreationTime,
            };
            var proxy = new BaseImageData(greenPlane, proxyProps, proxyMeta);
            FITSWriter.Write(proxy, proxyPath);

            var args = BuildArgs(proxyPath, options);
            _logger.LogInformation(
                "Plate solving {File} via single-channel proxy " +
                "{Proxy} (channels={N}): {Args}",
                originalPath, proxyPath, channels, args);

            try {
                using var proc = StartProcess(SolverPath, args);
                var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
                var stderr = await proc.StandardError.ReadToEndAsync(ct);

                var timeout = TimeSpan.FromSeconds(
                    _config.GetValue("PlateSolve:TimeoutSeconds", 120));
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout);
                try { await proc.WaitForExitAsync(cts.Token); }
                catch (OperationCanceledException) {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    return PlateSolveResult.Failed("ASTAP timed out");
                }

                if (proc.ExitCode != 0 && proc.ExitCode != 2) {
                    deleteProxyOnExit = false;
                    var detail = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim()
                               : !string.IsNullOrWhiteSpace(stdout) ? Tail(stdout, 1500)
                               : "(no output)";
                    return PlateSolveResult.Failed(
                        $"ASTAP failed on multi-channel proxy " +
                        $"(exit {proc.ExitCode}, {planeStats}, proxy={proxyPath}): {detail}");
                }

                var result = ParseIniResult(proxyPath);
                if (!result.Success) return result;

                // Stamp WCS into the ORIGINAL multi-channel FITS so
                // downstream consumers (FrameLibrary rescan, PCC,
                // SlewCenter re-solve) see the solved file directly.
                // We lift the CD matrix straight out of the proxy
                // (which ASTAP just updated in place) rather than
                // re-synthesising — see StampWcsIntoOriginal.
                StampWcsIntoOriginal(originalPath, full, result, w, h, proxyPath);
                return result;
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                _logger.LogError(ex, "ASTAP proxy plate solve failed");
                return PlateSolveResult.Failed(ex.Message);
            }
        } finally {
            // Delete the proxy + ASTAP sidecars only on success.
            // On failure the path is in the error message so the
            // user can hand-inspect (open in PixInsight, run ASTAP
            // from the command line, etc).
            if (deleteProxyOnExit) {
                try { if (File.Exists(proxyPath)) File.Delete(proxyPath); } catch { }
                try {
                    var iniSibling = Path.ChangeExtension(proxyPath, ".ini");
                    if (File.Exists(iniSibling)) File.Delete(iniSibling);
                } catch { }
                try {
                    var wcsSibling = Path.ChangeExtension(proxyPath, ".wcs");
                    if (File.Exists(wcsSibling)) File.Delete(wcsSibling);
                } catch { }
            }
        }
    }

    /// <summary>
    /// Rewrite <paramref name="originalPath"/> with the same pixels
    /// and metadata it already had, plus a fresh WCS block lifted
    /// from <paramref name="proxyPath"/>. ASTAP wrote the proxy FITS
    /// in place with the <c>-update</c> flag, so the proxy already
    /// carries the canonical CD matrix; reading those headers back
    /// is more reliable than re-synthesising the CD matrix from
    /// (scale, rotation) — the latter assumes a specific
    /// orientation convention and can flip x/y under high rotations
    /// (e.g. rot ~ 90° leaves CD11 and CD22 near zero with the bulk
    /// of the scale in CD12/CD21).
    /// </summary>
    private static void StampWcsIntoOriginal(string originalPath,
            BaseImageData original, PlateSolveResult solve, int w, int h,
            string proxyPath) {
        WcsInfo? wcs = null;
        try {
            using var fs = File.OpenRead(proxyPath);
            var hdr = FITSReader.ReadHeadersOnly(fs);
            wcs = WcsHeaders.Read(hdr);
        } catch {
            // Fall through; we'll synthesise below.
        }
        // Fallback: synthesise from (scale, rotation) if we couldn't
        // recover the proxy's CD matrix for any reason. Better to
        // have a slightly-wrong WCS than no WCS at all — the caller
        // gets the same numerical RA/Dec either way.
        wcs ??= WcsHeaders.FromSolveResult(
            solve.RaDeg, solve.DecDeg,
            solve.ScaleArcsecPerPixel, solve.RotationDeg, w, h);

        var newProps = original.Properties with { Wcs = wcs };
        var stamped = new BaseImageData(original.Data, newProps,
            original.MetaData);

        // Write to a sibling temp + atomic rename so an aborted
        // write doesn't leave the original truncated.
        var tmp = originalPath + ".tmp";
        FITSWriter.Write(stamped, tmp);
        try {
            File.Replace(tmp, originalPath, destinationBackupFileName: null);
        } catch (PlatformNotSupportedException) {
            // File.Replace fails on some filesystems; fall back.
            File.Delete(originalPath);
            File.Move(tmp, originalPath);
        }
    }
}
