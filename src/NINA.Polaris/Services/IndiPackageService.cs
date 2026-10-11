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

namespace NINA.Polaris.Services;

/// <summary>One INDI driver package as offered in the UI.</summary>
public readonly record struct IndiPackageInfo(
    string Name, string Summary, bool Installed, string? Version, bool Available);

/// <summary>Which INDI the host is running, because it decides what may be installed.</summary>
public enum IndiStack { Unknown, Ppa, Archive }

/// <summary>
/// Installs INDI driver packages from inside Polaris.
///
/// The reason this is not just "run apt" is the split between the two INDI
/// builds: the mutlaqja PPA (libindi1) and the distribution archive
/// (libindidriver1). Mixing them removes the stack in use, along with PHD2,
/// which is how a working rig loses its mount driver mid-session. So every
/// install is simulated first and refused if the plan removes anything, and
/// the catalogue only offers what matches the stack already installed.
/// </summary>
public sealed class IndiPackageService {
    public const string UnitPrefix = "polaris-indi-install@";

    // The archive's driver packages, kept in step with INDI_ARCHIVE_DRIVERS in
    // scripts/install-polaris-linux.sh: these are the ones the installer falls
    // back to when the PPA has nothing for this release.
    private static readonly (string Name, string Summary)[] ArchiveDrivers = {
        ("indi-asi", "ZWO ASI cameras, EAF, EFW"),
        ("indi-toupbase", "ToupTek, Altair, Omegon, Meade cameras"),
        ("indi-svbony", "SVBony cameras"),
        ("indi-playerone", "Player One cameras"),
        ("indi-qhy", "QHY cameras"),
        ("indi-gphoto", "DSLR and mirrorless over gphoto2"),
        ("indi-eqmod", "EQMod mounts (EQ6, HEQ5, AZ-EQ)"),
        ("indi-celestronaux", "Celestron AUX mounts"),
        ("indi-avalon", "Avalon mounts"),
        ("indi-atik", "Atik cameras and wheels"),
        ("indi-mi", "Moravian cameras"),
        ("indi-sx", "Starlight Xpress cameras and wheels"),
        ("indi-dsi", "Meade DSI cameras"),
        ("indi-fli", "FLI cameras and focusers"),
        ("indi-sbig", "SBIG cameras"),
        ("indi-apogee", "Apogee cameras"),
        ("indi-qsi", "QSI cameras"),
        ("indi-duino", "Arduino based devices"),
        ("indi-webcam", "UVC webcams"),
        ("indi-gpsd", "GPS through gpsd"),
        ("indi-gpsnmea", "GPS over NMEA serial"),
        ("indi-aagcloudwatcher-ng", "AAG CloudWatcher"),
    };

    // On the PPA the third-party drivers ship as one package, so there is one
    // thing to offer and it is all of them.
    private static readonly (string Name, string Summary)[] PpaDrivers = {
        ("indi-3rdparty-drivers", "Every third-party INDI driver (ZWO, ToupTek, SVBony, QHY, Player One and the rest)"),
    };

    private readonly ILogger<IndiPackageService> _logger;

    public IndiPackageService(ILogger<IndiPackageService> logger) {
        _logger = logger;
    }

    public bool IsSupported => OperatingSystem.IsLinux() && File.Exists("/usr/bin/apt-get");

    /// <summary>Which INDI build is installed. The PPA one owns libindi1; the
    /// archive one owns libindidriver1.</summary>
    public async Task<IndiStack> DetectStackAsync(CancellationToken ct = default) {
        if (!IsSupported) return IndiStack.Unknown;
        if (await IsInstalledAsync("libindi1", ct)) return IndiStack.Ppa;
        if (await IsInstalledAsync("libindidriver1", ct)) return IndiStack.Archive;
        return IndiStack.Unknown;
    }

    public async Task<IReadOnlyList<IndiPackageInfo>> ListAsync(CancellationToken ct = default) {
        var list = new List<IndiPackageInfo>();
        if (!IsSupported) return list;

        var stack = await DetectStackAsync(ct);
        var catalogue = stack switch {
            IndiStack.Ppa => PpaDrivers,
            IndiStack.Archive => ArchiveDrivers,
            // Nothing installed yet: offer the archive set, which is the one
            // that exists everywhere.
            _ => ArchiveDrivers
        };

        foreach (var (name, summary) in catalogue) {
            var (installed, version, available) = await PolicyAsync(name, ct);
            list.Add(new IndiPackageInfo(name, summary, installed, version, available));
        }
        return list;
    }

