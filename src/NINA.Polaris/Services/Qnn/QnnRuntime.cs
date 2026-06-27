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

namespace NINA.Polaris.Services.Qnn;

/// <summary>
/// Cheap, side-effect-free probe for whether this machine can run GraXpert AI
/// models on the Qualcomm Hexagon NPU (HTP) via the QAIRT (Qualcomm AI Runtime,
/// formerly QNN) tools. The Hexagon is the NPU on the Radxa Dragon Q6A
/// (QCS6490 / QCM6490, Hexagon V68) — the counterpart to <c>Services/Rknn</c>'s
/// Rockchip path. Like that path the gate is deliberately conservative: it must
/// look like a Qualcomm SBC (Linux + arm64) with the cDSP FastRPC bridge up
/// (<c>/dev/fastrpc-cdsp</c>) and the QAIRT runtime present (the
/// <c>qnn-net-run</c> tool + <c>libQnnHtp.so</c>). NOTE: this board's HTP is
/// integer-only (INT8/INT16, no FP16), so the bundled models are quantized.
///
/// The real authority is whether a <see cref="QnnSession"/> actually runs a
/// model; the inference service always falls back to the GraXpert CLI on any
/// failure, so a false positive here is harmless.
///
/// <para>The QAIRT runtime is located under <see cref="QairtRoot"/> — set
/// <c>POLARIS_QAIRT_ROOT</c> to override, else the bundled location the .deb
/// installs (<c>/opt/polaris/qairt</c>). Within it we expect
/// <c>bin/qnn-net-run</c>, <c>lib/libQnnHtp.so</c> + the matching Hexagon skel
/// under <c>dsp/</c> (used as <c>ADSP_LIBRARY_PATH</c>).</para>
///
/// <para>Set <c>POLARIS_DISABLE_NPU=1</c> (all NPU paths) or
/// <c>POLARIS_DISABLE_QNN=1</c> (just this one) to force it off.</para>
/// </summary>
public static class QnnRuntime {
    private static readonly Lazy<bool> _available = new(Probe);
    private static readonly Lazy<string> _diagnostics = new(BuildDiagnostics);

    /// <summary>True when this machine looks capable of running QNN/HTP models.</summary>
    public static bool IsAvailable => _available.Value;

    /// <summary>Human-readable summary of the probe result (for logs / status).</summary>
    public static string Diagnostics => _diagnostics.Value;

    /// <summary>Root of the bundled/extracted QAIRT runtime (tools + libs + skel).
    /// <c>POLARIS_QAIRT_ROOT</c> overrides; default is the .deb install location.</summary>
    public static string QairtRoot =>
        Environment.GetEnvironmentVariable("POLARIS_QAIRT_ROOT") is { Length: > 0 } r
            ? r : "/opt/polaris/qairt";

    /// <summary>Path to the <c>qnn-net-run</c> tool (QairtRoot/bin), else just the
    /// tool name to resolve on PATH.</summary>
    public static string NetRunPath {
        get {
            var p = Path.Combine(QairtRoot, "bin", "qnn-net-run");
            return File.Exists(p) ? p : "qnn-net-run";
        }
    }

    /// <summary>Path to <c>libQnnHtp.so</c> (the HTP backend), under QairtRoot/lib.</summary>
    public static string HtpBackendPath => Path.Combine(QairtRoot, "lib", "libQnnHtp.so");

    /// <summary>Directory holding the Hexagon DSP skel(s) (<c>libQnnHtpV*Skel.so</c>),
    /// used as <c>ADSP_LIBRARY_PATH</c> when launching <c>qnn-net-run</c>.</summary>
    public static string AdspLibraryPath => Path.Combine(QairtRoot, "dsp");

    private static bool Disabled() =>
        Environment.GetEnvironmentVariable("POLARIS_DISABLE_NPU") == "1" ||
        Environment.GetEnvironmentVariable("POLARIS_DISABLE_QNN") == "1";

    private static bool Probe() {
        try {
            if (Disabled()) return false;
            if (!OperatingSystem.IsLinux()) return false;
            if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64) return false;
            if (!HasFastRpc()) return false;       // cDSP bridge up
            if (!HasRuntime()) return false;       // QAIRT tools + HTP backend present
            return true;
        } catch {
            return false;
        }
    }

    /// <summary>The Hexagon is reachable only through the FastRPC bridge; the
    /// kernel exposes it as <c>/dev/fastrpc-cdsp</c> once the cDSP firmware has
    /// booted (confirmed present on the Radxa OS image).</summary>
    private static bool HasFastRpc() {
        try {
            return File.Exists("/dev/fastrpc-cdsp") || File.Exists("/dev/fastrpc-cdsp-secure");
        } catch { return false; }
    }

    /// <summary>The QAIRT runtime is bundled, not apt-installed (the public x86
    /// SDK and the device's apt runtime are version-locked), so we look for the
    /// <c>qnn-net-run</c> tool + <c>libQnnHtp.so</c> under <see cref="QairtRoot"/>.</summary>
    private static bool HasRuntime() {
        try {
            bool tool = File.Exists(Path.Combine(QairtRoot, "bin", "qnn-net-run"));
            bool htp = File.Exists(HtpBackendPath);
            return tool && htp;
        } catch { return false; }
    }

    private static string BuildDiagnostics() {
        if (Disabled()) return "NPU disabled via POLARIS_DISABLE_NPU/QNN=1";
        if (!OperatingSystem.IsLinux()) return "NPU unavailable: not Linux";
        if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
            return $"NPU unavailable: arch {RuntimeInformation.ProcessArchitecture} (need arm64)";
        if (!HasFastRpc())
            return "NPU unavailable: no /dev/fastrpc-cdsp (Hexagon cDSP bridge not up)";
        if (!HasRuntime())
            return $"NPU unavailable: QAIRT runtime not found under {QairtRoot} " +
                   "(need bin/qnn-net-run + lib/libQnnHtp.so; bundled in the arm64 .deb)";
        return "NPU available (Qualcomm Hexagon HTP via QAIRT detected)";
    }
}
