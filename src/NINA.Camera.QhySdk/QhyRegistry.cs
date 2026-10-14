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
using System.Text;
using NINA.Camera.QhySdk.Native;
using NINA.Image.NativeLibs;
using static NINA.Camera.QhySdk.Native.QhyNative;

namespace NINA.Camera.QhySdk;

/// <summary>Availability probe, native-library resolver and process-wide
/// SDK resource owner for the QHYCCD SDK. The driver appears in the RIGS
/// picker only when the native lib actually loads on this host/arch.</summary>
public static class QhyRegistry {
    private const string LibName = "qhyccd";
    private static bool _resolverRegistered;

    public static void EnsureResolver() {
        if (_resolverRegistered) return;
        _resolverRegistered = true;
        try { NativeLibrary.SetDllImportResolver(typeof(QhyNative).Assembly, Resolve); }
        catch { }
    }

    /// <summary>File names to try, per OS. libqhy installs a versioned soname
    /// (libqhyccd.so.25 for the 25.x SDK) and only the -dev style symlink is
    /// unversioned, which a runtime-only install may not have; older images
    /// carry older majors, hence the walk down.</summary>
    internal static string[] CandidateNames() => NativeSdkProbe.Candidates(
        "qhyccd.dll", "libqhyccd.dylib",
        "libqhyccd.so", "libqhyccd.so.25", "libqhyccd.so.24", "libqhyccd.so.23",
        "libqhyccd.so.22", "libqhyccd.so.21", "libqhyccd.so.20");

