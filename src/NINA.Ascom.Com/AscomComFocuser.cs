using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NINA.Image.Interfaces;

namespace NINA.Ascom.Com;

/// <summary>
/// ASCOM Platform Focuser (IFocuserV3) adapter exposed through
/// <see cref="IFocuser"/>. Late-binds via IDispatch (see
/// <see cref="ComMember"/>) instead of C# <c>dynamic</c> because some
/// focuser drivers don't expose every property through the DLR-friendly
/// default dispatch interface (we hit
/// <c>'System.__ComObject' does not contain a definition for 'Connected'</c>
/// on a real driver). InvokeMember goes through
/// <c>IDispatch::GetIDsOfNames</c> which the driver must support to be
/// ASCOM-compliant.
///
/// <para>Covers the surface auto-focus and the live-stack trigger
/// orchestrator actually call: connect, Position / MaxStep, Move,
/// Halt, Temperature. Relative moves are translated to absolute moves
/// per the ASCOM spec (Absolute drivers reject Move with a negative
/// argument).</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AscomComFocuser : IFocuser, IDisposable {
    private readonly string _progId;
    private readonly AscomComStaDispatcher _disp;
    private object? _driver;
    private string _deviceName = "ASCOM Focuser";
    private bool _absolute = true;
    private int _maxStep = 100000;

    public AscomComFocuser(string progId) {
        _progId = progId ?? throw new ArgumentNullException(nameof(progId));
        _disp = new AscomComStaDispatcher($"ASCOM-Focuser-{progId}");
    }

    public string DeviceName => _deviceName;
    public bool IsConnected => _driver != null
        && _disp.Invoke(() => SafeGet(() => ComMember.Get<bool>(_driver!, "Connected"))).Result;
    public int Position    => Read(() => ComMember.Get<int>(_driver!, "Position"), 0);
    public int MaxPosition => _maxStep;
    public double Temperature => Read(() => ComMember.Get<double>(_driver!, "Temperature"), double.NaN);
    public bool IsMoving   => Read(() => ComMember.Get<bool>(_driver!, "IsMoving"));

    public Task ConnectAsync(CancellationToken ct = default) => _disp.Invoke(() => {
        var t = Type.GetTypeFromProgID(_progId)
            ?? throw new InvalidOperationException(
                $"ASCOM driver '{_progId}' is not registered.");
        _driver = Activator.CreateInstance(t)
            ?? throw new InvalidOperationException(
                $"ASCOM driver '{_progId}' failed to instantiate.");
        ComMember.Set(_driver!, "Connected", true);
        try { _deviceName = ComMember.Get<string>(_driver!, "Name"); }
        catch { _deviceName = _progId; }
        _absolute = SafeGet(() => ComMember.Get<bool>(_driver!, "Absolute"));
        _maxStep  = SafeGet(() => ComMember.Get<int>(_driver!, "MaxStep"), 100000);
    });

    public Task DisconnectAsync(CancellationToken ct = default) => _disp.Invoke(() => {
        if (_driver == null) return;
        try { ComMember.Set(_driver!, "Connected", false); } catch { }
        try { Marshal.FinalReleaseComObject(_driver); } catch { }
        _driver = null;
    });

    public Task MoveAbsoluteAsync(int position, CancellationToken ct = default)
        => _disp.Invoke(() => {
            if (_driver == null) return;
            var clamped = Math.Clamp(position, 0, _maxStep);
            ComMember.Call(_driver!, "Move", clamped);
        });

    public Task MoveRelativeAsync(int steps, CancellationToken ct = default)
        => _disp.Invoke(() => {
            if (_driver == null) return;
            if (_absolute) {
                // Absolute drivers want a target step, not a delta.
                var cur = SafeGet(() => ComMember.Get<int>(_driver!, "Position"));
                var clamped = Math.Clamp(cur + steps, 0, _maxStep);
                ComMember.Call(_driver!, "Move", clamped);
            } else {
                // Relative drivers (rare these days) accept signed
                // deltas directly.
                ComMember.Call(_driver!, "Move", steps);
            }
        });

    public Task AbortAsync(CancellationToken ct = default) => _disp.Invoke(() => {
        if (_driver == null) return;
        try { ComMember.Call(_driver!, "Halt"); }
        catch { /* not all drivers implement Halt */ }
    });

    public void Dispose() {
        try { DisconnectAsync().GetAwaiter().GetResult(); } catch { }
        _disp.Dispose();
    }

    private T Read<T>(Func<T> read, T fallback = default!) =>
        _driver == null ? fallback
        : _disp.Invoke(() => SafeGet(read, fallback)).GetAwaiter().GetResult();

    private static T SafeGet<T>(Func<T> read, T fallback = default!) {
        try { return read(); } catch { return fallback; }
    }
}
