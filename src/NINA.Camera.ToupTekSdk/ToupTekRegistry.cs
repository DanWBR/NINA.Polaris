using System.Reflection;
using System.Runtime.InteropServices;

namespace NINA.Camera.ToupTekSdk;

/// <summary>Availability probe + native-library resolver for the ToupTek SDK
/// (via the vendored official <c>Toupcam</c> binding). The driver appears in
/// the RIGS picker only when the native lib actually loads on this
/// host/arch.</summary>
public static class ToupTekRegistry {
    private static bool _resolverRegistered;

    public static void EnsureResolver() {
        if (_resolverRegistered) return;
        _resolverRegistered = true;
        try { NativeLibrary.SetDllImportResolver(typeof(Toupcam).Assembly, Resolve); }
        catch { }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath) {
        // The binding imports "libtoupcam.so" (Linux) / "toupcam.dll" (Windows).
        if (libraryName.IndexOf("toupcam", StringComparison.OrdinalIgnoreCase) < 0)
            return IntPtr.Zero;
        var baseDir = AppContext.BaseDirectory;
        string[] candidates = OperatingSystem.IsWindows()
            ? new[] { "toupcam.dll" }
            : new[] { "libtoupcam.so" };
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
                _ = Toupcam.EnumV2();
                return true;
            } catch (DllNotFoundException) { return false; }
            catch (BadImageFormatException) { return false; }
            catch { return false; }
        }
    }
}
