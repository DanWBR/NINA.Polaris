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
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NINA.Image.FileFormat.FITS;

namespace NINA.Polaris.Services.PlateSolving;

/// <summary>
/// Any command-line solver the operator installed themselves, run from an
/// executable path and an argument template set in Settings. The contract is
/// fixed: exit code 0 and a WCS solution written to the <c>{wcs}</c> path,
/// either as FITS header cards (astrometry.net and ASTAP style) or as a JSON
/// object holding the same keywords.
///
/// The template is split into arguments before the placeholders are filled
/// in and the process starts without a shell, so a path with spaces stays one
/// argument and nothing in a value can inject another command. Text inside
/// <c>[ ]</c> is kept only when every placeholder in it has a value for this
/// solve, which is how hint flags drop out of a blind solve.
/// </summary>
public class CustomCommandSolver : IPlateSolver {
    private readonly IConfiguration _config;
    private readonly ILogger<CustomCommandSolver> _logger;
    private readonly ProfileService? _profiles;

    public CustomCommandSolver(IConfiguration config, ILogger<CustomCommandSolver> logger,
                               ProfileService? profiles = null) {
        _config = config;
        _logger = logger;
        _profiles = profiles;
    }

    public const string SolverId = "custom";

    /// <summary>Placeholders the template may use. A typo in a name is caught
    /// when the settings are checked, not halfway through a night.</summary>
    public static readonly string[] Placeholders = {
        "image", "wcs", "ra", "ra_hours", "dec", "radius",
        "fov", "fov_min", "fov_max", "scale", "scale_min", "scale_max",
        "downsample", "timeout", "timeout_ms"
    };

    public string Id => SolverId;

    public string DisplayName {
        get {
            var name = _profiles?.Active.CustomSolverName;
            return string.IsNullOrWhiteSpace(name) ? "Custom solver" : name.Trim();
        }
    }

    public string? SolverPath {
        get {
            var path = _profiles?.Active.CustomSolverPath;
            return string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        }
    }

    public string Template => _profiles?.Active.CustomSolverArgs?.Trim() ?? "";

    public bool SupportsBlindSolve => _profiles?.Active.CustomSolverBlind == true;

    public bool IsAvailable => Validate() == null;

    /// <summary>Why the solver cannot run as configured, or null when it can.</summary>
    public string? Validate() => Validate(SolverPath, Template);

