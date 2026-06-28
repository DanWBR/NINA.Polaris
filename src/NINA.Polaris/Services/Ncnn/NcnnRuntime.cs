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

namespace NINA.Polaris.Services.Ncnn;

/// <summary>
/// Cheap, side-effect-free probe for whether this machine can run ncnn models on
/// a Vulkan GPU. The gate: <c>libncnn.so</c> must be loadable AND a Vulkan
/// loader (<c>libvulkan.so.1</c>) must be present (ncnn's Vulkan backend needs
/// it). This is the open, vendor-neutral GPU lane — it runs on the Adreno 643 of
/// the Radxa Dragon Q6A via Turnip, on Mali, Intel, etc.
///
/// This is only a fast pre-check; the real authority is whether
/// <see cref="NcnnSession"/> loads a model and produces finite output (the
/// inference service falls back to the GraXpert CLI on any failure, so a false
/// positive here is harmless — one failed run, then fallback).
///
/// Set <c>POLARIS_DISABLE_NCNN=1</c> (or the shared <c>POLARIS_DISABLE_NPU=1</c>)
/// to force this path off for A/B timing against the CPU path.
///
/// Scope note: gated to Linux for now because that's where the lane is packaged
/// (libncnn.so bundled for linux-arm64). ncnn itself is cross-platform; Windows
/// support can follow once the native lib is packaged there too.
/// </summary>
public static class NcnnRuntime {
    private static readonly Lazy<bool> _available = new(Probe);
    private static readonly Lazy<string> _diagnostics = new(BuildDiagnostics);

    /// <summary>True when this machine looks capable of running ncnn-Vulkan models.</summary>
    public static bool IsAvailable => _available.Value;

    /// <summary>Human-readable summary of the probe result (for logs / status).</summary>
    public static string Diagnostics => _diagnostics.Value;

    private static bool Disabled =>
        Environment.GetEnvironmentVariable("POLARIS_DISABLE_NCNN") == "1" ||
        Environment.GetEnvironmentVariable("POLARIS_DISABLE_NPU") == "1";

    private static bool Probe() {
        try {
            if (Disabled) return false;
            if (!OperatingSystem.IsLinux()) return false;
            if (!CanLoad("vulkan", "libvulkan.so.1", "libvulkan.so")) return false;
            if (!CanLoad("ncnn", "libncnn.so")) return false;
            return true;
        } catch {
            return false;
        }
    }

    private static bool CanLoad(params string[] candidates) {
        foreach (var c in candidates) {
            // 1. default OS search (LD_LIBRARY_PATH, /etc/ld.so.cache, /usr/lib …) —
            //    finds system libs like libvulkan.so.1.
            if (NativeLibrary.TryLoad(c, out var h)) {
                try { NativeLibrary.Free(h); } catch { }
                return true;
            }
            // 2. the app's own directory. The .deb installs libncnn.so next to the
            //    app (/opt/polaris), which is NOT on the default dlopen path — the
            //    real [DllImport("ncnn")] resolves it via the .NET native search
            //    dirs, but a bare TryLoad doesn't, so probe the absolute path too.
            try {
                var p = Path.Combine(AppContext.BaseDirectory, c);
                if (File.Exists(p) && NativeLibrary.TryLoad(p, out var h2)) {
                    try { NativeLibrary.Free(h2); } catch { }
                    return true;
                }
            } catch { /* not present in app dir → keep probing */ }
        }
        return false;
    }

    private static string BuildDiagnostics() {
        if (Disabled) return "ncnn-Vulkan disabled via POLARIS_DISABLE_NCNN/NPU=1";
        if (!OperatingSystem.IsLinux())
            return "ncnn-Vulkan unavailable: not Linux (lib packaged for linux-arm64)";
        if (!CanLoad("vulkan", "libvulkan.so.1", "libvulkan.so"))
            return "ncnn-Vulkan unavailable: no Vulkan loader (install mesa-vulkan-drivers / libvulkan1)";
        if (!CanLoad("ncnn", "libncnn.so"))
            return "ncnn-Vulkan unavailable: libncnn.so not loadable";
        return "ncnn-Vulkan available (libncnn + Vulkan loader present)";
    }
}