    private async Task<bool> IsInstalledAsync(string pkg, CancellationToken ct) {
        var r = await RunAsync("dpkg-query", $"-W -f=${{Status}} {pkg}", ct);
        return r.stdout.Contains("install ok installed", StringComparison.Ordinal);
    }

    /// <summary>installed? / candidate version / is there a candidate at all.</summary>
    private async Task<(bool Installed, string? Version, bool Available)> PolicyAsync(
            string pkg, CancellationToken ct) {
        var r = await RunAsync("apt-cache", $"policy {pkg}", ct);
        string? installed = null, candidate = null;
        foreach (var raw in r.stdout.Split('\n')) {
            var line = raw.Trim();
            if (line.StartsWith("Installed:", StringComparison.Ordinal))
                installed = line["Installed:".Length..].Trim();
            else if (line.StartsWith("Candidate:", StringComparison.Ordinal))
                candidate = line["Candidate:".Length..].Trim();
        }
        bool isInstalled = installed != null && installed != "(none)";
        bool hasCandidate = candidate != null && candidate != "(none)";
        return (isInstalled, isInstalled ? installed : candidate, hasCandidate);
    }

    /// <summary>What apt would do, without doing it.</summary>
    public async Task<IndiPackagePlan> PlanAsync(string name, CancellationToken ct = default) {
        if (!IndiPackagePlanner.IsValidPackageName(name))
            return new IndiPackagePlan(Array.Empty<string>(), Array.Empty<string>(), false,
                "That is not an INDI driver package name.");
        if (!IsSupported)
            return new IndiPackagePlan(Array.Empty<string>(), Array.Empty<string>(), false,
                "Installing drivers is only available on a Linux host with apt.");

        var r = await RunAsync("apt-get", $"install -s {name}", ct);
        if (r.code != 0 && string.IsNullOrWhiteSpace(r.stdout))
            return new IndiPackagePlan(Array.Empty<string>(), Array.Empty<string>(), false,
                string.IsNullOrWhiteSpace(r.stderr) ? "apt could not plan the install." : r.stderr.Trim());
        return IndiPackagePlanner.Parse(r.stdout);
    }

    /// <summary>Plan, refuse if the plan is destructive, then install through
    /// the packaged unit (apt needs root; Polaris does not have it).</summary>
    public async Task<(bool Ok, string Message, IndiPackagePlan Plan)> InstallAsync(
            string name, CancellationToken ct = default) {
        var plan = await PlanAsync(name, ct);
        if (!plan.Ok) return (false, plan.Refusal ?? "Refused.", plan);

        var unit = UnitPrefix + name + ".service";
        var r = await RunAsync("systemctl", $"start {unit}", ct);
        if (r.code != 0) {
            var detail = string.IsNullOrWhiteSpace(r.stderr) ? r.stdout : r.stderr;
            _logger.LogWarning("INDI driver install unit failed ({Code}): {Detail}", r.code, detail);
            return (false, "Could not run the install: " + detail.Trim(), plan);
        }

        var (installed, version, _) = await PolicyAsync(name, ct);
        return installed
            ? (true, $"{name} {version} installed.", plan)
            : (false, "apt ran but the package is still not installed. See the host log.", plan);
    }

    private static async Task<(int code, string stdout, string stderr)> RunAsync(
            string file, string args, CancellationToken ct) {
        var psi = new ProcessStartInfo(file, args) {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        // A sane, non-interactive apt in every child process.
        psi.Environment["DEBIAN_FRONTEND"] = "noninteractive";
        psi.Environment["LC_ALL"] = "C";            // so the Inst/Remv lines are parseable
        using var p = Process.Start(psi);
        if (p == null) return (-1, "", "could not start " + file);
        var so = await p.StandardOutput.ReadToEndAsync(ct);
        var se = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, so, se);
    }
}
