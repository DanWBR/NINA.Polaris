using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NINA.Ascom.Com;

/// <summary>
/// Invokes an ASCOM driver's modal <c>SetupDialog()</c> method on a
/// one-shot dedicated STA thread. Used before Connect to let the user
/// pick the serial port, set the COM speed, configure tracking rates,
/// etc. — anything the driver author put in the setup form.
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

    /// <summary>Spawn a dedicated one-shot STA thread, create the
    /// driver via its ProgID, call <c>SetupDialog()</c> via late-bound
    /// reflection (NOT the DLR — many old ZWO/ASCOM drivers fail when
    /// invoked through C# <c>dynamic</c>), and dispose. Lifetime of
    /// the thread matches the dialog: nothing tears down until the
    /// user dismisses the form.
    ///
    /// <para>Previously this went through the shared
    /// <see cref="AscomComStaDispatcher"/> with a <c>using</c> block,
    /// but the dispatcher's Dispose() runs synchronously the moment
    /// the method returns the Task — long before the modal dialog
    /// closes — so it Joined the still-busy STA thread for its 2s
    /// ceiling and then left the dispatcher in a half-disposed state.
    /// A one-shot thread per call is simpler and has no race.</para>
    ///
    /// <para>The thread is marked Background so it doesn't keep the
    /// process alive past shutdown, and is named after the ProgID so
    /// it shows up identifiably in the debugger / Process Explorer
    /// when a misbehaving driver hangs.</para>
    /// </summary>
    public static Task OpenSetupDialogAsync(string progId, CancellationToken ct = default) {
        if (string.IsNullOrWhiteSpace(progId)) throw new ArgumentException(nameof(progId));

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() => {
            object? driver = null;
            try {
                var t = Type.GetTypeFromProgID(progId)
                    ?? throw new InvalidOperationException(
                        $"ASCOM driver '{progId}' is not registered. " +
                        "Install or re-register it via the ASCOM Platform.");

                driver = Activator.CreateInstance(t)
                    ?? throw new InvalidOperationException(
                        $"ASCOM driver '{progId}' failed to instantiate.");

                // Late-bound via reflection rather than C# `dynamic`.
                // The DLR's overload resolution trips on a subset of
                // ASCOM drivers (notably ZWO's and several VB6-era
                // ones) because they expose SetupDialog with an
                // implicit `this` parameter that confuses the binder.
                // Type.InvokeMember talks to IDispatch the same way
                // VBScript does, which is what every driver was
                // tested against.
                t.InvokeMember(
                    "SetupDialog",
                    BindingFlags.InvokeMethod,
                    null, driver, null);

                tcs.TrySetResult();
            } catch (Exception ex) {
                // Includes COMException + InvalidOperationException +
                // anything else SetupDialog can throw. Surface via the
                // returned Task — the endpoint handler turns it into a
                // 400 with the message.
                tcs.TrySetException(ex);
            } finally {
                if (driver != null) {
                    try { Marshal.FinalReleaseComObject(driver); } catch { }
                }
            }
        }) {
            IsBackground = true,
            Name = $"ASCOM-Setup-{progId}"
        };
        thread.SetApartmentState(ApartmentState.STA);

        // Wrap Start in a defensive try so the caller sees a failed
        // Task on the rare case the OS can't spin a new thread up
        // (low resources, mostly).
        try { thread.Start(); }
        catch (Exception ex) { tcs.TrySetException(ex); }

        return tcs.Task;
    }

    // Module initializer: install a process-wide guard against the
    // AccessViolationException that pre-.NET-4-style ASCOM drivers
    // can throw from SetupDialog. By default a .NET 5+ process tears
    // down on a corrupted-state exception regardless of any try/catch
    // up the stack — which manifested to the user as "the server
    // crashes when I click Setup on the ZWO ASCOM driver". The legacy
    // policy lets a [HandleProcessCorruptedStateExceptions] handler
    // catch it; without it nothing can. We don't set HPCSE on the
    // method (it's obsolete in modern .NET); the runtime config flag
    // is the only working knob. Setting it at module load means the
    // first call to anything in this assembly turns it on, which is
    // fine: the assembly is only loaded on Windows when an ASCOM
    // adapter is actually wired up.
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void EnableLegacyCorruptedStateExceptionPolicy() {
        try {
            AppContext.SetSwitch("System.Runtime.LegacyCorruptedStateExceptionsPolicy", true);
        } catch { /* best-effort, not critical to startup */ }
    }
}
