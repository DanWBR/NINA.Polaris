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

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NINA.Ascom.Com;

/// <summary>
/// Per-driver STA worker thread. Every ASCOM driver instance is
/// pinned to its own dispatcher and all property reads, property
/// writes, and method invocations against that instance are funnelled
/// through here.
///
/// <para>Why STA-per-driver and not a shared STA pool: most ASCOM
/// drivers historically targeted VB6 / WinForms and rely on COM
/// apartment semantics, an MTA call into the driver routinely
/// crashes the underlying picker dialog, the driver setup form, or
/// the camera's image-ready callback. Pinning each driver to its
/// own thread also means a slow operation on one device (a 60-second
/// telescope slew) cannot block a different device on a different
/// thread (an autofocus loop on the focuser).</para>
///
/// <para>The cost is modest: ~1 MB stack + a kernel thread per
/// connected device. A typical rig (camera + mount + focuser +
/// filter-wheel) uses 4 threads, well below the cost of running INDI
/// + Alpaca clients in parallel.</para>
///
/// <para>All public methods are safe to call from any thread; the
/// dispatcher serialises everything internally. Tasks complete on
/// the .NET TPL default scheduler so awaiters do not bounce back to
/// the STA thread, only the work itself runs there.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AscomComStaDispatcher : IDisposable {
    private readonly Thread _thread;
    // WINEXIT-3: a plain ConcurrentQueue + a wake event, NOT a BlockingCollection.
    // The pump must ALSO service the Windows message queue (see Pump), so it
    // cannot sit blocked inside BlockingCollection.Take — it waits on
    // MsgWaitForMultipleObjectsEx, which wakes on either queued work OR an input
    // message.
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly TaskCompletionSource _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _disposed;
    private volatile bool _shutdown;

    public AscomComStaDispatcher(string name) {
        _thread = new Thread(Pump) {
            IsBackground = true,
            Name = name
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>Resolves once the pump is alive on its STA. Callers
    /// can await it before queuing work to ensure deterministic
    /// ordering during construction.</summary>
    public Task ReadyAsync() => _ready.Task;

    private void Pump() {
        // CoInitialize is implicit on STA threads via SetApartmentState,
        // signal readiness before the first dequeue so awaiters on
        // ReadyAsync don't race the first queued work.
        _ready.TrySetResult();

        // WINEXIT-3: a real STA COM host has to PUMP THE WINDOWS MESSAGE LOOP,
        // not just consume a work queue. Real ASCOM camera / filter-wheel
        // drivers (VB6 / WinForms lineage) create a hidden window on Connect and
        // deliver their events — image-ready, cooler, position callbacks — as
        // window messages; COM itself marshals cross-apartment calls the same
        // way. A thread that blocks on a queue and never pumps starves those
        // messages, and a real driver crashes the process on connect. The ASCOM
        // simulators and simple drivers like EQMOD don't post to their own
        // window, which is exactly why sims + the mount worked while real
        // cameras / wheels took the host down (field report).
        //
        // MsgWaitForMultipleObjectsEx wakes on EITHER the work event OR an input
        // message, so we drain both without a busy-loop.
        var handle = _wake.SafeWaitHandle.DangerousGetHandle();
        var handles = new[] { handle };
        while (!_shutdown) {
            // Run everything queued so far.
            while (_queue.TryDequeue(out var action)) {
                try { action(); }
                catch {
                    // Per-call exceptions are propagated through the per-call
                    // TaskCompletionSource by the Invoke* helpers; anything that
                    // escapes there is bookkeeping noise, swallow it to keep the
                    // pump (and the driver's message loop) alive.
                }
            }
            if (_shutdown) break;

            // Wait for new work or a window message, then service the messages.
            uint r = MsgWaitForMultipleObjectsEx(
                1, handles, INFINITE, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
            if (r == WAIT_OBJECT_0 + 1) {
                while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE)) {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            }
            // r == WAIT_OBJECT_0 (work event) just loops back to drain the queue.
        }
    }

    /// <summary>Run a synchronous Func on the STA thread, return the
    /// result. Exceptions in <paramref name="work"/> surface as the
    /// returned Task's exception.</summary>
    public Task<T> Invoke<T>(Func<T> work) {
        if (_disposed) throw new ObjectDisposedException(nameof(AscomComStaDispatcher));
        var tcs = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue(() => {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        _wake.Set();
        return tcs.Task;
    }

    /// <summary>Run a synchronous Action on the STA thread.</summary>
    public Task Invoke(Action work) {
        if (_disposed) throw new ObjectDisposedException(nameof(AscomComStaDispatcher));
        var tcs = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue(() => {
            try { work(); tcs.SetResult(); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        _wake.Set();
        return tcs.Task;
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _shutdown = true;
        _wake.Set();   // wake the pump so it sees _shutdown and exits
        // Best-effort join so the COM teardown on the STA thread (driver
        // Disconnect + ReleaseComObject) actually runs before the process moves
        // on. 2 s ceiling, hung drivers shouldn't wedge shutdown.
        try { _thread.Join(TimeSpan.FromSeconds(2)); } catch { }
        try { _wake.Dispose(); } catch { }
    }

    // ── Win32 message pump ───────────────────────────────────────────
    private const uint INFINITE = 0xFFFFFFFF;
    private const uint QS_ALLINPUT = 0x04FF;
    private const uint MWMO_INPUTAVAILABLE = 0x0004;
    private const uint WAIT_OBJECT_0 = 0;
    private const uint PM_REMOVE = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint MsgWaitForMultipleObjectsEx(
        uint nCount, IntPtr[] pHandles, uint dwMilliseconds, uint dwWakeMask, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(
        out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);
}