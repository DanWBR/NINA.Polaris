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

using System.Runtime.InteropServices;

namespace NINA.Polaris.Services.OpenCl;

/// <summary>
/// Cheap, side-effect-free probe for whether this machine can run our OpenCL
/// image kernels on the SBC GPU. Mirrors <see cref="Rknn.RknnRuntime"/>: the
/// gate here is only a fast pre-check; the real authority is whether
/// <see cref="OpenClContext"/> actually finds a usable GPU device and builds a
/// trivial kernel without error (the compute backend always falls back to the
/// CPU path when that fails, so a false positive here is harmless).
///
/// The cheap gate is just "can we load the OpenCL ICD loader?". Boards that ship
/// one: Adreno (Qualcomm QCS6490 / Radxa Dragon Q6A), Mali (RK3588), PowerVR.
/// Raspberry Pi (VideoCore) has no production OpenCL, so the loader is absent and
/// the feature stays dormant.
///
/// Set <c>POLARIS_DISABLE_GPU=1</c> to force the GPU path off (debugging /
/// A-B timing against the CPU path).
/// </summary>
public static class OpenClRuntime {
    /// <summary>ICD loader / framework names tried, in order, across platforms.</summary>
    internal static readonly string[] LoaderCandidates = {
        "libOpenCL.so.1", // Linux ICD loader (ocl-icd / vendor)
        "libOpenCL.so",
        "OpenCL",          // Windows OpenCL.dll, and a last-chance Linux soname
        "/System/Library/Frameworks/OpenCL.framework/OpenCL", // macOS (dev only)
    };

    private static readonly Lazy<bool> _loaderPresent = new(ProbeLoader);

    /// <summary>True when the OpenCL ICD loader is loadable on this machine.
    /// This is the cheap gate; <see cref="OpenClContext"/> is the final arbiter
    /// of whether a usable GPU device + working compiler are actually present.</summary>
    public static bool LoaderPresent => _loaderPresent.Value;

    /// <summary>True when the user hasn't force-disabled the GPU path.</summary>
    public static bool Enabled =>
        Environment.GetEnvironmentVariable("POLARIS_DISABLE_GPU") != "1";

    /// <summary>Cheap "should we even try the GPU?" gate (env + loader present,
    /// and no vendor Mali driver left without its kernel device).</summary>
    public static bool IsAvailable => Enabled && LoaderPresent && StrandedMali == null;

    /// <summary>Human-readable summary of the probe result (for logs / status).</summary>
    public static string Diagnostics {
        get {
            if (!Enabled) return "GPU disabled via POLARIS_DISABLE_GPU=1";
            if (!LoaderPresent)
                return "GPU unavailable: no OpenCL ICD loader (libOpenCL) on this system";
            if (StrandedMali is { } why) return why;
            return "OpenCL ICD loader present (device probed at first use)";
        }
    }

    private static readonly Lazy<string?> _strandedMali = new(() => {
        try {
            var icds = Directory.Exists("/etc/OpenCL/vendors")
                ? Directory.GetFiles("/etc/OpenCL/vendors", "*.icd")
                    .Select(f => { try { return File.ReadAllText(f).Trim(); } catch { return ""; } })
                    .Where(s => s.Length > 0).ToList()
                : new List<string>();
            bool maliDevice = Directory.Exists("/dev")
                && Directory.GetFiles("/dev", "mali*").Length > 0;
            return StrandedMaliReason(icds, maliDevice);
        } catch { return null; }
    });

    /// <summary>Why the installed OpenCL drivers cannot run here, or null.</summary>
    public static string? StrandedMali => _strandedMali.Value;

    /// <summary>The vendor Mali OpenCL (libmali) talks to the vendor kernel's
    /// /dev/mali0. Under a mainline kernel (panthor) there is no such device,
    /// and the first OpenCL call segfaults the process ("No mali devices
    /// found"), which put an Orange Pi 5 Pro moved to Armbian's 6.18 kernel
    /// into a crash loop. Refused only when every installed driver is libmali:
    /// with another one present (Mesa's rusticl) the loader still loads
    /// libmali, so that case is left to the crash guard.</summary>
    internal static string? StrandedMaliReason(IReadOnlyCollection<string> icdLibraries, bool maliDevicePresent) {
        if (maliDevicePresent || icdLibraries.Count == 0) return null;
        if (!icdLibraries.All(l => Path.GetFileName(l).StartsWith("libmali", StringComparison.OrdinalIgnoreCase)))
            return null;
        return "GPU unavailable: the installed OpenCL driver is the vendor Mali one (libmali), which needs "
            + "the vendor kernel's /dev/mali0, and this kernel has none (a mainline kernel drives the GPU "
            + "with panthor). Install mesa-opencl-icd for OpenCL on this kernel, or boot the vendor kernel.";
    }

    private static bool ProbeLoader() {
        try {
            foreach (var name in LoaderCandidates) {
                if (NativeLibrary.TryLoad(name, out var h)) {
                    try { NativeLibrary.Free(h); } catch { }
                    return true;
                }
            }
        } catch { /* resolver threw -> treat as absent */ }
        return false;
    }
}
