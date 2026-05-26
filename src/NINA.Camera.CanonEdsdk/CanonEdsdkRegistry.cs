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
