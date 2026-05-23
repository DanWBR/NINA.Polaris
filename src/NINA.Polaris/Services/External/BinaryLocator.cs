namespace NINA.Polaris.Services.External;

/// <summary>
/// Shared helper for finding an external binary on disk. Until now each
/// integration (ASTAP, PHD2, etc.) duplicated its own GetDefaultPath +
/// EnumerateCandidatePaths pair. Siril and GraXpert make a third and
/// fourth copy unreasonable — this helper concentrates the lookup
/// strategy so adding a fifth tool only requires listing the candidate
/// paths.
///
/// Lookup priority:
///   1. Explicit configured path (profile setting / CLI flag override)
///   2. OS-specific list of well-known install dirs (Program Files,
///      /usr/bin, /Applications, etc.)
///   3. PATH environment variable (Linux/macOS — Windows uses PATHEXT
///      so we explicitly suffix .exe in the candidate list instead)
///
/// First hit wins. Returns null when nothing exists so callers can
/// gate UI ("not installed" banner) on a single check.
/// </summary>
public static class BinaryLocator {

    /// <summary>Diagnostic entry: where we looked + whether it existed.</summary>
    public sealed record Candidate(string Description, string Path, bool Exists);

    /// <summary>
    /// Resolve a binary path. Returns null if neither the configured
    /// path nor any candidate exists on disk.
    /// </summary>
    /// <param name="configuredPath">User-supplied override (profile field). Trimmed; empty/whitespace = unset.</param>
    /// <param name="windowsCandidates">Absolute paths to check on Windows.</param>
    /// <param name="linuxCandidates">Absolute paths to check on Linux/BSD.</param>
    /// <param name="macCandidates">Absolute paths to check on macOS.</param>
    /// <param name="pathLookupName">Binary name to look up via $PATH (Unix only). Pass null to skip PATH search.</param>
    public static string? Find(string? configuredPath,
                                string[] windowsCandidates,
                                string[] linuxCandidates,
                                string[] macCandidates,
                                string? pathLookupName) {
        var cfg = (configuredPath ?? "").Trim();
        if (cfg.Length > 0 && File.Exists(cfg)) return cfg;

        foreach (var c in PlatformCandidates(windowsCandidates, linuxCandidates, macCandidates)) {
            if (File.Exists(c)) return c;
        }

        if (!string.IsNullOrEmpty(pathLookupName) && !OperatingSystem.IsWindows()) {
            foreach (var c in PathLookup(pathLookupName)) {
                if (File.Exists(c)) return c;
            }
        }

        return null;
    }

    /// <summary>
    /// Diagnostic list of every place we looked. Used by the Settings
    /// panel to show "we checked: /usr/bin/siril (✗), /opt/siril (✗), ..."
    /// so the user knows where to drop the binary if it's installed
    /// somewhere unusual.
    /// </summary>
    public static IReadOnlyList<Candidate> Enumerate(string? configuredPath,
                                                      string[] windowsCandidates,
                                                      string[] linuxCandidates,
                                                      string[] macCandidates,
                                                      string? pathLookupName) {
        var list = new List<Candidate>();
        var cfg = (configuredPath ?? "").Trim();
        if (cfg.Length > 0) list.Add(new("Configured", cfg, File.Exists(cfg)));

        foreach (var c in PlatformCandidates(windowsCandidates, linuxCandidates, macCandidates)) {
            list.Add(new("Well-known install path", c, File.Exists(c)));
        }

        if (!string.IsNullOrEmpty(pathLookupName) && !OperatingSystem.IsWindows()) {
            foreach (var c in PathLookup(pathLookupName)) {
                list.Add(new("$PATH", c, File.Exists(c)));
            }
        }

        return list;
    }

    private static IEnumerable<string> PlatformCandidates(string[] win, string[] linux, string[] mac) {
        if (OperatingSystem.IsWindows()) return win;
        if (OperatingSystem.IsMacOS())   return mac;
        return linux;   // catches Linux + FreeBSD + everything else Unix-ish
    }

    private static IEnumerable<string> PathLookup(string binaryName) {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator)) {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            yield return Path.Combine(dir, binaryName);
        }
    }
}
