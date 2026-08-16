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
using Microsoft.Win32;

namespace NINA.Ascom.Host;

/// <summary>A failure carrying the real COM HRESULT and a machine-readable
/// kind, so the parent can rebuild the same clean, HRESULT-tagged message the
/// in-process path produces.</summary>
internal sealed class DriverError : Exception {
    public int Hr { get; }
    public string Kind { get; }
    public DriverError(string kind, string message, int hr = 0) : base(message) {
        Kind = kind;
        Hr = hr;
    }
}

/// <summary>
/// Holds the single activated ASCOM COM object and does late-bound member
/// access through <c>IDispatch</c> (same rationale as
/// <c>NINA.Ascom.Com.ComMember</c>: many old drivers don't bind cleanly
/// through the C# DLR). Every method here MUST be called on the STA pump.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class Driver {
    private object? _obj;

    /// <summary>Activate the COM object for a ProgID. In the x86 child this is
    /// exactly how a 32-bit-only in-proc driver gets loaded; the bitness guard
    /// only trips for the degenerate case of a 32-bit driver in a 64-bit
    /// child (which the parent avoids by launching the x86 child instead).</summary>
    public void Activate(string progId) {
        var (has64, has32, clsid) = ProbeBitness(progId);
        if (Environment.Is64BitProcess && has32 && !has64) {
            throw new DriverError("bitness",
                $"The ASCOM driver '{progId}' is registered 32-bit only (CLSID {clsid}). " +
                "This 64-bit host cannot load it; the parent should launch the 32-bit host.");
        }
        var t = Type.GetTypeFromProgID(progId)
            ?? throw new DriverError("activation", $"ASCOM ProgID '{progId}' is not registered.");
        _obj = Activator.CreateInstance(t)
            ?? throw new DriverError("activation", $"CreateInstance returned null for '{progId}'.");
    }

    public object? Get(string member)
        => Invoke(member, BindingFlags.GetProperty, null);

    public void Set(string member, object? value)
        => Invoke(member, BindingFlags.SetProperty, new[] { value });

    public object? Call(string member, object?[] args)
        => Invoke(member, BindingFlags.InvokeMethod, args);

    private object? Invoke(string member, BindingFlags kind, object?[]? args) {
        if (_obj == null) throw new DriverError("com", "driver not activated");
        try {
            return _obj.GetType().InvokeMember(
                member, kind | BindingFlags.Public | BindingFlags.Instance,
                null, _obj, args);
        } catch (TargetInvocationException tie) when (tie.InnerException != null) {
            // The driver's own COM failure: preserve its HRESULT.
            var inner = tie.InnerException;
            throw new DriverError("com", inner.Message, inner.HResult);
        } catch (DriverError) {
            throw;
        } catch (Exception ex) {
            throw new DriverError("com", ex.Message, ex.HResult);
        }
    }

    public void Dispose() {
        if (_obj == null) return;
        try {
            if (Marshal.IsComObject(_obj)) Marshal.FinalReleaseComObject(_obj);
        } catch { }
        _obj = null;
    }

    /// <summary>Which registry views carry the ProgID's InprocServer32
    /// (copied from NINA.Ascom.Com.AscomComActivation.ProbeBitness).</summary>
    private static (bool has64, bool has32, string clsid) ProbeBitness(string progId) {
        string clsid = "";
        try {
            using var cr = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Default);
            using var pk = cr.OpenSubKey($@"{progId}\CLSID");
            clsid = pk?.GetValue(null) as string ?? "";
        } catch { }

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
}
