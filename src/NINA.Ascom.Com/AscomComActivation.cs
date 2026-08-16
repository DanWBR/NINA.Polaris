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

using System.Runtime.Versioning;
using Microsoft.Win32;

namespace NINA.Ascom.Com;

/// <summary>
/// WINEXIT-3 diagnostic: a single choke point for activating an ASCOM COM
/// driver, so the exact failure mode is captured before the process can die.
///
/// <para>Real ASCOM camera / filter-wheel drivers took the whole (64-bit) host
/// down on connect, while the simulators + EQMOD were fine. The leading cause
/// is BITNESS: Polaris runs 64-bit; NINA desktop runs 32-bit precisely because
/// many ASCOM drivers (and their vendor SDKs) are 32-bit, and a 64-bit process
/// cannot load a 32-bit in-process COM server.</para>
///
/// <para>This helper (1) reads the driver's registered bitness and, when it is
/// 32-bit only in a 64-bit host, REFUSES with a clean, actionable message
/// instead of attempting an activation that crashes; and (2) writes a
/// synchronously-flushed breadcrumb around <c>CreateInstance</c> to
/// <c>%LOCALAPPDATA%/NINA.Polaris/logs/ascom_activation.log</c>, so if a
/// genuinely 64-bit driver still dies natively, the last line pinpoints whether
/// it was the activation or the driver's own Connect.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class AscomComActivation {

    /// <summary>Create the COM object for a ProgID, refusing a 32-bit-only
    /// driver in a 64-bit host with a clear error rather than a crash.</summary>
    public static object Create(string progId) {
        Log($"activate begin progId={progId} host={(Environment.Is64BitProcess ? "64-bit" : "32-bit")}");
        var (has64, has32, clsid) = ProbeBitness(progId);
        Log($"registry clsid={clsid} inproc64={has64} inproc32={has32}");

        if (Environment.Is64BitProcess && has32 && !has64) {
            var msg = $"The ASCOM driver '{progId}' is registered 32-bit only " +
                      $"(CLSID {clsid}). Polaris runs 64-bit and cannot load a 32-bit " +
                      "in-process ASCOM driver. Use this device over INDI or Alpaca, or a " +
                      "64-bit build of the driver.";
            Log("REFUSED (bitness): " + msg);
            throw new NotSupportedException(msg);
        }

        var t = Type.GetTypeFromProgID(progId)
            ?? throw new NotSupportedException($"ASCOM ProgID '{progId}' is not registered.");
        Log("about to CreateInstance");
        var obj = Activator.CreateInstance(t)
            ?? throw new NotSupportedException($"CreateInstance returned null for '{progId}'.");
        Log("CreateInstance OK");
        return obj;
    }

    /// <summary>Breadcrumb, for the adapters to bracket the driver's own
    /// <c>Connected = true</c> — the other place a real driver can die.</summary>
    public static void Note(string message) => Log(message);

    /// <summary>Public view of a driver's registered in-proc bitness, so a
    /// factory can decide whether to host it out-of-process. <c>has32 &amp;&amp;
    /// !has64</c> means a 32-bit-only in-proc driver the 64-bit host cannot
    /// load directly (route it through the win-x86 <see cref="AscomHostChannel"/>).</summary>
    public static (bool has64, bool has32) RegisteredBitness(string progId) {
        var (h64, h32, _) = ProbeBitness(progId);
        return (h64, h32);
    }

    /// <summary>Turn a failure from the driver's <c>Connected = true</c> into a
    /// clear, HRESULT-tagged message. A failing COM property set surfaces through
    /// the C# dynamic binder as an unhelpful <c>NullReferenceException</c>
    /// ("Object reference not set…"); this preserves the real HRESULT (e.g.
    /// 0x80004003 E_POINTER when the driver has no device selected) and points
    /// the operator at the driver's own setup, instead of a raw null-ref.</summary>
    public static Exception ConnectFailed(string progId, Exception inner) {
        var hr = inner.HResult;
        Log($"connect FAILED progId={progId} hr=0x{hr:X8} {inner.GetType().Name}: {inner.Message}");
        var msg = $"The ASCOM driver '{progId}' refused to connect (HRESULT 0x{hr:X8}). " +
                  "Check that a device is selected and available in the driver's own Setup dialog, " +
                  "and that no other program is holding it.";
        return new InvalidOperationException(msg, inner);
    }

    /// <summary>Which registry views carry the ProgID's InprocServer32.</summary>
    private static (bool has64, bool has32, string clsid) ProbeBitness(string progId) {
        string clsid = "";
        try {
            using var cr = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Default);
            using var pk = cr.OpenSubKey($@"{progId}\CLSID");
            clsid = pk?.GetValue(null) as string ?? "";
        } catch { /* unreadable → treat as unknown */ }

        bool Has(RegistryView view) {
            if (clsid.Length == 0) return false;
            try {
                using var cr = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view);
                using var k = cr.OpenSubKey($@"CLSID\{clsid}\InprocServer32");
                return k != null;
            } catch { return false; }
        }
        return (Has(RegistryView.Registry64), Has(RegistryView.Registry32), clsid);
    }

    private static readonly object _ioLock = new();

    private static void Log(string message) {
        try {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NINA.Polaris", "logs");
            Directory.CreateDirectory(dir);
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}";
            lock (_ioLock) File.AppendAllText(Path.Combine(dir, "ascom_activation.log"), line);
        } catch { /* diagnostics must never throw */ }
    }
}
