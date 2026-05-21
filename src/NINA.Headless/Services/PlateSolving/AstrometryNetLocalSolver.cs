using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace NINA.Headless.Services.PlateSolving;

/// <summary>
/// Local Astrometry.net (the open-source <c>solve-field</c> tool plus its
/// index catalogs). Capable blind solver on Linux; on Windows requires
/// Cygwin or the ANSVR package. Slower than ASTAP when hints are good but
/// genuinely independent of pointing accuracy.
///
/// We invoke <c>solve-field</c> with --overwrite, ask for no plots / no
/// PNGs (we don't need them), and parse stdout for the canonical
/// "Field center: (RA,Dec) = ..." + "Field size: ..." + "Field rotation
/// angle:" lines.
/// </summary>
public class AstrometryNetLocalSolver : IPlateSolver {
    private readonly IConfiguration _config;
    private readonly ILogger<AstrometryNetLocalSolver> _logger;

    public AstrometryNetLocalSolver(IConfiguration config, ILogger<AstrometryNetLocalSolver> logger) {
        _config = config;
        _logger = logger;
    }

    public string Id => "astrometry-net-local";
    public string DisplayName => "Astrometry.net (local solve-field)";
    public bool SupportsBlindSolve => true;

    public string SolverPath => _config.GetValue("PlateSolve:SolveFieldPath", GetDefaultPath())!;

    public bool IsAvailable {
        get {
            if (string.IsNullOrEmpty(SolverPath)) return false;
            // Allow either an absolute path to solve-field or a bare command on PATH
            if (File.Exists(SolverPath)) return true;
            return Path.GetFileName(SolverPath) == SolverPath; // assume PATH lookup will work
        }
    }

    public async Task<PlateSolveResult> SolveAsync(string fitsPath, PlateSolveOptions options, CancellationToken ct = default) {
        if (!IsAvailable) return PlateSolveResult.Failed("solve-field not configured (PlateSolve:SolveFieldPath)");
        if (!File.Exists(fitsPath)) return PlateSolveResult.Failed("FITS file not found: " + fitsPath);

        var args = BuildArgs(fitsPath, options);
        _logger.LogInformation("Plate solving {File} with solve-field: {Args}", fitsPath, args);

        try {
            var psi = new ProcessStartInfo {
                FileName = SolverPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = new Process { StartInfo = psi };
            proc.Start();

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);

            var timeout = TimeSpan.FromSeconds(_config.GetValue("PlateSolve:TimeoutSeconds", 180));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try { await proc.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return PlateSolveResult.Failed("solve-field timed out");
            }

            var stdout = await stdoutTask;
            _logger.LogDebug("solve-field exit: {Code}\n{Out}", proc.ExitCode, stdout);

            return ParseStdout(stdout);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            return PlateSolveResult.Failed(ex.Message);
        }
    }

    /// <summary>Public for unit testing.</summary>
    public string BuildArgs(string fitsPath, PlateSolveOptions options) {
        var args = $"--overwrite --no-plots --no-verify --crpix-center --downsample {Math.Max(1, options.Downsample)}";
        if (options.HintRa.HasValue && options.HintDec.HasValue) {
            args += $" --ra {(options.HintRa.Value * 15).ToString(CultureInfo.InvariantCulture)}";
            args += $" --dec {options.HintDec.Value.ToString(CultureInfo.InvariantCulture)}";
            args += $" --radius {Math.Max(1, options.SearchRadiusDeg).ToString(CultureInfo.InvariantCulture)}";
        }
        if (options.ScaleArcsecPerPixel > 0) {
            // ±20% scale window
            var lo = options.ScaleArcsecPerPixel * 0.8;
            var hi = options.ScaleArcsecPerPixel * 1.2;
            args += " --scale-units arcsecperpix";
            args += $" --scale-low {lo.ToString("F3", CultureInfo.InvariantCulture)}";
            args += $" --scale-high {hi.ToString("F3", CultureInfo.InvariantCulture)}";
        }
        args += $" \"{fitsPath}\"";
        return args;
    }

    /// <summary>Public for unit testing.</summary>
    public PlateSolveResult ParseStdout(string stdout) {
        if (stdout.Contains("Did not solve", StringComparison.OrdinalIgnoreCase) ||
            stdout.Contains("not solved", StringComparison.OrdinalIgnoreCase)) {
            return PlateSolveResult.Failed("solve-field did not converge");
        }

        // "Field center: (RA,Dec) = (180.5432, +12.3456) deg."
        var center = Regex.Match(stdout,
            @"Field center:\s*\(RA,Dec\)\s*=\s*\(([+-]?\d+\.?\d*)\s*,\s*([+-]?\d+\.?\d*)\)",
            RegexOptions.IgnoreCase);
        if (!center.Success) return PlateSolveResult.Failed("Could not parse Field center line");

        var raDeg = double.Parse(center.Groups[1].Value, CultureInfo.InvariantCulture);
        var decDeg = double.Parse(center.Groups[2].Value, CultureInfo.InvariantCulture);

        var result = new PlateSolveResult {
            Success = true, SolverUsed = Id,
            RaDeg = raDeg, RaHours = raDeg / 15.0, DecDeg = decDeg
        };

        // "pixel scale 1.234 arcsec/pix"
        var scale = Regex.Match(stdout, @"pixel scale\s+([\d.]+)\s+arcsec/pix", RegexOptions.IgnoreCase);
        if (scale.Success)
            result.ScaleArcsecPerPixel = double.Parse(scale.Groups[1].Value, CultureInfo.InvariantCulture);

        // "Field rotation angle: up is 12.3 degrees E of N"
        var rot = Regex.Match(stdout, @"Field rotation angle:\s*up is\s+([+-]?\d+\.?\d*)\s+degrees",
            RegexOptions.IgnoreCase);
        if (rot.Success)
            result.RotationDeg = double.Parse(rot.Groups[1].Value, CultureInfo.InvariantCulture);

        return result;
    }

    private static string GetDefaultPath() {
        if (OperatingSystem.IsWindows()) return "";  // requires ANSVR / Cygwin
        return "/usr/bin/solve-field";
    }
}
