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

using System.Text.Json;
using System.Text.RegularExpressions;

namespace NINA.Polaris.Services;

/// <summary>
/// The native guider's calibrations, kept in a file of their own per rig
/// (<c>{profiles}/guide-calibrations/{rigId}.json</c>) next to the copy in the
/// profile, and read back every time Loop is pressed.
///
/// <para>Field, 2026-10-10: on a night of address changes one rig reported "no
/// calibration" although only the host's network settings had been touched;
/// the operator recovered with a calibration file saved by hand. The file here
/// is updated entry by entry, never overwritten from the profile's list, so a
/// list lost from the profile cannot take the file's copies with it.</para>
///
/// <para>Also the matching rules: an equipment key carries device ids, and the
/// id of a network device is its address (Alpaca <c>host:port:n</c>, SynScan
/// Wi-Fi <c>host</c>), so the same mount on a new address used to count as
/// other gear. Addresses are left out of the comparison, and when nothing
/// matches exactly a calibration for the same guide camera, binning and
/// focal length is used, as PHD2 does, with a note naming the mount it was
/// measured with.</para>
/// </summary>
public static class GuideCalibrationFile {
    public const int Cap = 12;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string PathFor(string profileDirectory, string rigId)
        => Path.Combine(profileDirectory, "guide-calibrations", rigId + ".json");

    public static List<NativeCalibrationData> Load(string path) {
        lock (Gate) {
            try {
                if (!File.Exists(path)) return new();
                return JsonSerializer.Deserialize<List<NativeCalibrationData>>(File.ReadAllText(path)) ?? new();
            } catch (Exception) {
                return new();   // a damaged file must not stop the guider; the profile copy still counts
            }
        }
    }

    /// <summary>Add or replace the entry for <paramref name="d"/>'s key.</summary>
    public static void Upsert(string path, NativeCalibrationData d) {
        lock (Gate) {
            var list = LoadUnlocked(path);
            list.RemoveAll(c => SameKey(c.Key, d.Key));
            list.Add(d);
            if (list.Count > Cap) list.RemoveRange(0, list.Count - Cap);
            Write(path, list);
        }
    }

    public static void Remove(string path, string key) {
        lock (Gate) {
            var list = LoadUnlocked(path);
            if (list.RemoveAll(c => SameKey(c.Key, key)) > 0) Write(path, list);
        }
    }

    private static List<NativeCalibrationData> LoadUnlocked(string path) {
        try {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<List<NativeCalibrationData>>(File.ReadAllText(path)) ?? new()
                : new();
        } catch (Exception) {
            return new();
        }
    }

    private static void Write(string path, List<NativeCalibrationData> list) {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list, Json));
        File.Move(tmp, path, overwrite: true);
    }

    // ---- matching ----

    private static readonly Regex Address = new(
        @"(?:https?://)?\b\d{1,3}(?:\.\d{1,3}){3}(?::\d+)?", RegexOptions.Compiled);

    /// <summary>The key without network addresses: the same device on a new
    /// address is the same device.</summary>
    public static string NormalizeKey(string? key)
        => Address.Replace((key ?? "").Trim().ToLowerInvariant(), "");

    public static bool SameKey(string? a, string? b)
        => string.Equals(NormalizeKey(a), NormalizeKey(b), StringComparison.Ordinal);

    /// <summary>One part of a key ("cam", "bin", "fl", "mount").</summary>
    public static string Part(string? key, string name) {
        foreach (var p in NormalizeKey(key).Split('|')) {
            var i = p.IndexOf('=');
            if (i > 0 && p[..i] == name) return p[(i + 1)..];
        }
        return "";
    }

    /// <summary>The calibration to use for <paramref name="key"/>: the newest
    /// with the same equipment (addresses aside), else the newest for the same
    /// guide camera, binning and focal length, which is what the geometry and
    /// pixel rates depend on; <c>Exact</c> says which.</summary>
    public static (NativeCalibrationData? Match, bool Exact) Find(IReadOnlyList<NativeCalibrationData> list, string key) {
        var ordered = list.OrderBy(c => c.SavedAtUtc ?? "", StringComparer.Ordinal).ToList();
        var exact = ordered.LastOrDefault(c => SameKey(c.Key, key));
        if (exact != null) return (exact, true);
        var near = ordered.LastOrDefault(c => !string.IsNullOrEmpty(c.Key)
            && Part(c.Key, "cam") == Part(key, "cam")
            && Part(c.Key, "bin") == Part(key, "bin")
            && Part(c.Key, "fl") == Part(key, "fl"));
        return (near, false);
    }

    /// <summary>Profile and file together, one entry per key, newest kept.</summary>
    public static List<NativeCalibrationData> Merge(IEnumerable<NativeCalibrationData>? profile,
            IEnumerable<NativeCalibrationData>? file) {
        var all = (profile ?? Enumerable.Empty<NativeCalibrationData>())
            .Concat(file ?? Enumerable.Empty<NativeCalibrationData>())
            .Where(c => c != null)
            .OrderBy(c => c.SavedAtUtc ?? "", StringComparer.Ordinal);
        var byKey = new Dictionary<string, NativeCalibrationData>();
        foreach (var c in all) byKey[NormalizeKey(c.Key)] = c;
        return byKey.Values.OrderBy(c => c.SavedAtUtc ?? "", StringComparer.Ordinal).ToList();
    }
}