    internal static string? Validate(string? path, string template) {
        if (string.IsNullOrWhiteSpace(path)) return "No executable set.";
        var resolved = ResolveExecutable(path);
        if (resolved == null)
            return Path.IsPathRooted(path) || path.Contains('/') || path.Contains('\\')
                ? $"{path} does not exist."
                : $"{path} is not on the PATH.";
        if (!OperatingSystem.IsWindows()) {
            try {
                var mode = File.GetUnixFileMode(resolved);
                if ((mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
                    return $"{resolved} is not executable (chmod +x).";
            } catch { /* unreadable mode: let the start report it */ }
        }
        if (string.IsNullOrWhiteSpace(template)) return "No argument template set.";
        var groups = SplitGroups(template, out var groupError);
        if (groupError != null) return groupError;
        var used = groups.SelectMany(g => PlaceholderNames(g.Text)).ToHashSet();
        var unknown = used.Where(n => !Placeholders.Contains(n)).ToList();
        if (unknown.Count > 0) return "Unknown placeholder " + string.Join(", ", unknown.Select(n => "{" + n + "}")) + ".";
        foreach (var required in new[] { "image", "wcs" }) {
            if (!groups.Any(g => !g.Optional && PlaceholderNames(g.Text).Contains(required)))
                return "The template needs {" + required + "} outside any [ ] group.";
        }
        return null;
    }

    /// <summary>The path the OS would start: the file itself, or the first
    /// match on the PATH for a bare command name. Null when neither exists.</summary>
    internal static string? ResolveExecutable(string path) {
        path = path.Trim();
        if (Path.IsPathRooted(path) || path.Contains('/') || path.Contains('\\'))
            return File.Exists(path) ? Path.GetFullPath(path) : null;
        var exts = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : Array.Empty<string>();
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) {
            var candidate = Path.Combine(dir, path);
            if (File.Exists(candidate)) return candidate;
            foreach (var ext in exts)
                if (File.Exists(candidate + ext)) return candidate + ext;
        }
        return null;
    }

    private record struct Group(string Text, bool Optional);

    private static List<Group> SplitGroups(string template, out string? error) {
        error = null;
        var groups = new List<Group>();
        var sb = new StringBuilder();
        bool inGroup = false;
        foreach (var c in template) {
            if (c == '[') {
                if (inGroup) { error = "A [ ] group cannot hold another one."; return groups; }
                groups.Add(new Group(sb.ToString(), false));
                sb.Clear();
                inGroup = true;
            } else if (c == ']') {
                if (!inGroup) { error = "A ] has no matching [."; return groups; }
                groups.Add(new Group(sb.ToString(), true));
                sb.Clear();
                inGroup = false;
            } else {
                sb.Append(c);
            }
        }
        if (inGroup) { error = "A [ has no matching ]."; return groups; }
        groups.Add(new Group(sb.ToString(), false));
        return groups;
    }

    private static readonly Regex PlaceholderRx = new(@"\{([A-Za-z_]+)\}", RegexOptions.Compiled);

    private static IEnumerable<string> PlaceholderNames(string text) =>
        PlaceholderRx.Matches(text).Select(m => m.Groups[1].Value.ToLowerInvariant());

    /// <summary>
    /// Turn the template into the argument list for one solve. Optional groups
    /// whose placeholders lack a value are dropped first, then the text is
    /// split on whitespace (double quotes keep a token together and are
    /// removed), then each token gets its values. A placeholder with no value
    /// outside a group fails the solve with an error naming it.
    /// </summary>
    internal static List<string>? BuildArguments(string template,
            IReadOnlyDictionary<string, string?> values, out string? error) {
        var groups = SplitGroups(template, out error);
        if (error != null) return null;
        var kept = new StringBuilder();
        foreach (var g in groups) {
            if (g.Optional && PlaceholderNames(g.Text).Any(n => !values.TryGetValue(n, out var v) || v == null))
                continue;
            kept.Append(' ').Append(g.Text).Append(' ');
        }

        var args = new List<string>();
        foreach (var token in Tokenize(kept.ToString())) {
            string? missing = null;
            var filled = PlaceholderRx.Replace(token, m => {
                var name = m.Groups[1].Value.ToLowerInvariant();
                if (values.TryGetValue(name, out var v) && v != null) return v;
                missing ??= name;
                return m.Value;
            });
            if (missing != null) {
                error = "{" + missing + "} has no value for this solve; wrap it in [ ] to make it optional.";
                return null;
            }
            args.Add(filled);
        }
        return args;
    }

    private static IEnumerable<string> Tokenize(string text) {
        var sb = new StringBuilder();
        bool quoted = false, any = false;
        foreach (var c in text) {
            if (c == '"') { quoted = !quoted; any = true; continue; }
            if (!quoted && char.IsWhiteSpace(c)) {
                if (any) { yield return sb.ToString(); sb.Clear(); any = false; }
                continue;
            }
            sb.Append(c);
            any = true;
        }
        if (any) yield return sb.ToString();
    }

    /// <summary>The values one solve offers the template. Hints the solve does
    /// not have are null, so groups that use them drop out.</summary>
    internal static Dictionary<string, string?> Values(string fitsPath, string wcsPath,
            PlateSolveOptions options, int timeoutSeconds) {
        static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
        bool hinted = options.HintRa.HasValue && options.HintDec.HasValue;
        bool fov = options.FovDeg > 0, scale = options.ScaleArcsecPerPixel > 0;
        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) {
            ["image"] = fitsPath,
            ["wcs"] = wcsPath,
            ["ra"] = hinted ? F(options.HintRa!.Value * 15.0) : null,
            ["ra_hours"] = hinted ? F(options.HintRa!.Value) : null,
            ["dec"] = hinted ? F(options.HintDec!.Value) : null,
            ["radius"] = hinted ? F(Math.Max(1, options.SearchRadiusDeg)) : null,
            ["fov"] = fov ? F(options.FovDeg) : null,
            ["fov_min"] = fov ? F(options.FovDeg * 0.7) : null,
            ["fov_max"] = fov ? F(options.FovDeg * 1.3) : null,
            ["scale"] = scale ? F(options.ScaleArcsecPerPixel) : null,
            ["scale_min"] = scale ? F(options.ScaleArcsecPerPixel * 0.8) : null,
            ["scale_max"] = scale ? F(options.ScaleArcsecPerPixel * 1.2) : null,
            ["downsample"] = Math.Max(1, options.Downsample).ToString(CultureInfo.InvariantCulture),
            ["timeout"] = timeoutSeconds.ToString(CultureInfo.InvariantCulture),
            ["timeout_ms"] = (timeoutSeconds * 1000L).ToString(CultureInfo.InvariantCulture),
        };
    }

