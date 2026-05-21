using System.Diagnostics;
using System.Globalization;

namespace NINA.Headless.Services.PlateSolving;

/// <summary>
/// ASTAP solver — fastest local option, hint-driven by default. Same logic
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
                return PlateSolveResult.Failed($"ASTAP failed (exit {proc.ExitCode}): {stderr.Trim()}");
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
}
