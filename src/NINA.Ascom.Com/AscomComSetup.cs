using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NINA.Ascom.Com;

/// <summary>
/// Invokes an ASCOM driver's modal <c>SetupDialog()</c> method on a
/// dedicated STA thread. Used before Connect to let the user pick the
/// serial port, set the COM speed, configure tracking rates, etc. —
/// anything the driver author put in the setup form.
///
/// <para>The dialog blocks the dispatcher thread until the user
/// dismisses it; the returned Task completes only then. UI callers
/// should treat it as a long-running modal: show a spinner /
/// disable the rest of the panel while it's open.</para>
///
/// <para>Requires an interactive Windows session (Polaris started by
/// the logged-in user, not from a service). Running Polaris as a
/// SYSTEM service the SetupDialog() can't render anywhere — the
/// driver typically logs a "no interactive desktop" error and the
/// Task completes with an exception.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class AscomComSetup {

    /// <summary>Spawn a one-shot STA worker, create the driver via
    /// its ProgID, call <c>SetupDialog()</c>, and dispose. Never
    /// re-uses an existing connected instance, the dialog is meant
    /// to run BEFORE Connect when the driver might still be holding
    /// references to a previous (mis-)configuration.</summary>
    public static Task OpenSetupDialogAsync(string progId, CancellationToken ct = default) {
        if (string.IsNullOrWhiteSpace(progId)) throw new ArgumentException(nameof(progId));
        using var disp = new AscomComStaDispatcher($"ASCOM-Setup-{progId}");
        return disp.Invoke(() => {
            var t = Type.GetTypeFromProgID(progId)
                ?? throw new InvalidOperationException(
                    $"ASCOM driver '{progId}' is not registered.");
            dynamic driver = Activator.CreateInstance(t)
                ?? throw new InvalidOperationException(
                    $"ASCOM driver '{progId}' failed to instantiate.");
            try {
                driver.SetupDialog();
            } finally {
                try { Marshal.FinalReleaseComObject(driver); } catch { }
            }
        });
    }
}
