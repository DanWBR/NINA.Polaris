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

namespace NINA.Polaris.Services.Rknn;

/// <summary>
/// Cheap, side-effect-free probe for whether this machine can run RKNN models
/// on a Rockchip NPU. The gate is deliberately conservative: it must look like
/// an RK3588-class board (Linux + arm64), the RKNPU2 user-space runtime
/// (<c>librknnrt.so</c>) must be loadable, and a DRM render node must exist
/// (the modern RKNPU driver exposes the NPU as <c>/dev/dri/renderD12x</c>, not
/// <c>/dev/rknpu</c>).
///
/// This is only a fast pre-check to decide whether to *attempt* the NPU path.
/// The real authority is whether <see cref="RknnSession"/> initialises a model
/// without error; the inference service always falls back to the GraXpert CLI
/// when a session fails to load, so a false positive here is harmless (one
/// failed init, then fallback).
///
/// Set <c>POLARIS_DISABLE_NPU=1</c> to force the NPU path off (debugging /
/// A-B timing against the CPU path).
/// </summary>
public static class RknnRuntime {
    private static readonly Lazy<bool> _available = new(Probe);
    private static readonly Lazy<string> _diagnostics = new(BuildDiagnostics);

    /// <summary>True when this machine looks capable of running RKNN models.</summary>
    public static bool IsAvailable => _available.Value;

    /// <summary>Human-readable summary of the probe result (for logs / status).</summary>
    public static string Diagnostics => _diagnostics.Value;

    private static bool Probe() {
        try {
            if (Environment.GetEnvironmentVariable("POLARIS_DISABLE_NPU") == "1") return false;
            if (!OperatingSystem.IsLinux()) return false;
            if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64) return false;
            if (!HasRenderNode()) return false;
            if (!CanLoadRuntime()) return false;
            return true;
        } catch {
            return false;
        }
    }

    /// <summary>
    /// The RKNPU driver registers the NPU as a DRM render node. On an RK3588
    /// the GPU (Mali) and the NPU each get one; we can't tell which minor is
    /// which without opening them, so we accept "any render node exists" as the
    /// gate and let <see cref="RknnSession"/> be the final arbiter.
    /// </summary>
    private static bool HasRenderNode() {
        try {
            if (!Directory.Exists("/dev/dri")) return false;
            foreach (var e in Directory.EnumerateFileSystemEntries("/dev/dri")) {
                var name = Path.GetFileName(e);
                if (name.StartsWith("renderD", StringComparison.Ordinal)) return true;
            }
        } catch { /* /dev not enumerable → treat as absent */ }
        return false;
    }

    /// <summary>
    /// Can we load librknnrt.so? Uses the default resolver (app dir, the .deb's
    /// /usr/lib copy, LD_LIBRARY_PATH). We free the handle immediately; the
    /// DllImport calls re-resolve through the same path on first use.
    /// </summary>
    private static bool CanLoadRuntime() {
        foreach (var candidate in new[] { "librknnrt.so", "rknnrt" }) {
            if (NativeLibrary.TryLoad(candidate, out var h)) {
                try { NativeLibrary.Free(h); } catch { }
                return true;
            }
        }
        return false;
    }

    private static string BuildDiagnostics() {
        if (Environment.GetEnvironmentVariable("POLARIS_DISABLE_NPU") == "1")
            return "NPU disabled via POLARIS_DISABLE_NPU=1";
        if (!OperatingSystem.IsLinux())
            return "NPU unavailable: not Linux";
        if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
            return $"NPU unavailable: arch {RuntimeInformation.ProcessArchitecture} (need arm64)";
        if (!HasRenderNode())
            return "NPU unavailable: no /dev/dri/renderD* node (RKNPU driver missing)";
        if (!CanLoadRuntime())
            return "NPU unavailable: librknnrt.so not loadable (RKNPU2 runtime missing)";
        return "NPU available (RK3588 RKNPU2 runtime detected)";
    }
}
