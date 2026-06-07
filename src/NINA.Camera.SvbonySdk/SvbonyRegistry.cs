using System.Reflection;
using System.Runtime.InteropServices;
using NINA.Camera.SvbonySdk.Native;

namespace NINA.Camera.SvbonySdk;

/// <summary>
/// Availability probe + native-library resolver for the SVBony SDK. Mirrors
/// the role of CanonEdsdkRegistry / SonySdkRegistry. The driver appears in
/// the RIGS picker only when <see cref="IsAvailable"/> is true (i.e. the
/// native lib actually loaded on this host/arch).
/// </summary>
public static class SvbonyRegistry {
    private const string LibName = "SVBCameraSDK";
    private static bool _resolverRegistered;

    /// <summary>Register a resolver that loads the SVBony native lib from
    /// the app base directory (where the per-RID Content copy lands) in
    /// addition to the OS default search path. Idempotent.</summary>
    public static void EnsureResolver() {
        if (_resolverRegistered) return;
        _resolverRegistered = true;
        try {
            NativeLibrary.SetDllImportResolver(typeof(SvbonyNative).Assembly, Resolve);
        } catch { /* already set by another caller — fine */ }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath) {
        if (!string.Equals(libraryName, LibName, StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero; // not ours; let the default resolver handle it

        var baseDir = AppContext.BaseDirectory;
        string[] candidates = OperatingSystem.IsWindows()
            ? new[] { "SVBCameraSDK.dll" }
            : new[] { "libSVBCameraSDK.so" };
        foreach (var name in candidates) {
            var path = Path.Combine(baseDir, name);
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out var h)) return h;
        }
        // Fall back to the OS loader (system-installed lib).
        return NativeLibrary.TryLoad(libraryName, assembly, searchPath, out var sys)
            ? sys : IntPtr.Zero;
    }

    /// <summary>True when the SVBony native lib loads and the SDK entry
    /// point is callable on this host. False on platforms/arches without
    /// the binary, so the UI hides the driver gracefully.</summary>
    public static bool IsAvailable {
        get {
            try {
                EnsureResolver();
                _ = SvbonyNative.SVBGetNumOfConnectedCameras();
                return true;
            } catch (DllNotFoundException) {
                return false;
            } catch (BadImageFormatException) {
                return false;
            } catch {
                return false;
            }
        }
    }
}
