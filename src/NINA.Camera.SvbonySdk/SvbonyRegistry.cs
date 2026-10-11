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
using NINA.Camera.SvbonySdk.Native;
using NINA.Image.NativeLibs;

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

    // dlopen flags. RTLD_GLOBAL is the point: it puts libusb's symbols in the
    // global namespace where the SDK's unresolved references can find them.
    private const int RtldNow = 0x0002;
    private const int RtldGlobal = 0x0100;

    private static bool _prereqChecked;
    private static bool _prereqOk;

    /// <summary>Why the SVBony driver is not offered, or null when it is.</summary>
    public static string? UnavailableReason { get; private set; }

    // glibc 2.34 folded libdl into libc; older systems still have libdl.so.2.
    [DllImport("libc", EntryPoint = "dlopen", CharSet = CharSet.Ansi)]
    private static extern IntPtr LibcDlopen(string file, int mode);

    [DllImport("libdl.so.2", EntryPoint = "dlopen", CharSet = CharSet.Ansi)]
    private static extern IntPtr LibdlDlopen(string file, int mode);

    private static IntPtr GlobalDlopen(string soname) {
        try { return LibcDlopen(soname, RtldNow | RtldGlobal); }
        catch (DllNotFoundException) { /* pre-2.34 glibc */ }
        catch (EntryPointNotFoundException) { /* ditto */ }
        try { return LibdlDlopen(soname, RtldNow | RtldGlobal); }
        catch (DllNotFoundException) { return IntPtr.Zero; }
        catch (EntryPointNotFoundException) { return IntPtr.Zero; }
    }

    /// <summary>
    /// Pull libusb into the global symbol namespace before the SVBony SDK is
    /// loaded, and refuse to load the SDK at all if that fails.
    ///
    /// <para>The x86-64 build of <c>libSVBCameraSDK.so</c> calls 22 libusb
    /// functions and declares no dependency on libusb: its DT_NEEDED list is
    /// libstdc++, libm, libgcc_s and libc, nothing else. The arm64 build of
    /// the same version does declare it, which is why every Raspberry Pi and
    /// Orange Pi is fine and only mini PCs are not.</para>
    ///
    /// <para>The consequence is worse than a missing driver. The loader
    /// resolves the SDK lazily, so the process starts, serves the interface,
    /// and dies the first time anything enumerates cameras:</para>
    /// <code>
    /// Symbol lookup error: libSVBCameraSDK.so: undefined symbol: libusb_init
    /// polaris.service: Main process exited, code=exited, status=127
    /// </code>
    /// <para>That is the dynamic loader calling _exit, not an exception, so
    /// the try/catch around the availability probe below cannot help: there
    /// is nothing to catch. systemd then restarts the unit five seconds
    /// later and the operator sees the interface disconnect for ever, which
    /// is exactly how it was reported, as a network fault on an N95.</para>
    ///
    /// <para>So the dependency is loaded first, by hand, with RTLD_GLOBAL.
    /// When it cannot be, the SDK is never loaded and the driver simply does
    /// not appear, which is a bad night for one camera brand instead of a
    /// server that will not stay up.</para>
    /// </summary>
    private static bool PrerequisitesOk() {
        if (_prereqChecked) return _prereqOk;
        _prereqChecked = true;
        if (!OperatingSystem.IsLinux()) { _prereqOk = true; return true; }

        foreach (var soname in new[] { "libusb-1.0.so.0", "libusb-1.0.so" }) {
            if (GlobalDlopen(soname) != IntPtr.Zero) {
                _prereqOk = true;
                UnavailableReason = null;
                return true;
            }
        }
        _prereqOk = false;
        UnavailableReason =
            "libusb-1.0 is not installed, and the SVBony SDK needs it. Without it the SDK would "
            + "terminate Polaris the first time a camera is enumerated. Install it with: "
            + "sudo apt install libusb-1.0-0";
        return false;
    }

    /// <summary>Register a resolver that loads the SVBony native lib from
    /// the app base directory (where the per-RID Content copy lands) in
    /// addition to the OS default search path. Idempotent.</summary>
    public static void EnsureResolver() {
        if (_resolverRegistered) return;
        _resolverRegistered = true;
        try {
            NativeLibrary.SetDllImportResolver(typeof(SvbonyNative).Assembly, Resolve);
        } catch { /* already set by another caller, fine */ }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath) {
        if (!string.Equals(libraryName, LibName, StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero; // not ours; let the default resolver handle it

        // Refusing here turns a process death into a DllNotFoundException,
        // which the probe below catches and the UI reports as "no driver".
        if (!PrerequisitesOk()) return IntPtr.Zero;

        string[] candidates = NativeSdkProbe.Candidates("SVBCameraSDK.dll", "libSVBCameraSDK.dylib", "libSVBCameraSDK.so");
        // Probe the app base dir (bundled per-RID Content, Windows) and the
        // writable native-SDK pack dir the host exports for the downloadable
        // camera-SDK pack (Linux, where /opt/polaris isn't writable).
        foreach (var dir in NativeSdkProbe.Dirs()) {
            foreach (var name in candidates) {
                var path = Path.Combine(dir, name);
                if (File.Exists(path) && NativeLibrary.TryLoad(path, out var h)) return h;
            }
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
                if (!PrerequisitesOk()) return false;
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