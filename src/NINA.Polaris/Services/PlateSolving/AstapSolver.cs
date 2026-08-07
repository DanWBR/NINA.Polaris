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
    private readonly ProfileService? _profiles;

    public AstapSolver(IConfiguration config, ILogger<AstapSolver> logger,
                       ProfileService? profiles = null) {
        _config = config;
        _logger = logger;
        _profiles = profiles;
    }

    public string Id => "astap";
    public string DisplayName => "ASTAP";
    public bool SupportsBlindSolve => true;

    public string SolverPath {
        get {
            // A path set in the UI (profile) wins, then appsettings config.
            var fromProfile = _profiles?.Active.AstapPath;
            if (!string.IsNullOrWhiteSpace(fromProfile)) return fromProfile!;
            var configured = _config.GetValue<string?>("PlateSolve:AstapPath", null);
            if (!string.IsNullOrWhiteSpace(configured)) return configured!;
            // Otherwise auto-detect: prefer an actually-existing candidate so a
            // user who installed the GUI binary as /usr/local/bin/astap (instead
            // of the headless /usr/bin/astap_cli) is still picked up.
            return ResolveAstapPath();
        }
    }

    public bool IsAvailable => !string.IsNullOrEmpty(SolverPath) && File.Exists(SolverPath);

    public async Task<PlateSolveResult> SolveAsync(string fitsPath, PlateSolveOptions options,
            CancellationToken ct = default, Action<string>? onLog = null) {
        if (!IsAvailable) return PlateSolveResult.Failed("ASTAP not found at: " + SolverPath);
        if (!File.Exists(fitsPath)) return PlateSolveResult.Failed("FITS file not found: " + fitsPath);

        var result = await SolveOnceAsync(fitsPath, options, ct, onLog);
        if (result.Success) return result;

        // Built-in blind retry. A hinted solve that detects plenty of stars but
        // finds "no solution" is almost always a bad position/scale hint — most
        // often a wrong focal length in the rig (so ASTAP searches at the wrong
        // image scale) or garbage RA/Dec metadata. A classic field like M42
        // solves trivially without hints, so drop the position + FOV constraints
        // and let ASTAP read the scale straight from the frame. This runs before
        // (and independently of) the external blind fallback in PlateSolveService.
        bool hadHints = (options.HintRa.HasValue && options.HintDec.HasValue && options.SearchRadiusDeg > 0)
                        || options.FovDeg > 0;
        if (!hadHints || ct.IsCancellationRequested) return result;

        var blind = new PlateSolveOptions {
            SearchRadiusDeg = 0,   // omit -ra/-spd/-r (search the whole sky)
            FovDeg = 0,            // omit -fov; don't trust a possibly-wrong scale
            Downsample = options.Downsample,
            ScaleArcsecPerPixel = options.ScaleArcsecPerPixel
        };
        _logger.LogInformation("ASTAP hinted solve failed; retrying blind (no position/FOV hint)");
        try { onLog?.Invoke("== hinted solve failed; retrying ASTAP blind (auto position) =="); } catch { }
        var blindResult = await SolveOnceAsync(fitsPath, blind, ct, onLog);
        if (blindResult.Success) return blindResult;
        if (ct.IsCancellationRequested) return result;

        // FIELD6-14: last resort — retry HINTED with a coarser downsample. Measured
        // on real M8 L-Ultimate subs: a marginal frame (soft focus / bright
        // nebulosity / noise) that fails at the default -z 2 solves at -z 4. Coarser
        // binning averages down the noise, flattens the nebula's high-frequency
        // structure, and makes stars more compact — recovering exactly the
        // intermittent "detected ~7 stars, no solution" failures. z=4 solved every
        // frame in the sample and doesn't hurt easy ones (they solve at any z≥2);
        // only escalate when we're already at a finer factor, and skip it if a
        // successful blind pass would have returned above. Keeps the good hint —
        // the marginal frames need coarser detection, not a wider search.
        // Escalate from the factor actually in force, not from the per-call
        // default: with a profile setting of 1 the old code compared 4 against
        // that default, built the command from the profile again, and re-ran
        // the failing solve verbatim (field log: three identical -z 1 commands
        // under a line announcing -z 4).
        var currentZ = EffectiveDownsample(options);
        var escalatedZ = EscalatedDownsample(currentZ, options.ScaleArcsecPerPixel);
        // Coarse frames retry DOWN (see the overload): accept that as progress
        // too, otherwise the last rung is skipped exactly where it is needed.
        if (escalatedZ != currentZ && currentZ >= 0) {
            var coarse = new PlateSolveOptions {
                HintRa = options.HintRa, HintDec = options.HintDec,
                SearchRadiusDeg = options.SearchRadiusDeg, FovDeg = options.FovDeg,
                ScaleArcsecPerPixel = options.ScaleArcsecPerPixel,
                Downsample = escalatedZ,
                DownsampleIsExplicit = true
            };
            _logger.LogInformation(
                "ASTAP blind solve failed too; retrying hinted at -z {Z} ({Why})", escalatedZ,
                escalatedZ < currentZ
                    ? $"frame already coarse at {options.ScaleArcsecPerPixel:F1} arcsec/px, "
                      + "keeping every pixel instead of binning the stars away"
                    : "coarser detection");
            try { onLog?.Invoke($"== retrying ASTAP hinted at downsample {escalatedZ} (marginal frame) =="); } catch { }
            var coarseResult = await SolveOnceAsync(fitsPath, coarse, ct, onLog);
            if (coarseResult.Success) return coarseResult;
        }

        // Nothing solved: keep the richer original (hinted) error, which usually has
        // the more useful ASTAP log.
        return result;
    }

    private async Task<PlateSolveResult> SolveOnceAsync(string fitsPath, PlateSolveOptions options,
            CancellationToken ct, Action<string>? onLog) {

        // ASTAP only accepts 2-D FITS images. Multi-channel inputs
        // (NAXIS=3, e.g. the RGB master produced by ChannelCombine)
        // make it exit non-zero with no useful output. Detect that
        // here, write a single-channel proxy (green plane, highest
        // SNR for star detection in most OSC + filter wheel
        // pipelines), solve the proxy, then stamp the WCS back into
        // the original multi-channel FITS.
        if (NeedsProxyForSolve(fitsPath, out var originalChannels)) {
            return await SolveViaProxyAsync(fitsPath, options,
                originalChannels, ct, onLog);
        }

        var args = BuildArgs(fitsPath, options);
        _logger.LogInformation("Plate solving {File} with ASTAP: {Args}", fitsPath, args);
        try { onLog?.Invoke($"$ {Path.GetFileName(SolverPath)} {args}"); } catch { }

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
                // Only the CancelAfter above is a timeout; a trip of the CALLER's
                // token means the operator cancelled. Rethrow so the endpoint
                // answers 499 rather than reporting a bogus timeout.
                ct.ThrowIfCancellationRequested();
                return PlateSolveResult.Failed("ASTAP timed out");
            }

            _logger.LogDebug("ASTAP exit code: {Code}, stdout: {Out}", proc.ExitCode, stdout);

            // ASTAP runs in a couple of seconds and writes everything at
            // once, so we stream the captured lines after exit rather than
            // live; enough for the UI output panel.
            if (onLog != null) {
                foreach (var line in (stdout ?? "").Replace("\r", "").Split('\n'))
                    if (line.Length > 0) onLog(line);
                foreach (var line in (stderr ?? "").Replace("\r", "").Split('\n'))
                    if (line.Length > 0) onLog("[stderr] " + line);
                onLog($"[exit {proc.ExitCode}]");
            }

            // Full process output, surfaced to the UI for every outcome.
            var output = PlateSolveProcessOutput.Combine(SolverPath, args, stdout, stderr, proc.ExitCode);

            if (proc.ExitCode != 0 && proc.ExitCode != 2) {
                // ASTAP writes almost everything to stdout, not stderr,
                // so surfacing only stderr leaves the user with a bare
                // "ASTAP failed (exit N):" and no clue why. Fold the
                // tail of stdout into the error so the UI / tests get
                // an actionable message.
                var detail = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim()
                           : !string.IsNullOrWhiteSpace(stdout) ? Tail(stdout, 1500)
                           : "(no output)";
                return new PlateSolveResult {
                    Success = false, SolverUsed = Id, Output = output,
                    Error = $"ASTAP failed (exit {proc.ExitCode}): {detail}"
                };
            }

            var result = ParseIniResult(fitsPath);
            result.Output = output;
            return result;
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
        // Downsample factor (ASTAP -z). A config value overrides the per-call
        // default so weak hardware (Pi, ASIAIR-mini-class boards) can solve a
        // big sensor far faster by binning the frame before star detection.
        // 0 = ASTAP auto-downsample (scales with image size); 1 = none;
        // 2/3/4 = fixed. Default keeps the previous behaviour (2).
        var downsample = EffectiveDownsample(options);
        if (downsample > 0)
            args += $" -z {downsample}";
        else if (downsample == 0)
            args += " -z 0"; // explicit auto-downsample

        // Point ASTAP at the star-database directory when we can locate one.
        // Running headless as the 'polaris' service user, ASTAP's own search
        // (relative to the binary / installer's HOME) often misses a database
        // installed system-wide (e.g. the V50 .deb), giving an end-of-solve
        // "no star database" failure even though the DB is present.
        var dataDir = ResolveAstapDataDir();
        if (!string.IsNullOrEmpty(dataDir))
            args += $" -d \"{dataDir}\"";

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

            // CD matrix (deg/pixel) + reference pixel. The CD matrix carries
            // parity (mirror/flip) that CROTA1 alone cannot, so the annotation
            // projector prefers it. ASTAP always writes CD1_1..CD2_2 + CRPIX1/2.
            if (dict.TryGetValue("CD1_1", out var cd11) &&
                double.TryParse(cd11, CultureInfo.InvariantCulture, out var v11) &&
                dict.TryGetValue("CD1_2", out var cd12) &&
                double.TryParse(cd12, CultureInfo.InvariantCulture, out var v12) &&
                dict.TryGetValue("CD2_1", out var cd21) &&
                double.TryParse(cd21, CultureInfo.InvariantCulture, out var v21) &&
                dict.TryGetValue("CD2_2", out var cd22) &&
                double.TryParse(cd22, CultureInfo.InvariantCulture, out var v22)) {
                result.CD11 = v11; result.CD12 = v12;
                result.CD21 = v21; result.CD22 = v22;
            }
            if (dict.TryGetValue("CRPIX1", out var cp1) &&
                double.TryParse(cp1, CultureInfo.InvariantCulture, out var vcp1))
                result.CrPix1 = vcp1;
            if (dict.TryGetValue("CRPIX2", out var cp2) &&
                double.TryParse(cp2, CultureInfo.InvariantCulture, out var vcp2))
                result.CrPix2 = vcp2;

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

    /// <summary>Locate an ASTAP executable. Returns the first candidate that
    /// exists on disk, else the conventional default (used only for the
    /// "not found at: X" error message). Covers both the headless
    /// <c>astap_cli</c> and the GUI <c>astap</c> binary (which also solves from
    /// the command line) under both <c>/usr/bin</c> and <c>/usr/local/bin</c>,
    /// since the official ASTAP .deb installs the GUI binary as
    /// <c>/usr/local/bin/astap</c> and ships no <c>astap_cli</c>.</summary>
    private static string ResolveAstapPath() {
        foreach (var c in AstapCandidates()) {
            if (File.Exists(c)) return c;
        }
        // Last resort: search PATH for either name.
        foreach (var name in OperatingSystem.IsWindows()
                     ? new[] { "astap_cli.exe", "astap.exe" }
                     : new[] { "astap_cli", "astap" }) {
            var onPath = FindOnPath(name);
            if (onPath != null) return onPath;
        }
        return GetDefaultAstapPath();
    }

    private static IEnumerable<string> AstapCandidates() {
        if (OperatingSystem.IsWindows()) {
            yield return @"C:\Program Files\astap\astap_cli.exe";
            yield return @"C:\Program Files\astap\astap.exe";
            yield return @"C:\Program Files (x86)\astap\astap_cli.exe";
            yield return @"C:\Program Files (x86)\astap\astap.exe";
        } else {
            yield return "/usr/bin/astap_cli";
            yield return "/usr/local/bin/astap_cli";
            yield return "/usr/bin/astap";
            yield return "/usr/local/bin/astap";
            yield return "/opt/astap/astap_cli";
            yield return "/opt/astap/astap";
        }
    }

    private static string? FindOnPath(string fileName) {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;
        foreach (var dir in pathEnv.Split(Path.PathSeparator)) {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try {
                var full = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(full)) return full;
            } catch { /* skip malformed PATH entries */ }
        }
        return null;
    }

    private static string GetDefaultAstapPath() {
        if (OperatingSystem.IsWindows()) return @"C:\Program Files\astap\astap_cli.exe";
        return "/usr/bin/astap_cli";
    }

    /// <summary>Locate the directory holding an ASTAP star database (the
    /// <c>*.290</c> / <c>*.1476</c> / <c>*.001</c> files of H17/H18/V50/D50/...).
    /// An explicit <c>PlateSolve:AstapDataDir</c> config wins; otherwise scan
    /// the binary's own folder and the standard system/user locations. Returns
    /// "" when nothing is found (ASTAP then falls back to its own search).</summary>
    private string ResolveAstapDataDir() {
        var fromProfile = _profiles?.Active.AstapDataDir;
        if (!string.IsNullOrWhiteSpace(fromProfile)) return fromProfile!;
        var configured = _config.GetValue<string?>("PlateSolve:AstapDataDir", null);
        if (!string.IsNullOrWhiteSpace(configured)) return configured!;

        foreach (var dir in AstapDataDirCandidates()) {
            if (DirHasStarDatabase(dir)) return dir;
        }
        return "";
    }

    private IEnumerable<string> AstapDataDirCandidates() {
        // The folder the executable lives in is where ASTAP's own installer
        // drops databases, so check it first.
        var exeDir = Path.GetDirectoryName(SolverPath);
        if (!string.IsNullOrEmpty(exeDir)) yield return exeDir;

        if (OperatingSystem.IsWindows()) {
            yield return @"C:\Program Files\astap";
            yield return @"C:\Program Files (x86)\astap";
        } else {
            yield return "/usr/share/astap";
            yield return "/usr/share/astap/data";
            yield return "/usr/local/share/astap";
            yield return "/opt/astap";
        }

        // Per-user data dir of whatever account the server runs as.
        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrEmpty(home))
            yield return Path.Combine(home, ".local", "share", "astap");
        // Common service-account home on the Pi/.deb install.
        yield return "/home/polaris/.local/share/astap";
    }

    private static bool DirHasStarDatabase(string dir) {
        try {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
            // ASTAP star-database file extensions across the H/V/D/G series.
            foreach (var pattern in new[] { "*.290", "*.1476", "*.001", "*.0_5" }) {
                if (Directory.EnumerateFiles(dir, pattern).Any()) return true;
            }
        } catch { /* unreadable dir, skip */ }
        return false;
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
    /// <summary>FIELD6-14: the coarser downsample to retry a failed solve with.
    /// At least 4 (a marginal M8 sub that failed at -z 2 solved at -z 4 in testing),
    /// and always a real step up from the current factor. 0 (ASTAP auto) is treated
    /// as "not coarse enough" and escalated to 4.</summary>
    internal static int EscalatedDownsample(int current)
        => Math.Max(4, current <= 0 ? 4 : current + 2);

    /// <summary>Sampling below which downsampling further can only destroy
    /// stars. At 4 arcsec/pixel a typical star already spans about one pixel.</summary>
    internal const double CoarseArcsecPerPixel = 4.0;

    /// <summary>The retry downsample, aware of how coarsely the frame is
    /// already sampled.
    ///
    /// The escalation above assumes the frame is finely sampled and the problem
    /// is noise or nebulosity, where averaging down helps. On an UNDERSAMPLED
    /// frame it is backwards: the stars already span about a pixel, and binning
    /// further turns them into single-pixel spikes that ASTAP cannot tell from
    /// hot pixels. Field report 2026-08-07: a live stack at 1:2 put a 2.75"/px
    /// rig at 5.5"/px, and the ladder answered by retrying at -z 4, i.e. 22"/px.
    /// Every attempt failed for an hour.
    ///
    /// When the frame is already coarse, go the other way and give ASTAP every
    /// pixel it has. A value of 0 (auto) is treated as "unknown scale" and
    /// keeps the original behaviour.</summary>
    internal static int EscalatedDownsample(int current, double arcsecPerPixel)
        => arcsecPerPixel >= CoarseArcsecPerPixel ? 1 : EscalatedDownsample(current);

    /// <summary>The downsample this call will actually run with: the profile
    /// setting, then appsettings, then the per-call value, EXCEPT when the
    /// caller marked its value explicit (the retry ladder). Shared by BuildArgs
    /// and the escalation so the two can never disagree about what -z is in
    /// force, which is how the "retrying at -z 4" line came to print above a
    /// command carrying -z 1.</summary>
    private int EffectiveDownsample(PlateSolveOptions options) {
        if (options.DownsampleIsExplicit) return options.Downsample;
        return _profiles?.Active.PlateSolveDownsample
               ?? _config.GetValue<int?>("PlateSolve:Downsample")
               ?? options.Downsample;
    }

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
            int channels, CancellationToken ct, Action<string>? onLog = null) {
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

        // Project the GREEN plane (index 1). Empirically the best single channel to
        // solve — and NOT for the SNR reason the old comment gave (green is not
        // "the highest-SNR band" on a debayered full-res frame; it's just 1 of 3).
        //
        // FIELD6-14, measured on real M8 L-Ultimate subs with ASTAP: green solves
        // where luminance, R+G+B, G+B and red-only all FAIL. The reason is the
        // TARGET, not the photon count — M8 is a bright Ha emission nebula, so the
        // RED (Ha) channel is full of bright, structured nebulosity that raises the
        // local background and buries the stars ASTAP needs. The GREEN (OIII)
        // channel carries far less of that nebula, so the star field stands out
        // clean. Averaging channels (the tempting "more photons" fix) INJECTS the
        // Ha nebula back in and makes detection worse — proven, do not "improve"
        // this to luminance. The real cure for the intermittent failures is the
        // downsample escalation in SolveAsync, not the plane choice.
        int greenIdx = Math.Min(1, channels - 1);
        var greenPlane = new ushort[planeLen];
        Array.Copy(full.Data, greenIdx * planeLen, greenPlane, 0, planeLen);

        // Log per-plane stats so a failed solve is debuggable without re-running
        // the pipeline: min / max / mean flag a plane that's too dim, clipped, or
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

        // Why a perfectly good frame can be unsolvable, and why the operator
        // needs to be told rather than left with "exit 1".
        //
        // A reduced frame does not just lose pixels, it loses STARS: binning
        // 1:2 doubles the arcsec/pixel, and on an already-undersampled rig the
        // stars fall below one pixel. ASTAP cannot centroid a single-pixel
        // spike - it is indistinguishable from a hot pixel - so hinted, blind
        // and coarse-downsample all fail on a frame that looks fine on screen.
        //
        // Field report 2026-08-07: live stack at 1:2 on a 2.75"/px rig with
        // HFR 0.94 px. Every solve failed for an hour; switching to 1:1 solved
        // instantly on the same sky. Nothing in the output said "your stacking
        // resolution is the problem", which is what this string is for.
        double coarseScale = 0;
        var scopeMeta = full.MetaData.Telescope;
        var camMeta = full.MetaData.Camera;
        if (scopeMeta.FocalLength > 0 && camMeta.PixelSizeX > 0) {
            coarseScale = 206.2648 * camMeta.PixelSizeX / scopeMeta.FocalLength;
        }
        string undersampledHint = coarseScale >= 4.0
            ? $" The frame works out to about {coarseScale:F1} arcsec/pixel, which is coarse "
              + "enough that stars span barely a pixel and star detection has little to hold "
              + "on to. If the live stack is running at a reduced stacking resolution, that "
              + "halves or quarters the sampling: try 1:1, or solve a full-resolution sub."
            : "";

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
                onLog?.Invoke($"multi-channel FITS: solving via single-channel proxy ({planeStats})");
                onLog?.Invoke($"$ {Path.GetFileName(SolverPath)} {args}");
            } catch { }

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
                    // Only the CancelAfter above is a timeout; a trip of the
                    // CALLER's token means the operator cancelled. Rethrow so the
                    // endpoint answers 499 rather than reporting a bogus timeout.
                    // (FIELD5-1 claimed "ASTAP x2" but only patched SolveOnceAsync;
                    // this proxy path — taken for every NAXIS3>1 frame, i.e. RGB
                    // masters and OSC — kept lying "timed out" on cancel.)
                    ct.ThrowIfCancellationRequested();
                    return PlateSolveResult.Failed("ASTAP timed out");
                }

                if (onLog != null) {
                    foreach (var line in (stdout ?? "").Replace("\r", "").Split('\n'))
                        if (line.Length > 0) onLog(line);
                    foreach (var line in (stderr ?? "").Replace("\r", "").Split('\n'))
                        if (line.Length > 0) onLog("[stderr] " + line);
                    onLog($"[exit {proc.ExitCode}]");
                }

                var output = PlateSolveProcessOutput.Combine(SolverPath, args, stdout, stderr, proc.ExitCode);

                if (proc.ExitCode != 0 && proc.ExitCode != 2) {
                    deleteProxyOnExit = false;
                    var detail = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim()
                               : !string.IsNullOrWhiteSpace(stdout) ? Tail(stdout, 1500)
                               : "(no output)";
                    return new PlateSolveResult {
                        Success = false, SolverUsed = Id, Output = output,
                        Error = $"ASTAP failed on multi-channel proxy " +
                            $"(exit {proc.ExitCode}, {planeStats}, proxy={proxyPath}): {detail}"
                            + undersampledHint
                    };
                }

                var result = ParseIniResult(proxyPath);
                result.Output = output;
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
        // Preferred: the CD matrix ASTAP reported in its .ini/.wcs sidecar,
        // already captured in the solve result. It carries the true parity
        // (mirror/flip); the proxy FITS itself is only WCS-updated in place
        // when ASTAP runs with -update, so reading it back is unreliable and
        // re-synthesising from (scale, rotation) drops parity — either way the
        // RA axis can end up mirrored, misprojecting every catalog star (PCC/
        // SPCC "few matches"). The solve result is the reliable source.
        if (solve.HasCdMatrix) {
            wcs = WcsHeaders.FromCdMatrix(
                solve.RaDeg != 0 ? solve.RaDeg : solve.RaHours * 15.0,
                solve.DecDeg, solve.CrPix1, solve.CrPix2,
                solve.CD11!.Value, solve.CD12!.Value,
                solve.CD21!.Value, solve.CD22!.Value, w, h);
        }
        if (wcs == null) {
            try {
                using var fs = File.OpenRead(proxyPath);
                var hdr = FITSReader.ReadHeadersOnly(fs);
                wcs = WcsHeaders.Read(hdr);
            } catch {
                // Fall through; we'll synthesise below.
            }
        }
        // Last-resort fallback: synthesise from (scale, rotation) if we have
        // no CD matrix at all. Better a slightly-wrong WCS than none — the
        // caller gets the same numerical RA/Dec either way.
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