    public async Task<PlateSolveResult> SolveAsync(string fitsPath, PlateSolveOptions options,
            CancellationToken ct = default, Action<string>? onLog = null) {
        var invalid = Validate();
        if (invalid != null) return PlateSolveResult.Failed($"{DisplayName}: {invalid}");
        if (!File.Exists(fitsPath)) return PlateSolveResult.Failed("FITS file not found: " + fitsPath);

        var exe = SolverPath!;
        var timeoutSeconds = _config.GetValue("PlateSolve:TimeoutSeconds", 180);
        // A fresh path per solve: some solvers refuse to overwrite an existing file.
        var wcsPath = Path.Combine(Path.GetDirectoryName(fitsPath) ?? Path.GetTempPath(),
            Path.GetFileNameWithoutExtension(fitsPath) + ".custom-" + Guid.NewGuid().ToString("N")[..8] + ".wcs");
        var args = BuildArguments(Template, Values(fitsPath, wcsPath, options, timeoutSeconds), out var argError);
        if (args == null) return PlateSolveResult.Failed($"{DisplayName}: {argError}");
        var shown = string.Join(' ', args.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a));
        _logger.LogInformation("Plate solving {File} with {Name}: {Exe} {Args}", fitsPath, DisplayName, exe, shown);

        try {
            var psi = new ProcessStartInfo {
                FileName = exe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var proc = new Process { StartInfo = psi };
            var stdoutSb = new StringBuilder();
            var stderrSb = new StringBuilder();
            proc.OutputDataReceived += (_, e) => {
                if (e.Data == null) return;
                lock (stdoutSb) stdoutSb.AppendLine(e.Data);
                try { onLog?.Invoke(e.Data); } catch { /* logging is best-effort */ }
            };
            proc.ErrorDataReceived += (_, e) => {
                if (e.Data == null) return;
                lock (stderrSb) stderrSb.AppendLine(e.Data);
                try { onLog?.Invoke(e.Data); } catch { /* logging is best-effort */ }
            };
            try { onLog?.Invoke($"$ {Path.GetFileName(exe)} {shown}"); } catch { }

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            try { await proc.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) {
                try { proc.Kill(entireProcessTree: true); } catch { }
                // Only CancelAfter is a timeout; the caller's token is the
                // operator cancelling, which the endpoint answers with 499.
                ct.ThrowIfCancellationRequested();
                return PlateSolveResult.Failed($"{DisplayName} timed out after {timeoutSeconds} s");
            }
            proc.WaitForExit();

            string stdout, stderr;
            lock (stdoutSb) stdout = stdoutSb.ToString();
            lock (stderrSb) stderr = stderrSb.ToString();

            PlateSolveResult result;
            if (proc.ExitCode != 0) {
                var last = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
                result = PlateSolveResult.Failed($"{DisplayName} did not solve (exit {proc.ExitCode})"
                    + (string.IsNullOrEmpty(last) ? "" : ": " + last));
            } else if (!File.Exists(wcsPath)) {
                result = PlateSolveResult.Failed($"{DisplayName} exited 0 but wrote no WCS file to {{wcs}}");
            } else {
                var (w, h) = ImageSize(fitsPath);
                result = ParseWcs(File.ReadAllText(wcsPath, Encoding.Latin1), w, h)
                    ?? PlateSolveResult.Failed($"{DisplayName} wrote a WCS file Polaris cannot read (needs CRVAL1/2 and a CD matrix or CDELT)");
                result.SolverUsed = Id;
            }
            result.Output = PlateSolveProcessOutput.Combine(exe, shown, stdout, stderr, proc.ExitCode);
            return result;
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            return PlateSolveResult.Failed($"{DisplayName}: {ex.Message}");
        } finally {
            try { if (File.Exists(wcsPath)) File.Delete(wcsPath); } catch { /* best effort */ }
        }
    }

