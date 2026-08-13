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

using System.Collections.Concurrent;
using NINA.Camera.ZwoSdk.Native;

namespace NINA.Camera.ZwoSdk;

/// <summary>
/// Enumerate connected ZWO ASI cameras. Id is the SDK CameraID (used by
/// Open/control calls), Model is the camera name.
///
/// <para>WHY THIS IS NOT A PLAIN LOOP. Once this process holds a camera OPEN,
/// the SDK stops reporting the others. Measured on the field board, two cameras
/// on the bus (2026-08-13, OPi 5 Pro):</para>
/// <code>
///   fresh process, nothing open     ASIGetNumOfConnectedCameras() = 2
///                                     id=0 ASI585MC Pro, id=1 ASI678MC
///   Polaris, holding the 585 open   ASIGetNumOfConnectedCameras() = 1
///                                     id=0 ASI585MC Pro
/// </code>
/// <para>So the guide-camera picker, opened while the imaging camera was
/// connected, could not offer the second camera at all and said nothing about
/// why. The imaging picker looked fine only because it happened to be populated
/// seconds after a disconnect. Confirmed both ways in the field: disconnecting
/// the imaging camera made the second camera appear.</para>
///
/// <para>So: remember every camera seen during this process's lifetime and
/// return the union, flagging which ones the SDK can see right now. A remembered
/// camera that has since been unplugged still lists and its connect fails with
/// the SDK's own message, which is a better failure than hiding a camera that is
/// physically present.</para>
/// </summary>
public static class ZwoDiscovery {

    /// <param name="Present">The SDK reported this camera on THIS scan. False
    /// means it was seen earlier in this process and is currently masked, which
    /// in practice means another camera is open.</param>
    public record ZwoCameraEntry(string Id, string Model, string Info, bool Present = true);

    // Seen-this-process registry, id -> model. Static because the masking is a
    // property of the process's SDK state, not of any one camera object.
    private static readonly ConcurrentDictionary<int, string> Seen = new();

    /// <summary>Forget the remembered cameras. For tests, and for a caller that
    /// wants a genuinely clean scan.</summary>
    public static void ForgetSeen() => Seen.Clear();

    /// <summary>The live scan alone, without the remembered union.</summary>
    public static IReadOnlyList<ZwoCameraEntry> Scan() {
        ZwoRegistry.EnsureResolver();
        var list = new List<ZwoCameraEntry>();
        int n;
        try { n = AsiNative.ASIGetNumOfConnectedCameras(); }
        catch { return list; }
        for (int i = 0; i < n; i++) {
            var info = new AsiNative.ASI_CAMERA_INFO();
            if (AsiNative.ASIGetCameraProperty(ref info, i) != AsiNative.ASI_ERROR_CODE.ASI_SUCCESS)
                continue;
            var model = string.IsNullOrWhiteSpace(info.Name) ? $"ASI #{info.CameraID}" : info.Name;
            list.Add(new ZwoCameraEntry(info.CameraID.ToString(), model, model));
        }
        return list;
    }

    public static IReadOnlyList<ZwoCameraEntry> Enumerate() => Merge(Scan());

    /// <summary>Union a live scan with what this process has seen before. Pure,
    /// so the masking behaviour is testable without a camera attached.</summary>
    public static IReadOnlyList<ZwoCameraEntry> Merge(IReadOnlyList<ZwoCameraEntry> live) {
        foreach (var e in live) {
            if (int.TryParse(e.Id, out var liveId)) Seen[liveId] = e.Model;
        }
        var present = live.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var all = new List<ZwoCameraEntry>(live);
        foreach (var kv in Seen) {
            var id = kv.Key.ToString();
            if (present.Contains(id)) continue;
            all.Add(new ZwoCameraEntry(id, kv.Value, kv.Value, Present: false));
        }
        return all.OrderBy(e => int.TryParse(e.Id, out var i) ? i : int.MaxValue).ToList();
    }
}