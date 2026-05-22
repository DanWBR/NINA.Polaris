using System.Runtime.Versioning;
using NINA.Camera.CanonEdsdk.Native;

namespace NINA.Camera.CanonEdsdk;

/// <summary>
/// Walks the EDSDK camera list once per call and returns a snapshot
/// of the connected Canon bodies. Used by the UI to populate the
/// camera dropdown when the user picks <c>driver=canon-edsdk</c>,
/// and by <see cref="CanonEdsdkCamera"/> to resolve a device id back
/// to its native camera handle at connect time.
///
/// The native list is volatile — a USB unplug invalidates every
/// handle it contains — so we always re-enumerate and never cache
/// the camera refs themselves across calls.
/// </summary>
[SupportedOSPlatform("windows")]
public static class CanonEdsdkDiscovery {

    public record CanonCameraEntry(string Id, string Model, string PortName);

    /// <summary>Returns the connected Canon cameras, or an empty list
    /// when none are present. Throws when the SDK itself can't be
    /// initialised (DLL missing or version mismatch) — the caller
    /// should fall back to a "no DSL R cameras detected" message.</summary>
    public static IReadOnlyList<CanonCameraEntry> Enumerate() {
        CanonEdsdkRegistry.EnsureInitialized();

        var result = new List<CanonCameraEntry>();
        var err = EdsdkNative.EdsGetCameraList(out var listRef);
        if (err != EdsdkConstants.EDS_ERR_OK || listRef == IntPtr.Zero) return result;

        try {
            err = EdsdkNative.EdsGetChildCount(listRef, out var count);
            if (err != EdsdkConstants.EDS_ERR_OK) return result;

            for (int i = 0; i < count; i++) {
                err = EdsdkNative.EdsGetChildAtIndex(listRef, i, out var camRef);
                if (err != EdsdkConstants.EDS_ERR_OK || camRef == IntPtr.Zero) continue;

                try {
                    err = EdsdkNative.EdsGetDeviceInfo(camRef, out var info);
                    if (err != EdsdkConstants.EDS_ERR_OK) continue;

                    // Synthetic id: port name is what EDSDK uses
                    // internally to disambiguate two bodies of the
                    // same model plugged in side-by-side. Stable
                    // across re-enumeration as long as the user
                    // doesn't re-plug into a different USB port.
                    result.Add(new CanonCameraEntry(
                        Id:       info.szPortName,
                        Model:    info.szDeviceDescription,
                        PortName: info.szPortName));
                } finally {
                    EdsdkNative.EdsRelease(camRef);
                }
            }
        } finally {
            EdsdkNative.EdsRelease(listRef);
        }
        return result;
    }

    /// <summary>Find the EDSDK <c>EdsCameraRef</c> matching a previously-discovered
    /// id and return it as a held handle. The caller owns the ref and
    /// must <c>EdsRelease</c> it (typically by passing ownership to a
    /// <see cref="CanonEdsdkCamera"/> instance, which releases at
    /// disconnect/dispose).</summary>
    public static IntPtr OpenCameraRefById(string id) {
        CanonEdsdkRegistry.EnsureInitialized();

        var err = EdsdkNative.EdsGetCameraList(out var listRef);
        if (err != EdsdkConstants.EDS_ERR_OK) return IntPtr.Zero;
        try {
            if (EdsdkNative.EdsGetChildCount(listRef, out var count) != EdsdkConstants.EDS_ERR_OK)
                return IntPtr.Zero;
            for (int i = 0; i < count; i++) {
                if (EdsdkNative.EdsGetChildAtIndex(listRef, i, out var camRef) != EdsdkConstants.EDS_ERR_OK
                    || camRef == IntPtr.Zero) continue;
                if (EdsdkNative.EdsGetDeviceInfo(camRef, out var info) == EdsdkConstants.EDS_ERR_OK
                    && string.Equals(info.szPortName, id, StringComparison.Ordinal)) {
                    return camRef;   // caller owns + releases
                }
                EdsdkNative.EdsRelease(camRef);
            }
        } finally {
            EdsdkNative.EdsRelease(listRef);
        }
        return IntPtr.Zero;
    }
}
