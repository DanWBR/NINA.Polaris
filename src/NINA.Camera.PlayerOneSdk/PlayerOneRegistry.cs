using System.Reflection;
using System.Runtime.InteropServices;
using NINA.Camera.PlayerOneSdk.Native;

namespace NINA.Camera.PlayerOneSdk;

/// <summary>Availability probe + native-library resolver for the PlayerOne
/// SDK. The driver appears in the RIGS picker only when the native lib
/// actually loads on this host/arch.</summary>
public static class PlayerOneRegistry {
    private const string LibName = "PlayerOneCamera";
    private static bool _resolverRegistered;

    public static void EnsureResolver() {
        if (_resolverRegistered) return;
        _resolverRegistered = true;
        try { NativeLibrary.SetDllImportResolver(typeof(PoaNative).Assembly, Resolve); }
        catch { }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath) {
        if (!string.Equals(libraryName, LibName, StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;
        var baseDir = AppContext.BaseDirectory;
        string[] candidates = OperatingSystem.IsWindows()
            ? new[] { "PlayerOneCamera.dll" }
            : new[] { "libPlayerOneCamera.so", "libPlayerOneCamera.so.3" };
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
                _ = PoaNative.POAGetCameraCount();
                return true;
            } catch (DllNotFoundException) { return false; }
            catch (BadImageFormatException) { return false; }
            catch { return false; }
        }
    }
}
