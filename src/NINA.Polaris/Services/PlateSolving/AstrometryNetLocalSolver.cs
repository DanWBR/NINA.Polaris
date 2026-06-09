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
using System.Globalization;
using System.Text.RegularExpressions;

namespace NINA.Polaris.Services.PlateSolving;

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
            var stderr = await stderrTask;
            _logger.LogDebug("solve-field exit: {Code}\n{Out}", proc.ExitCode, stdout);

            var result = ParseStdout(stdout);
            // A solve-field built from source (or a partial install) often
            // solves and writes the .wcs file but cannot print the
            // human-readable "Field center" summary, because that step
            // shells out to helper tools (wcsinfo etc.) that may not be on
            // PATH. stdout then lacks the line we scrape and parsing fails
            // even though the solve succeeded. Fall back to reading the
            // .wcs FITS header solve-field always writes.
            if (!result.Success) {
                var wcsPath = WcsOutputPath(fitsPath);
                var fromWcs = TryParseWcsFile(wcsPath);
                if (fromWcs != null) {
                    result = fromWcs;
                } else if (!File.Exists(wcsPath)) {
                    // No summary AND no .wcs => solve-field genuinely did
                    // not solve. Say so plainly instead of the misleading
                    // "could not parse" message.
                    result = PlateSolveResult.Failed(
                        "solve-field did not produce a solution (no .wcs written)");
                }
            }
            result.Output = PlateSolveProcessOutput.Combine(SolverPath, args, stdout, stderr, proc.ExitCode);
            return result;
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            return PlateSolveResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Deterministic path solve-field is told to write the WCS solution to
    /// (via --wcs), so the .wcs fallback reads from a known location
    /// instead of guessing solve-field's default output naming.
    /// </summary>
    public static string WcsOutputPath(string fitsPath) =>
        Path.ChangeExtension(fitsPath, ".wcs");

    /// <summary>Public for unit testing.</summary>
    public string BuildArgs(string fitsPath, PlateSolveOptions options) {
        // --wcs pins the solution file to a known path (the .wcs fallback
        // reads it). --new-fits none skips the large rewritten FITS we
        // don't need. Quote the path for spaces.
        var args = $"--overwrite --no-plots --no-verify --crpix-center "
                 + $"--wcs \"{WcsOutputPath(fitsPath)}\" --new-fits none "
                 + $"--downsample {Math.Max(1, options.Downsample)}";
        if (options.HintRa.HasValue && options.HintDec.HasValue) {
            args += $" --ra {(options.HintRa.Value * 15).ToString(CultureInfo.InvariantCulture)}";
            args += $" --dec {options.HintDec.Value.ToString(CultureInfo.InvariantCulture)}";
            args += $" --radius {Math.Max(1, options.SearchRadiusDeg).ToString(CultureInfo.InvariantCulture)}";
        }
        if (options.ScaleArcsecPerPixel > 0) {
            // ±20% scale window around the known pixel scale.
            var lo = options.ScaleArcsecPerPixel * 0.8;
            var hi = options.ScaleArcsecPerPixel * 1.2;
            args += " --scale-units arcsecperpix";
            args += $" --scale-low {lo.ToString("F3", CultureInfo.InvariantCulture)}";
            args += $" --scale-high {hi.ToString("F3", CultureInfo.InvariantCulture)}";
        } else if (options.FovDeg > 0) {
            // No pixel scale, but we know the field width in degrees
            // (from focal length + sensor). Constrain the solve by field
            // width so solve-field doesn't have to try every index scale
            // blind, which is the slowest mode and most likely to fail.
            // Slightly wider window (±30%) since FovDeg is an estimate.
            var lo = options.FovDeg * 0.7;
            var hi = options.FovDeg * 1.3;
            args += " --scale-units degwidth";
            args += $" --scale-low {lo.ToString("F4", CultureInfo.InvariantCulture)}";
            args += $" --scale-high {hi.ToString("F4", CultureInfo.InvariantCulture)}";
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

    /// <summary>
    /// Fallback path: read the WCS solution straight from the .wcs FITS
    /// header solve-field writes, instead of scraping its stdout summary.
    /// Returns a successful result, or null if the file is absent /
    /// unparseable. Public for unit testing.
    /// </summary>
    public PlateSolveResult? TryParseWcsFile(string wcsPath) {
        try {
            if (!File.Exists(wcsPath)) return null;
            var cards = ReadFitsHeaderCards(wcsPath);
            if (cards.Count == 0) return null;
            if (!cards.TryGetValue("CRVAL1", out var sRa) ||
                !cards.TryGetValue("CRVAL2", out var sDec)) return null;
            if (!double.TryParse(sRa, NumberStyles.Float, CultureInfo.InvariantCulture, out var raDeg) ||
                !double.TryParse(sDec, NumberStyles.Float, CultureInfo.InvariantCulture, out var decDeg))
                return null;

            // With --crpix-center, CRPIX is the image centre, so CRVAL is
            // the field centre, exactly what the stdout summary reports.
            raDeg = ((raDeg % 360) + 360) % 360;
            var result = new PlateSolveResult {
                Success = true, SolverUsed = Id,
                RaDeg = raDeg, RaHours = raDeg / 15.0, DecDeg = decDeg
            };

            double D(string k) => cards.TryGetValue(k, out var v) &&
                double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

            // Pixel scale and rotation from the CD matrix (deg/pixel).
            if (cards.ContainsKey("CD1_1")) {
                double cd11 = D("CD1_1"), cd12 = D("CD1_2"), cd21 = D("CD2_1"), cd22 = D("CD2_2");
                double det = cd11 * cd22 - cd12 * cd21;
                double scaleDeg = Math.Sqrt(Math.Abs(det));
                if (scaleDeg > 0) result.ScaleArcsecPerPixel = scaleDeg * 3600.0;
                // Position angle of the +Y (up) axis, E of N. atan2 on the
                // CD column matching astrometry.net's reported orientation.
                double rot = Math.Atan2(cd21, cd11) * 180.0 / Math.PI;
                result.RotationDeg = ((rot % 360) + 360) % 360;
            } else if (cards.ContainsKey("CDELT2")) {
                double cdelt2 = D("CDELT2");
                if (cdelt2 != 0) result.ScaleArcsecPerPixel = Math.Abs(cdelt2) * 3600.0;
                result.RotationDeg = D("CROTA2");
            }

            return result;
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Failed reading WCS file {Path}", wcsPath);
            return null;
        }
    }

    /// <summary>
    /// Minimal FITS-header reader: parse 80-byte ASCII cards (KEYWORD =
    /// value / comment) up to the END card. Sufficient for a header-only
    /// .wcs file; not a general FITS data reader.
    /// </summary>
    private static Dictionary<string, string> ReadFitsHeaderCards(string path) {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var bytes = File.ReadAllBytes(path);
        for (int off = 0; off + 80 <= bytes.Length; off += 80) {
            var card = System.Text.Encoding.ASCII.GetString(bytes, off, 80);
            var key = card.Length >= 8 ? card[..8].Trim() : card.Trim();
            if (key == "END") break;
            if (key.Length == 0) continue;
            // Value-indicator "= " in columns 9-10 (0-based 8-9).
            if (card.Length < 10 || card[8] != '=') continue;
            var rest = card[10..];
            var slash = rest.IndexOf('/');
            if (slash >= 0) rest = rest[..slash];
            var val = rest.Trim().Trim('\'').Trim();
            if (!dict.ContainsKey(key)) dict[key] = val;
        }
        return dict;
    }

    private static string GetDefaultPath() {
        if (OperatingSystem.IsWindows()) return "";  // requires ANSVR / Cygwin
        return "/usr/bin/solve-field";
    }
}