using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace NINA.Polaris.Services.PlateSolving;

/// <summary>
/// PlateSolve3 (PlaneWave Instruments), fast at long focal lengths and small
/// FOVs, tolerates elongated/distorted stars, works with very few stars (&lt;10).
///
/// CLI form (PlateSolve3.80):
///   PlateSolve3.exe imagefile RA_rad Dec_rad arcsec_per_pixel_x arcsec_per_pixel_y \
///                   max_minutes detection_threshold dataset_path
///
/// Hints (RA / Dec in *radians*, plus pixel scale) are required, PlateSolve3
/// does not do blind solves, so SupportsBlindSolve is false. Result is written
/// to stdout in a Match Found block; we parse the line:
///   "RA: 12h 34m 56.78s  Dec: 12d 34' 56.7\""
/// plus "Pixel size: 1.234 arcsec".
/// </summary>
public class PlateSolve3Solver : IPlateSolver {
    private readonly IConfiguration _config;
    private readonly ILogger<PlateSolve3Solver> _logger;

    public PlateSolve3Solver(IConfiguration config, ILogger<PlateSolve3Solver> logger) {
        _config = config;
        _logger = logger;
    }

    public string Id => "platesolve3";
    public string DisplayName => "PlateSolve3";
    public bool SupportsBlindSolve => false;

    public string SolverPath => _config.GetValue("PlateSolve:PlateSolve3Path", "")!;
    public string CatalogPath => _config.GetValue("PlateSolve:PlateSolve3CatalogPath", "")!;

    public bool IsAvailable => !string.IsNullOrEmpty(SolverPath) && File.Exists(SolverPath);

    public async Task<PlateSolveResult> SolveAsync(string fitsPath, PlateSolveOptions options, CancellationToken ct = default) {
        if (!IsAvailable) return PlateSolveResult.Failed("PlateSolve3 not configured (PlateSolve:PlateSolve3Path)");
        if (!File.Exists(fitsPath)) return PlateSolveResult.Failed("FITS file not found: " + fitsPath);
        if (!options.HintRa.HasValue || !options.HintDec.HasValue || options.ScaleArcsecPerPixel <= 0) {
            return PlateSolveResult.Failed("PlateSolve3 requires RA/Dec hints and pixel scale");
        }

        var raRad = options.HintRa.Value * 15.0 * Math.PI / 180.0;
        var decRad = options.HintDec.Value * Math.PI / 180.0;
        var scale = options.ScaleArcsecPerPixel.ToString(CultureInfo.InvariantCulture);
        var args = $"\"{fitsPath}\" {raRad.ToString("F6", CultureInfo.InvariantCulture)} " +
                   $"{decRad.ToString("F6", CultureInfo.InvariantCulture)} {scale} {scale} 1 5";
        if (!string.IsNullOrEmpty(CatalogPath)) args += $" \"{CatalogPath}\"";

        _logger.LogInformation("Plate solving {File} with PlateSolve3: {Args}", fitsPath, args);

        try {
            var psi = new ProcessStartInfo {
                FileName = SolverPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(SolverPath) ?? ""
            };
            using var proc = new Process { StartInfo = psi };
            proc.Start();

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);

            var timeout = TimeSpan.FromSeconds(_config.GetValue("PlateSolve:TimeoutSeconds", 120));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try { await proc.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return PlateSolveResult.Failed("PlateSolve3 timed out");
            }

            var stdout = await stdoutTask;
            _logger.LogDebug("PlateSolve3 exit: {Code}\n{Out}", proc.ExitCode, stdout);

            return ParseStdout(stdout, fitsPath);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            return PlateSolveResult.Failed(ex.Message);
        }
    }

    /// <summary>Public for unit testing, parses PlateSolve3 stdout into a result.</summary>
    public PlateSolveResult ParseStdout(string stdout, string fitsPath) {
        if (!stdout.Contains("Match Found", StringComparison.OrdinalIgnoreCase) &&
            !stdout.Contains("Solution found", StringComparison.OrdinalIgnoreCase)) {
            return PlateSolveResult.Failed("PlateSolve3 did not find a match");
        }

        var result = new PlateSolveResult { Success = true, SolverUsed = Id };

        // RA can be either "RA: 12h 34m 56.78s" or "RA: 188.5234 deg" depending on build
        var raMatch = Regex.Match(stdout, @"RA[:\s]+(\d+)h\s*(\d+)m\s*([\d.]+)s", RegexOptions.IgnoreCase);
        if (raMatch.Success) {
            var h = int.Parse(raMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            var m = int.Parse(raMatch.Groups[2].Value, CultureInfo.InvariantCulture);
            var s = double.Parse(raMatch.Groups[3].Value, CultureInfo.InvariantCulture);
            result.RaHours = h + m / 60.0 + s / 3600.0;
            result.RaDeg = result.RaHours * 15.0;
        } else {
            var raDegMatch = Regex.Match(stdout, @"RA[:\s]+([+-]?\d+\.\d+)\s*(?:deg|°)", RegexOptions.IgnoreCase);
            if (raDegMatch.Success) {
                result.RaDeg = double.Parse(raDegMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                result.RaHours = result.RaDeg / 15.0;
            }
        }

        var decMatch = Regex.Match(stdout, @"Dec[:\s]+([+-]?\d+)d\s*(\d+)['′]\s*([\d.]+)[""″]", RegexOptions.IgnoreCase);
        if (decMatch.Success) {
            var d = int.Parse(decMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            var m = int.Parse(decMatch.Groups[2].Value, CultureInfo.InvariantCulture);
            var s = double.Parse(decMatch.Groups[3].Value, CultureInfo.InvariantCulture);
            var sign = d < 0 ? -1 : 1;
            result.DecDeg = sign * (Math.Abs(d) + m / 60.0 + s / 3600.0);
        } else {
            var decDegMatch = Regex.Match(stdout, @"Dec[:\s]+([+-]?\d+\.\d+)\s*(?:deg|°)", RegexOptions.IgnoreCase);
            if (decDegMatch.Success)
                result.DecDeg = double.Parse(decDegMatch.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        var scaleMatch = Regex.Match(stdout, @"(?:Pixel size|Pixel scale|Scale)[:\s]+([\d.]+)\s*(?:arcsec|\""/px)", RegexOptions.IgnoreCase);
        if (scaleMatch.Success)
            result.ScaleArcsecPerPixel = double.Parse(scaleMatch.Groups[1].Value, CultureInfo.InvariantCulture);

        var rotMatch = Regex.Match(stdout, @"(?:Rotation|Angle|Position angle)[:\s]+([+-]?\d+\.?\d*)", RegexOptions.IgnoreCase);
        if (rotMatch.Success)
            result.RotationDeg = double.Parse(rotMatch.Groups[1].Value, CultureInfo.InvariantCulture);

        return result;
    }
}