    /// <summary>Where the QHYCCD "AllInOne" driver installer puts the SDK on
    /// Windows: <c>%ProgramFiles%\QHYCCD\AllInOne\sdk\x64\qhyccd.dll</c>. It
    /// does not add that folder to PATH, so the default search never finds
    /// it. Null off Windows or on an architecture the installer has no build
    /// for.</summary>
    internal static string? WindowsInstallPath() {
        if (!OperatingSystem.IsWindows()) return null;
        var arch = RuntimeInformation.ProcessArchitecture switch {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            _ => null,
        };
        if (arch == null) return null;
        var pf = Environment.GetEnvironmentVariable("ProgramFiles");
        if (string.IsNullOrWhiteSpace(pf))
            pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(pf)) return null;
        return Path.Combine(pf, "QHYCCD", "AllInOne", "sdk", arch, "qhyccd.dll");
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath) {
        if (!string.Equals(libraryName, LibName, StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;
        var candidates = CandidateNames();
        // The app base dir and the host-exported native-SDK pack dir, same as
        // every other vendor resolver. Nothing is bundled for QHY, but an
        // operator who drops the library there gets it picked up.
        foreach (var dir in NativeSdkProbe.Dirs()) {
            foreach (var name in candidates) {
                var path = Path.Combine(dir, name);
                if (File.Exists(path) && NativeLibrary.TryLoad(path, out var h)) return h;
            }
        }
        // The OS loader's own search (PATH on Windows, ld.so.cache on Linux),
        // by each file name: the bare "qhyccd" would only ever find the
        // unversioned libqhyccd.so.
        foreach (var name in candidates) {
            if (NativeLibrary.TryLoad(name, assembly, searchPath, out var sys)) return sys;
        }
        // Windows: the vendor installer's folder. qhyccd.dll links against
        // ftd2xx.dll, tbb.dll and the VC90 runtime, which sit in that same
        // folder and nowhere on the search path. NativeLibrary.TryLoad with an
        // absolute path calls LoadLibraryEx with LOAD_WITH_ALTERED_SEARCH_PATH,
        // which makes the loader look for those dependencies in the DLL's own
        // directory first. So no AddDllDirectory / SetDllDirectory, which
        // would change the search path for the whole process.
        var vendor = WindowsInstallPath();
        if (vendor != null && File.Exists(vendor) && NativeLibrary.TryLoad(vendor, out var v)) return v;
        return IntPtr.Zero;
    }

    /// <summary>SDK build date as "YY.MM.DD", or null before the first
    /// successful probe.</summary>
    public static string? SdkVersion { get; private set; }

    /// <summary>True when the QHY native lib loads on this host. The probe
    /// is GetQHYCCDSDKVersion, which needs no InitQHYCCDResource and touches
    /// no USB device.</summary>
    /// <summary>Name the SDK goes by in <see cref="NativeCallGuard"/>.</summary>
    public const string GuardName = "qhy";

    /// <summary>Why the SDK is switched off after it crashed Polaris, or null.</summary>
    public static string? QuarantineReason => NativeCallGuard.QuarantineReason(GuardName);

    public static bool IsAvailable {
        get {
            try {
                EnsureResolver();
                if (QuarantineReason != null) return false;
                NativeCallGuard.Run(GuardName, "loading the SDK", () => {
                    if (GetQHYCCDSDKVersion(out var y, out var m, out var d, out _) == QHYCCD_SUCCESS)
                        SdkVersion = $"{y:00}.{m:00}.{d:00}";
                });
                return true;
            } catch (DllNotFoundException) { return false; }
            catch (BadImageFormatException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
            catch { return false; }
        }
    }

    // ----- Process-wide SDK resource -----
    //
    // InitQHYCCDResource / ScanQHYCCD / OpenQHYCCD / CloseQHYCCD act on the
    // SDK's global camera table, not on one camera, so they share one lock
    // across every QhySdkCamera in the process. The resource is initialised
    // lazily (the first discovery or connect) and released when the last open
    // camera closes, never while one is open: releasing it tears down the
    // handles of every camera at once. This is the same lifecycle N.I.N.A.'s
    // QHY driver uses.
    //
    // A camera's filter wheel (on its CFW port, or built in as on the
    // MiniCAM8) is driven through the camera's own handle, so the camera and
    // its wheel share one: the first to open it gets a fresh handle, the
    // second the same one, and CloseQHYCCD runs when the last of them lets
    // go. They also share the lock that serialises calls on that handle, as
    // the SDK is not thread-safe per camera.

    private static readonly object _resource = new();
    private static bool _initialized;
    private static readonly HashSet<string> _scannedIds = new(StringComparer.Ordinal);

    private sealed class SharedHandle {
        public required string Id;
        public required IntPtr Handle;
        public required string Model;
        public int Users;
        public bool Initialized;
        public readonly object Calls = new();
    }

    private static readonly Dictionary<string, SharedHandle> _open = new(StringComparer.Ordinal);

    public record QhyCameraId(string Id, string Model);

    private static void EnsureInitializedLocked() {
        if (_initialized) return;
        var rc = InitQHYCCDResource();
        if (rc != QHYCCD_SUCCESS)
            throw new InvalidOperationException($"QHY InitQHYCCDResource failed: 0x{rc:X8}");
        _initialized = true;
    }

    /// <summary>Scan the bus and list every QHY camera by SDK id and model.
    /// The id ("QHY600M-6bb54f4c1d2e3f40") is what OpenQHYCCD takes; the model
    /// ("QHY600M") is the readable name.</summary>
    internal static IReadOnlyList<QhyCameraId> Scan() {
        EnsureResolver();
        lock (_resource) {
            return NativeCallGuard.Run(GuardName, "listing cameras", () => {
                EnsureInitializedLocked();
                return (IReadOnlyList<QhyCameraId>)ScanLocked();
            });
        }
    }

    private static List<QhyCameraId> ScanLocked() {
        var list = new List<QhyCameraId>();
        uint n = ScanQHYCCD();
        // ScanQHYCCD returns the count, or QHYCCD_ERROR with nothing attached.
        if (n == QHYCCD_ERROR || n > 64) n = 0;
        _scannedIds.Clear();
        for (uint i = 0; i < n; i++) {
            var idBuf = new byte[IdBufferLength];
            if (GetQHYCCDId(i, idBuf) != QHYCCD_SUCCESS) continue;
            var id = CString(idBuf);
            if (string.IsNullOrWhiteSpace(id)) continue;
            var modelBuf = new byte[ModelBufferLength];
            var model = GetQHYCCDModel(id, modelBuf) == QHYCCD_SUCCESS ? CString(modelBuf) : "";
            if (string.IsNullOrWhiteSpace(model)) model = ModelFromId(id);
            _scannedIds.Add(id);
            list.Add(new QhyCameraId(id, model));
        }
        return list;
    }

    /// <summary>Open a camera by SDK id. OpenQHYCCD only knows ids a scan
    /// reported, so this scans first, except when another QHY camera is
    /// already open and the id is already in the table: a rescan then is a
    /// needless trip over a bus that camera is using. Returns the handle (zero
    /// when OpenQHYCCD refused) and the model name. A camera this process
    /// already has open returns the same handle again; every Open needs its
    /// own Close.</summary>
    internal static (IntPtr Handle, string Model) Open(string id) {
        EnsureResolver();
        lock (_resource) {
            if (_open.TryGetValue(id, out var shared)) {
                shared.Users++;
                return (shared.Handle, shared.Model);
            }
            return NativeCallGuard.Run(GuardName, "opening the camera", () => OpenLocked(id));
        }
    }

    private static (IntPtr Handle, string Model) OpenLocked(string id) {
        EnsureInitializedLocked();
        string model = "";
        if (!_scannedIds.Contains(id) || _open.Count == 0) {
            foreach (var c in ScanLocked())
                if (c.Id == id) model = c.Model;
        }
        if (!_scannedIds.Contains(id)) {
            ReleaseIfIdleLocked();
            throw new InvalidOperationException($"QHY camera '{id}' not found.");
        }
        var h = OpenQHYCCD(id);
        if (h == IntPtr.Zero) {
            ReleaseIfIdleLocked();
            return (IntPtr.Zero, model);
        }
        if (string.IsNullOrWhiteSpace(model)) {
            var modelBuf = new byte[ModelBufferLength];
            model = GetQHYCCDModel(id, modelBuf) == QHYCCD_SUCCESS ? CString(modelBuf) : "";
        }
        if (string.IsNullOrWhiteSpace(model)) model = ModelFromId(id);
        _open[id] = new SharedHandle { Id = id, Handle = h, Model = model, Users = 1 };
        return (h, model);
    }

    /// <summary>Give back a handle Open returned. CloseQHYCCD runs when the
    /// last user lets go, and the SDK resource is released when that was the
    /// last open camera.</summary>
    internal static void Close(IntPtr handle) {
        if (handle == IntPtr.Zero) return;
        lock (_resource) {
            var shared = Find(handle);
            if (shared != null) {
                if (--shared.Users > 0) return;
                _open.Remove(shared.Id);
            }
            try { CloseQHYCCD(handle); } catch { }
            ReleaseIfIdleLocked();
        }
    }

    /// <summary>The lock every SDK call on <paramref name="handle"/> goes
    /// through, shared by the camera and its filter wheel.</summary>
    internal static object CallLock(IntPtr handle) {
        lock (_resource) return Find(handle)?.Calls ?? new object();
    }

    /// <summary>Whether InitQHYCCD has run on this handle since it was
    /// opened: the wheel skips it when the camera already did it.</summary>
    internal static bool IsCameraInitialized(IntPtr handle) {
        lock (_resource) return Find(handle)?.Initialized ?? false;
    }

    internal static void MarkCameraInitialized(IntPtr handle) {
        lock (_resource) { var s = Find(handle); if (s != null) s.Initialized = true; }
    }

    private static SharedHandle? Find(IntPtr handle) {
        foreach (var s in _open.Values)
            if (s.Handle == handle) return s;
        return null;
    }

    private static void ReleaseIfIdleLocked() {
        if (!_initialized || _open.Count > 0) return;
        try { ReleaseQHYCCDResource(); } catch { }
        _initialized = false;
        // A fresh InitQHYCCDResource starts with an empty table.
        _scannedIds.Clear();
    }

    /// <summary>The model is the id minus its serial suffix
    /// ("QHY600M-6bb54f4c1d2e3f40" → "QHY600M"). Only a fallback for when
    /// GetQHYCCDModel says nothing: some model names contain a dash
    /// themselves ("QHY5LII-M"), so the last dash is the one cut at.</summary>
    internal static string ModelFromId(string id) {
        var dash = id.LastIndexOf('-');
        return dash > 0 ? id[..dash] : id;
    }

    private static string CString(byte[] buf) {
        int len = Array.IndexOf(buf, (byte)0);
        if (len < 0) len = buf.Length;
        return Encoding.ASCII.GetString(buf, 0, len).Trim();
    }
}
