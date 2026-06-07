using System.Reflection;
using System.Runtime.InteropServices;
using NINA.Camera.ZwoSdk.Native;

namespace NINA.Camera.ZwoSdk;

/// <summary>Availability probe + native-library resolver for the ZWO ASI
/// SDK. The driver appears in the RIGS picker only when the native lib
/// actually loads on this host/arch.</summary>
public static class ZwoRegistry {
    private const string LibName = "ASICamera2";
    private static bool _resolverRegistered;

    public static void EnsureResolver() {
        if (_resolverRegistered) return;
        _resolverRegistered = true;
        try { NativeLibrary.SetDllImportResolver(typeof(AsiNative).Assembly, Resolve); }
        catch { }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath) {
        if (!string.Equals(libraryName, LibName, StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;
        var baseDir = AppContext.BaseDirectory;
        string[] candidates = OperatingSystem.IsWindows()
            ? new[] { "ASICamera2.dll" }
            : new[] { "libASICamera2.so" };
        foreach (var name in candidates) {
            var path = Path.Combine(baseDir, name);
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out var h)) return h;
        }
        return NativeLibrary.TryLoad(libraryName, assembly, searchPath, out var sys) ? sys : IntPtr.Zero;
    }

    public static bool IsAvailable {
        get {
            try {
                EnsureResolver();
                _ = AsiNative.ASIGetNumOfConnectedCameras();
                return true;
            } catch (DllNotFoundException) { return false; }
            catch (BadImageFormatException) { return false; }
            catch { return false; }
        }
    }
}
