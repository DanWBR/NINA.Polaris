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

    /// <summary>Folder name of the EDSDK inside the writable native-SDK pack
    /// directory, where the zip installer puts the DLLs.</summary>
    public const string PackFolder = "canon-edsdk";

    /// <summary>The folder the EDSDK was loaded from, once it has been.</summary>
    public static string? LoadedFrom { get; private set; }

    static CanonEdsdkRegistry() {
        try { NativeLibrary.SetDllImportResolver(typeof(EdsdkNative).Assembly, Resolve); }
        catch { /* already set: the default search still applies */ }
    }

    /// <summary>Where Polaris looks for <c>EDSDK.dll</c>, in order: next to the
    /// executable, in <c>plugins/canon-edsdk</c> beside it, and in
    /// <c>canon-edsdk</c> under the writable native-SDK directory (the profile
    /// data folder, no administrator rights needed, kept across updates).
    /// Windows' own search (System32, PATH) follows when none has it.
    ///
    /// The guide used to send people to <c>plugins/canon-edsdk</c> while nothing
    /// looked there: the DLL was only ever found next to the executable or on
    /// PATH, because Windows does not search subfolders.</summary>
    public static IEnumerable<string> SearchDirs() {
        var baseDir = AppContext.BaseDirectory;
        yield return baseDir;
        yield return Path.Combine(baseDir, "plugins", PackFolder);
        var pack = Environment.GetEnvironmentVariable(NINA.Image.NativeLibs.NativeSdkProbe.EnvVar);
        if (!string.IsNullOrWhiteSpace(pack)) yield return Path.Combine(pack, PackFolder);
    }

    /// <summary>The folder the zip installer writes to, or null when the host
    /// has not exported a native-SDK directory.</summary>
    public static string? InstallDir {
        get {
            var pack = Environment.GetEnvironmentVariable(NINA.Image.NativeLibs.NativeSdkProbe.EnvVar);
            return string.IsNullOrWhiteSpace(pack) ? null : Path.Combine(pack, PackFolder);
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath) {
        if (!libraryName.StartsWith("EDSDK", StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
        foreach (var dir in SearchDirs()) {
            var edsdk = Path.Combine(dir, "EDSDK.dll");
            if (!File.Exists(edsdk)) continue;
            // EDSDK.dll pulls in EdsImage.dll by name. Loading it from the same
            // folder first means that lookup finds the copy already in the
            // process instead of searching the default path for it.
            var image = Path.Combine(dir, "EdsImage.dll");
            if (File.Exists(image)) NativeLibrary.TryLoad(image, out _);
            if (NativeLibrary.TryLoad(edsdk, out var handle)) {
                LoadedFrom = dir;
                return handle;
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>Forget a failed probe so the next <see cref="IsAvailable"/> looks
    /// again, after the DLLs were installed without a restart. A probe that
    /// succeeded stays: the SDK is initialised once per process.</summary>
    public static void ResetProbe() {
        lock (_lock) { if (!_initialized) _available = null; }
    }

    /// <summary>True when <c>EDSDK.dll</c> is found in one of
    /// <see cref="SearchDirs"/> or on Windows' own search path and
    /// <c>EdsInitializeSDK</c> returns successfully. Cached after the first
    /// probe; <see cref="ResetProbe"/> clears a failed one.</summary>
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
            var err = Run(EdsdkNative.EdsInitializeSDK);
            if (err != EdsdkConstants.EDS_ERR_OK) {
                throw new InvalidOperationException(
                    $"EdsInitializeSDK failed with code 0x{err:X8}. " +
                    "Check that the Canon EDSDK DLLs are reachable.");
            }
            _initialized = true;
            // Process-exit hook so the SDK gets a chance to release the
            // USB handles cleanly even on hard shutdown.
            AppDomain.CurrentDomain.ProcessExit += (_, _) => {
                try { Invoke(EdsdkNative.EdsTerminateSDK).Wait(2000); } catch { /* best effort */ }
            };
        }
    }

    // ---- The EDSDK thread -----------------------------------------
    //
    // EDSDK on Windows hands its events (a picture ready to download, a
    // property that changed) to the thread that initialised it, and only when
    // that thread lets it: a GUI app's message loop, or, in a program without
    // one, regular calls to EdsGetEvent(). Polaris is a server with neither, and
    // nothing called EdsGetEvent, so TakePicture fired the shutter and the
    // picture never reached Polaris: PREVIEW waited until the browser gave up
    // (Canon EOS R8 on Windows, 2026-10-13). Calls also came from whatever
    // thread-pool thread served the request, and the SDK is not made for that.
    //
    // So one thread owns the SDK: it initialises it, runs every call handed to
    // it, and pumps EdsGetEvent between them, which is where the camera's object
    // events (and with them the downloads) now run.

    private static readonly System.Collections.Concurrent.BlockingCollection<Action> _work = new();
    private static Thread? _sdkThread;
    private static readonly TimeSpan PumpInterval = TimeSpan.FromMilliseconds(20);

    private static void EnsureThread() {
        if (_sdkThread != null) return;
        lock (_work) {
            if (_sdkThread != null) return;
            // MTA, not STA: an STA thread has to pump window messages while it
            // waits, and EdsOpenSession blocks it waiting for one, so a
            // session never opened (the call hung for good with an R8).
            // EdsGetEvent delivers the camera's events without a message loop.
            var t = new Thread(Pump) { IsBackground = true, Name = "EDSDK" };
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
            _sdkThread = t;
        }
    }

    private static void Pump() {
        while (true) {
            try {
                if (_work.TryTake(out var job, PumpInterval)) job();
                if (_initialized) EdsdkNative.EdsGetEvent();
            } catch {
                // A job reports its own failure through its task; the pump
                // itself must never stop, or the SDK goes deaf for good.
            }
        }
    }

    /// <summary>True on the EDSDK thread, where SDK calls run directly (event
    /// handlers, and calls nested inside a job).</summary>
    public static bool OnSdkThread => Thread.CurrentThread == _sdkThread;

    /// <summary>Run <paramref name="f"/> on the EDSDK thread.</summary>
    public static Task<T> Invoke<T>(Func<T> f) {
        if (OnSdkThread) {
            try { return Task.FromResult(f()); }
            catch (Exception ex) { return Task.FromException<T>(ex); }
        }
        EnsureThread();
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Add(() => {
            try { tcs.SetResult(f()); } catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    public static Task Invoke(Action a) => Invoke<object?>(() => { a(); return null; });

    /// <summary>Run <paramref name="f"/> on the EDSDK thread and wait for it.</summary>
    public static T Run<T>(Func<T> f) => Invoke(f).GetAwaiter().GetResult();

    public static void Run(Action a) => Invoke(a).GetAwaiter().GetResult();
}