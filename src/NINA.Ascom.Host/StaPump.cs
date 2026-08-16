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

namespace NINA.Ascom.Host;

/// <summary>
/// STA worker thread that also pumps the Windows message loop, so a real
/// ASCOM driver's hidden-window callbacks (image-ready, position, cooler)
/// and COM's own cross-apartment marshalling get serviced. This is a copy
/// of <c>NINA.Ascom.Com.AscomComStaDispatcher</c>, duplicated here so the
/// child host stays free of the NINA.Core / NINA.Image dependency chain and
/// its self-contained per-RID publish stays small. Keep the two in sync.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class StaPump : IDisposable {
    private readonly Thread _thread;
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly TaskCompletionSource _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _disposed;
    private volatile bool _shutdown;

    public StaPump(string name) {
        _thread = new Thread(Pump) { IsBackground = true, Name = name };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public Task ReadyAsync() => _ready.Task;

    private void Pump() {
        _ready.TrySetResult();
        var handle = _wake.SafeWaitHandle.DangerousGetHandle();
        var handles = new[] { handle };
        while (!_shutdown) {
            while (_queue.TryDequeue(out var action)) {
                try { action(); } catch { /* propagated via per-call TCS */ }
            }
            if (_shutdown) break;
            uint r = MsgWaitForMultipleObjectsEx(
                1, handles, INFINITE, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
            if (r == WAIT_OBJECT_0 + 1) {
                while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE)) {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            }
        }
    }

    public Task<T> Invoke<T>(Func<T> work) {
        if (_disposed) throw new ObjectDisposedException(nameof(StaPump));
        var tcs = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue(() => {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        _wake.Set();
        return tcs.Task;
    }

    public Task Invoke(Action work) {
        if (_disposed) throw new ObjectDisposedException(nameof(StaPump));
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
        _wake.Set();
        try { _thread.Join(TimeSpan.FromSeconds(2)); } catch { }
        try { _wake.Dispose(); } catch { }
    }

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
