using System.Diagnostics;
using System.Globalization;

namespace NINA.Headless.Services;

public class PlateSolveService {
    private readonly IConfiguration _config;
    private readonly ILogger<PlateSolveService> _logger;

    public PlateSolveService(IConfiguration config, ILogger<PlateSolveService> logger) {
        _config = config;
        _logger = logger;
    }

    public string SolverPath =>
        _config.GetValue("PlateSolve:AstapPath", GetDefaultAstapPath())!;

    public bool IsAvailable => File.Exists(SolverPath);

    public async Task<PlateSolveResult> SolveAsync(string fitsPath, PlateSolveOptions options,
        CancellationToken ct = default) {
        if (!IsAvailable) {
            return PlateSolveResult.Failed("ASTAP not found at: " + SolverPath);
        }

        if (!File.Exists(fitsPath)) {
            return PlateSolveResult.Failed("FITS file not found: " + fitsPath);
        }

        var args = BuildArgs(fitsPath, options);
        _logger.LogInformation("Plate solving {File} with ASTAP: {Args}", fitsPath, args);

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

            var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = await proc.StandardError.ReadToEndAsync(ct);

            var timeout = TimeSpan.FromSeconds(
                _config.GetValue("PlateSolve:TimeoutSeconds", 120));

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            try {
                await proc.WaitForExitAsync(cts.Token);
            } catch (OperationCanceledException) {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return PlateSolveResult.Failed("Plate solve timed out");
            }

            _logger.LogDebug("ASTAP exit code: {Code}, stdout: {Out}", proc.ExitCode, stdout);

            if (proc.ExitCode != 0 && proc.ExitCode != 2) {
                return PlateSolveResult.Failed(
                    $"ASTAP failed (exit {proc.ExitCode}): {stderr.Trim()}");
            }

            return ParseIniResult(fitsPath);
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            _logger.LogError(ex, "Plate solve failed");
            return PlateSolveResult.Failed(ex.Message);
        }
    }

    private string BuildArgs(string fitsPath, PlateSolveOptions options) {
        var args = $"-f \"{fitsPath}\"";

        if (options.SearchRadiusDeg > 0 && options.HintRa.HasValue && options.HintDec.HasValue) {
            args += $" -ra {options.HintRa.Value.ToString(CultureInfo.InvariantCulture)}";
            args += $" -spd {(options.HintDec.Value + 90).ToString(CultureInfo.InvariantCulture)}";
            args += $" -r {options.SearchRadiusDeg.ToString(CultureInfo.InvariantCulture)}";
        }

        if (options.FovDeg > 0) {
            args += $" -fov {options.FovDeg.ToString(CultureInfo.InvariantCulture)}";
        }

        if (options.Downsample > 0) {
            args += $" -z {options.Downsample}";
        }

        args += " -update";

        return args;
    }

    private PlateSolveResult ParseIniResult(string fitsPath) {
        var iniPath = Path.ChangeExtension(fitsPath, ".ini");

        if (!File.Exists(iniPath)) {
            return PlateSolveResult.Failed("ASTAP .ini result file not found");
        }

        try {
            var lines = File.ReadAllLines(iniPath);
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var line in lines) {
                var eqIdx = line.IndexOf('=');
                if (eqIdx <= 0) continue;
                var key = line[..eqIdx].Trim();
                var val = line[(eqIdx + 1)..].Trim();
                dict[key] = val;
            }

            if (!dict.TryGetValue("PLTSOLVD", out var solved) ||
                !solved.Equals("T", StringComparison.OrdinalIgnoreCase)) {
                return PlateSolveResult.Failed("Plate solve did not converge");
            }

            var result = new PlateSolveResult { Success = true };

            if (dict.TryGetValue("CRVAL1", out var raStr) &&
                double.TryParse(raStr, CultureInfo.InvariantCulture, out var raDeg)) {
                result.RaDeg = raDeg;
                result.RaHours = raDeg / 15.0;
            }

            if (dict.TryGetValue("CRVAL2", out var decStr) &&
                double.TryParse(decStr, CultureInfo.InvariantCulture, out var decDeg)) {
                result.DecDeg = decDeg;
            }

            if (dict.TryGetValue("CDELT1", out var scaleStr) &&
                double.TryParse(scaleStr, CultureInfo.InvariantCulture, out var scale)) {
                result.ScaleArcsecPerPixel = Math.Abs(scale) * 3600;
            }

            if (dict.TryGetValue("CROTA1", out var rotStr) &&
                double.TryParse(rotStr, CultureInfo.InvariantCulture, out var rot)) {
                result.RotationDeg = rot;
            }

            _logger.LogInformation(
                "Plate solve succeeded: RA={Ra:F4}h, Dec={Dec:F4}°, Scale={Scale:F2}\"/px, Rot={Rot:F1}°",
                result.RaHours, result.DecDeg, result.ScaleArcsecPerPixel, result.RotationDeg);

            CleanupTempFiles(fitsPath);

            return result;
        } catch (Exception ex) {
            return PlateSolveResult.Failed("Failed to parse ASTAP result: " + ex.Message);
        }
    }

    private void CleanupTempFiles(string fitsPath) {
        var basePath = Path.ChangeExtension(fitsPath, null);
        foreach (var ext in new[] { ".ini", ".wcs" }) {
            try {
                var path = basePath + ext;
                if (File.Exists(path)) File.Delete(path);
            } catch { }
        }
    }

    private static string GetDefaultAstapPath() {
        if (OperatingSystem.IsWindows())
            return @"C:\Program Files\astap\astap_cli.exe";
        return "/usr/bin/astap_cli";
    }
}

public class PlateSolveOptions {
    public double? HintRa { get; set; }
    public double? HintDec { get; set; }
    public double SearchRadiusDeg { get; set; } = 30;
    public double FovDeg { get; set; }
    public int Downsample { get; set; } = 2;
}

public class PlateSolveResult {
    public bool Success { get; set; }
    public string? Error { get; set; }
    public double RaHours { get; set; }
    public double RaDeg { get; set; }
    public double DecDeg { get; set; }
    public double ScaleArcsecPerPixel { get; set; }
    public double RotationDeg { get; set; }

    public static PlateSolveResult Failed(string error) =>
        new() { Success = false, Error = error };
}
