using System.Collections.Concurrent;
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
    private readonly BlockingCollection<Action> _queue = new();
    private readonly TaskCompletionSource _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

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
        try {
            foreach (var action in _queue.GetConsumingEnumerable()) {
                try { action(); }
                catch {
                    // Per-call exceptions are propagated through the
                    // per-call TaskCompletionSource by Invoke* helpers,
                    // anything that escapes here is bookkeeping noise we
                    // swallow to keep the pump alive.
                }
            }
        } catch (ObjectDisposedException) {
            // Race-safe shutdown signal: Dispose() can race the pump's
            // internal TryTake call (the foreach lowers to a
            // TryTakeWithNoTimeValidation that can throw if the
            // BlockingCollection is disposed mid-take). Treat the
            // exception as the legitimate "queue is gone, exit"
            // signal — the pump is shutting down either way.
        }
    }

    /// <summary>Run a synchronous Func on the STA thread, return the
    /// result. Exceptions in <paramref name="work"/> surface as the
    /// returned Task's exception.</summary>
    public Task<T> Invoke<T>(Func<T> work) {
        if (_disposed) throw new ObjectDisposedException(nameof(AscomComStaDispatcher));
        var tcs = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() => {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    /// <summary>Run a synchronous Action on the STA thread.</summary>
    public Task Invoke(Action work) {
        if (_disposed) throw new ObjectDisposedException(nameof(AscomComStaDispatcher));
        var tcs = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() => {
            try { work(); tcs.SetResult(); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        // CompleteAdding lets the pump's foreach exit cleanly: the
        // next TryTake sees IsAddingCompleted + empty, MoveNext
        // returns false, foreach exits, thread terminates.
        try { _queue.CompleteAdding(); } catch { }
        // Best-effort join so the COM teardown on the STA thread
        // (driver Disconnect + ReleaseComObject) actually runs before
        // the process moves on. 2 s ceiling, hung drivers shouldn't
        // wedge shutdown.
        try { _thread.Join(TimeSpan.FromSeconds(2)); } catch { }
        // Deliberately NOT calling _queue.Dispose() here.
        // BlockingCollection.Dispose races with any pump iteration
        // that's currently inside its internal TryTake (the foreach
        // body in Pump), producing ObjectDisposedException on the
        // pump thread. CompleteAdding + Join is enough to drain the
        // queue cleanly; the BlockingCollection itself becomes GC-
        // unreachable as soon as this dispatcher is collected.
    }
}
