using System.Runtime.Versioning;
using Microsoft.Win32;

namespace NINA.Ascom.Com;

/// <summary>
/// Enumerates ASCOM Platform drivers registered on the local machine
/// by walking the registry hives the ASCOM installer populates.
///
/// <para>The ASCOM Platform stores each installed driver under
/// <c>HKLM\SOFTWARE\ASCOM\&lt;DeviceType&gt; Drivers\&lt;ProgID&gt;</c>,
/// with the (default) value holding a human-readable description. The
/// 32-bit subset (still common for very old drivers) lives under
/// <c>HKLM\SOFTWARE\WOW6432Node\ASCOM\&lt;DeviceType&gt; Drivers\&lt;ProgID&gt;</c>.
/// HKCU is also walked so per-user installs are picked up.</para>
///
/// <para>This is the headless equivalent of the ASCOM Chooser dialog,
/// it never pops a window, never blocks a thread on a modal, and
/// works in a Windows service / SYSTEM session. The Chooser stays
/// reachable as a follow-up convenience for users who want the
/// familiar setup UI, but Polaris does not depend on it.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class AscomComRegistry {

    /// <summary>ASCOM device categories that map to a Polaris adapter.
    /// Keep aligned with the per-device-type method group at the
    /// bottom of this file.</summary>
    public enum DeviceType {
        Camera,
        Telescope,
        Focuser,
        FilterWheel,
        Rotator,
        Dome,
        FlatPanel,            // CoverCalibrator in ASCOM Platform 6.5+
        ObservingConditions
    }

    /// <summary>One registered driver. <paramref name="ProgId"/> is
    /// what gets fed to <see cref="Type.GetTypeFromProgID(string)"/>;
    /// <paramref name="Description"/> is the human-facing label the
    /// installer wrote into the (default) value (e.g. "ZWO ASI
    /// Camera"); <paramref name="DeviceType"/> echoes the subkey the
    /// driver was found under so a UI dropdown can group / filter.</summary>
    public sealed record AscomDriver(
        string ProgId,
        string Description,
        DeviceType DeviceType,
        bool Is32BitOnly);

    /// <summary>List every driver installed for the given device
    /// type. Empty when the ASCOM Platform is not installed at all
    /// (subkey absent) OR when no driver of that type is registered.
    /// Never throws.</summary>
    public static IReadOnlyList<AscomDriver> Enumerate(DeviceType type) {
        var subKeyName = SubKeyName(type);
        var seen = new Dictionary<string, AscomDriver>(StringComparer.OrdinalIgnoreCase);

        // 64-bit native path (HKLM + HKCU).
        WalkHive(Registry.LocalMachine, $@"SOFTWARE\ASCOM\{subKeyName}",
            type, is32: false, seen);
        WalkHive(Registry.CurrentUser, $@"SOFTWARE\ASCOM\{subKeyName}",
            type, is32: false, seen);

        // 32-bit WOW64 path. Most ASCOM 6.x drivers ship in-proc COM,
        // when Polaris itself is 64-bit those drivers must come from
        // an out-of-proc surrogate (DLLSurrogate). We still report
        // them so the UI can show a helpful "requires 32-bit
        // surrogate" badge instead of pretending the driver doesn't
        // exist.
        WalkHive(Registry.LocalMachine, $@"SOFTWARE\WOW6432Node\ASCOM\{subKeyName}",
            type, is32: true, seen);
        WalkHive(Registry.CurrentUser, $@"SOFTWARE\WOW6432Node\ASCOM\{subKeyName}",
            type, is32: true, seen);

        return seen.Values
            .OrderBy(d => d.Description, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>True when at least one driver of any type is
    /// registered, i.e. the ASCOM Platform is installed and usable.
    /// Cheap, used by RIGS UI to gate the "ASCOM (COM)" entry in
    /// the driver-source dropdown.</summary>
    public static bool IsPlatformInstalled() {
        try {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\ASCOM");
            if (k != null) return true;
            using var k32 = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\ASCOM");
            return k32 != null;
        } catch { return false; }
    }

    private static void WalkHive(RegistryKey hive, string path, DeviceType type,
                                  bool is32, Dictionary<string, AscomDriver> seen) {
        try {
            using var key = hive.OpenSubKey(path);
            if (key == null) return;
            foreach (var progId in key.GetSubKeyNames()) {
                // Skip dummy / internal entries the installer leaves
                // behind, anything without a description string is
                // almost certainly not a real driver.
                using var sub = key.OpenSubKey(progId);
                if (sub == null) continue;
                var description = sub.GetValue(null) as string;
                if (string.IsNullOrWhiteSpace(description)) description = progId;

                // Prefer the 64-bit native entry when the same ProgID
                // exists in both views (it almost always does for
                // ASCOM 6.x+ drivers shipped as 64-bit in-proc).
                if (seen.TryGetValue(progId, out var existing) && !existing.Is32BitOnly) continue;

                seen[progId] = new AscomDriver(progId, description!, type, is32);
            }
        } catch {
            // ACL / permissions issues on a single hive shouldn't
            // hide drivers we found in another hive.
        }
    }

    private static string SubKeyName(DeviceType type) => type switch {
        DeviceType.Camera              => "Camera Drivers",
        DeviceType.Telescope           => "Telescope Drivers",
        DeviceType.Focuser             => "Focuser Drivers",
        DeviceType.FilterWheel         => "FilterWheel Drivers",
        DeviceType.Rotator             => "Rotator Drivers",
        DeviceType.Dome                => "Dome Drivers",
        DeviceType.FlatPanel           => "CoverCalibrator Drivers",
        DeviceType.ObservingConditions => "ObservingConditions Drivers",
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };
}