    private static (int W, int H) ImageSize(string fitsPath) {
        try {
            using var fs = File.OpenRead(fitsPath);
            var h = FITSReader.ReadHeadersOnly(fs);
            int Get(string k) => h.TryGetValue(k, out var c) && int.TryParse(c.Value, out var v) ? v : 0;
            return (Get("NAXIS1"), Get("NAXIS2"));
        } catch {
            return (0, 0);
        }
    }

    /// <summary>
    /// Read a WCS solution from FITS header cards (80-column records, with or
    /// without newlines) or from a JSON object carrying the same keywords.
    /// The field centre is the reported RA/Dec: when the reference pixel is
    /// not the image centre, the centre is projected through the TAN solution
    /// and becomes the new reference pixel. Null when no solution is found.
    /// </summary>
    internal static PlateSolveResult? ParseWcs(string text, int width, int height) {
        var k = text.TrimStart().StartsWith('{') ? JsonKeywords(text) : HeaderKeywords(text);
        if (k == null) return null;
        double? D(string key) => k.TryGetValue(key, out var v) ? v : null;

        if (D("CRVAL1") is not double crval1 || D("CRVAL2") is not double crval2) return null;
        if (width <= 0) width = (int)(D("IMAGEW") ?? D("NAXIS1") ?? 0);
        if (height <= 0) height = (int)(D("IMAGEH") ?? D("NAXIS2") ?? 0);
        double crpix1 = D("CRPIX1") ?? (width + 1) / 2.0;
        double crpix2 = D("CRPIX2") ?? (height + 1) / 2.0;

        double cd11, cd12, cd21, cd22;
        if (D("CD1_1") is double c11) {
            cd11 = c11; cd12 = D("CD1_2") ?? 0; cd21 = D("CD2_1") ?? 0; cd22 = D("CD2_2") ?? 0;
        } else if (D("CDELT1") is double d1 && D("CDELT2") is double d2) {
            if (D("PC1_1") is double p11) {
                cd11 = d1 * p11; cd12 = d1 * (D("PC1_2") ?? 0);
                cd21 = d2 * (D("PC2_1") ?? 0); cd22 = d2 * (D("PC2_2") ?? 1);
            } else {
                var rot = (D("CROTA2") ?? 0) * Math.PI / 180.0;
                cd11 = d1 * Math.Cos(rot); cd12 = -d2 * Math.Sin(rot);
                cd21 = d1 * Math.Sin(rot); cd22 = d2 * Math.Cos(rot);
            }
        } else {
            return null;
        }
        if (cd11 * cd22 - cd12 * cd21 == 0) return null;

        double ra = crval1, dec = crval2;
        if (width > 0 && height > 0) {
            double cx = (width + 1) / 2.0, cy = (height + 1) / 2.0;
            if (Math.Abs(cx - crpix1) > 0.5 || Math.Abs(cy - crpix2) > 0.5) {
                (ra, dec) = TanToSky(crval1, crval2, cd11 * (cx - crpix1) + cd12 * (cy - crpix2),
                                     cd21 * (cx - crpix1) + cd22 * (cy - crpix2));
                // The CD matrix is kept as is: across a field it changes only by
                // the convergence of the meridians, well under what the viewer
                // and the FOV overlays can show.
                crpix1 = cx; crpix2 = cy;
            }
        }
        ra = ((ra % 360) + 360) % 360;

        double det = cd11 * cd22 - cd12 * cd21;
        double rotation = Math.Atan2(cd21, cd11) * 180.0 / Math.PI;
        return new PlateSolveResult {
            Success = true, SolverUsed = SolverId,
            RaDeg = ra, RaHours = ra / 15.0, DecDeg = dec,
            ScaleArcsecPerPixel = Math.Sqrt(Math.Abs(det)) * 3600.0,
            RotationDeg = ((rotation % 360) + 360) % 360,
            CD11 = cd11, CD12 = cd12, CD21 = cd21, CD22 = cd22,
            CrPix1 = crpix1, CrPix2 = crpix2
        };
    }

