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

using System.Reflection;
using System.Runtime.InteropServices;
using NINA.Image.NativeLibs;

namespace NINA.Camera.AltairSdk;

/// <summary>Availability probe + native-library resolver for the Altair SDK
/// (via the vendored official <c>Altaircam</c> binding). The driver appears in
/// the RIGS picker only when the native lib actually loads on this
/// host/arch.</summary>
public static class AltairRegistry {
    private static bool _resolverRegistered;

    public static void EnsureResolver() {
        if (_resolverRegistered) return;
        _resolverRegistered = true;
        try { NativeLibrary.SetDllImportResolver(typeof(Altaircam).Assembly, Resolve); }
        catch { }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath) {
        // The binding imports "libaltaircam.so" (Linux) / "altaircam.dll" (Windows).
        if (libraryName.IndexOf("altaircam", StringComparison.OrdinalIgnoreCase) < 0)
            return IntPtr.Zero;
        string[] candidates = NativeSdkProbe.Candidates("altaircam.dll", "libaltaircam.dylib", "libaltaircam.so");
        foreach (var dir in NativeSdkProbe.Dirs()) {
            foreach (var name in candidates) {
                var path = Path.Combine(dir, name);
                if (File.Exists(path) && NativeLibrary.TryLoad(path, out var h)) return h;
            }
        }
        return NativeLibrary.TryLoad(libraryName, assembly, searchPath, out var sys) ? sys : IntPtr.Zero;
    }

    public static bool IsAvailable {
        get {
            try {
                EnsureResolver();
                _ = Altaircam.EnumV2();
                return true;
            } catch (DllNotFoundException) { return false; }
            catch (BadImageFormatException) { return false; }
            catch { return false; }
        }
    }
}