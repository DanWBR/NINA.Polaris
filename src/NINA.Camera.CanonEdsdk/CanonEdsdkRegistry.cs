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

using System.Runtime.Versioning;
using NINA.Camera.CanonEdsdk.Native;

namespace NINA.Camera.CanonEdsdk;

/// <summary>
/// Singleton-style holder for the EDSDK init / terminate calls. The
/// Canon SDK is process-global, <c>EdsInitializeSDK</c> must be
/// called exactly once per process lifetime, and <c>EdsTerminateSDK</c>
/// must match it. Multiple camera instances share the init.
///
/// Thread-safety: <see cref="EnsureInitialized"/> uses a lock so the
/// first-touch race between Polaris startup and any test invocation
/// can't double-init. <see cref="IsAvailable"/> is a cheap probe that
/// catches the typical "user hasn't dropped the DLLs in yet" case and
/// surfaces it through the camera-drivers endpoint without crashing.
/// </summary>
[SupportedOSPlatform("windows")]
public static class CanonEdsdkRegistry {
    private static readonly object _lock = new();
    private static bool _initialized;
    private static bool? _available;

    /// <summary>True when <c>EDSDK.dll</c> is reachable on the standard
    /// DLL search path (next to the Polaris .exe, in
    /// <c>plugins/canon-edsdk/</c>, or anywhere on PATH) and
    /// <c>EdsInitializeSDK</c> returns successfully. Cached after the
    /// first probe, if the user adds the DLL after startup they need
    /// to restart Polaris.</summary>
    public static bool IsAvailable {
        get {
            if (_available.HasValue) return _available.Value;
            try {
                EnsureInitialized();
                _available = true;
            } catch {
                _available = false;
            }
            return _available.Value;
        }
    }

    /// <summary>Initialise the SDK if it hasn't been already. Throws
    /// when the native DLL can't be loaded (user hasn't installed
    /// EDSDK yet) or when <c>EdsInitializeSDK</c> returns non-OK.</summary>
    public static void EnsureInitialized() {
        if (_initialized) return;
        lock (_lock) {
            if (_initialized) return;
            var err = EdsdkNative.EdsInitializeSDK();
            if (err != EdsdkConstants.EDS_ERR_OK) {
                throw new InvalidOperationException(
                    $"EdsInitializeSDK failed with code 0x{err:X8}. " +
                    "Check that the Canon EDSDK DLLs are reachable.");
            }
            _initialized = true;
            // Process-exit hook so the SDK gets a chance to release the
            // USB handles cleanly even on hard shutdown.
            AppDomain.CurrentDomain.ProcessExit += (_, _) => {
                try { EdsdkNative.EdsTerminateSDK(); } catch { /* best effort */ }
            };
        }
    }
}