    /// <summary>Gnomonic (TAN) deprojection: intermediate world coordinates
    /// (degrees) around the reference point to RA/Dec (degrees).</summary>
    internal static (double Ra, double Dec) TanToSky(double ra0Deg, double dec0Deg, double xiDeg, double etaDeg) {
        const double r = Math.PI / 180.0;
        double xi = xiDeg * r, eta = etaDeg * r, dec0 = dec0Deg * r;
        double den = Math.Cos(dec0) - eta * Math.Sin(dec0);
        double ra = ra0Deg * r + Math.Atan2(xi, den);
        double dec = Math.Atan2(Math.Sin(dec0) + eta * Math.Cos(dec0), Math.Sqrt(xi * xi + den * den));
        return (ra / r, dec / r);
    }

    private static Dictionary<string, double>? HeaderKeywords(string text) {
        var cards = text.Contains('\n')
            ? text.Split('\n').Select(l => l.TrimEnd('\r'))
            : Enumerable.Range(0, text.Length / 80).Select(i => text.Substring(i * 80, 80));
        var dict = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in cards) {
            var eq = card.IndexOf('=');
            var key = (eq > 0 ? card[..eq] : card.Length >= 8 ? card[..8] : card).Trim();
            if (key == "END") break;
            if (eq <= 0 || key.Length == 0 || key.Length > 8) continue;
            var value = card[(eq + 1)..];
            var slash = value.IndexOf('/');
            if (slash >= 0 && !value[..slash].Contains('\'')) value = value[..slash];
            value = value.Trim().Trim('\'').Trim();
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                dict.TryAdd(key, d);
        }
        return dict.Count > 0 ? dict : null;
    }

    private static Dictionary<string, double>? JsonKeywords(string text) {
        try {
            using var doc = JsonDocument.Parse(text);
            var obj = FindWcsObject(doc.RootElement);
            if (obj == null) return null;
            var dict = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in obj.Value.EnumerateObject()) {
                if (p.Value.ValueKind == JsonValueKind.Number) dict.TryAdd(p.Name, p.Value.GetDouble());
                else if (p.Value.ValueKind == JsonValueKind.String
                         && double.TryParse(p.Value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    dict.TryAdd(p.Name, d);
            }
            return dict;
        } catch (JsonException) {
            return null;
        }
    }

    /// <summary>The first object, depth first, that carries CRVAL1.</summary>
    private static JsonElement? FindWcsObject(JsonElement e) {
        if (e.ValueKind == JsonValueKind.Object) {
            if (e.EnumerateObject().Any(p => p.NameEquals("CRVAL1") || string.Equals(p.Name, "crval1", StringComparison.OrdinalIgnoreCase)))
                return e;
            foreach (var p in e.EnumerateObject())
                if (FindWcsObject(p.Value) is JsonElement found) return found;
        } else if (e.ValueKind == JsonValueKind.Array) {
            foreach (var item in e.EnumerateArray())
                if (FindWcsObject(item) is JsonElement found) return found;
        }
        return null;
    }
}
