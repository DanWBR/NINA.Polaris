using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NINA.Image.Interfaces;

namespace NINA.Ascom.Com;

/// <summary>
/// ASCOM Platform FilterWheel (IFilterWheelV2) adapter exposed through
/// <see cref="IFilterWheel"/>. Late-binds via IDispatch (see
/// <see cref="ComMember"/>) — same rationale as
/// <see cref="AscomComFocuser"/>, some drivers don't surface
/// <c>Connected</c> on the default <c>dynamic</c> dispatch interface.
///
/// <para>Position semantics: ASCOM uses -1 as the "still moving"
/// sentinel for the Position property. We translate that to the
/// previous known position so callers polling don't see a transient
/// negative value; <see cref="IsMoving"/> is true while the wheel
/// settles.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AscomComFilterWheel : IFilterWheel, IDisposable {
    private readonly string _progId;
    private readonly AscomComStaDispatcher _disp;
    private object? _driver;
    private string _deviceName = "ASCOM Filter Wheel";
    private string[] _names = Array.Empty<string>();
    private int _lastPosition;

    public AscomComFilterWheel(string progId) {
        _progId = progId ?? throw new ArgumentNullException(nameof(progId));
        _disp = new AscomComStaDispatcher($"ASCOM-FilterWheel-{progId}");
    }

    public string DeviceName => _deviceName;
    public bool IsConnected => _driver != null
        && _disp.Invoke(() => SafeGet(() => ComMember.Get<bool>(_driver!, "Connected"))).Result;

    public int Position {
        get {
            // Position is VT_I2 (short) per the ASCOM spec; ComMember
            // converts to int. -1 sentinel means "still settling".
            var raw = Read(() => ComMember.Get<int>(_driver!, "Position"), -1);
            if (raw < 0) return _lastPosition;
            _lastPosition = raw;
            return raw;
        }
    }

    public bool IsMoving =>
        Read(() => ComMember.Get<int>(_driver!, "Position"), -1) < 0;

    public string[] FilterNames => _names;
    public int FilterCount => _names.Length;
    public string CurrentFilterName {
        get {
            var p = Position;
            return (p >= 0 && p < _names.Length) ? _names[p] : "";
        }
    }

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
        // Names is an ASCOM SAFEARRAY of strings; IDispatch hands it
        // back as a System.Array of strings underneath.
        try {
            var arr = (Array)ComMember.Get<object>(_driver!, "Names");
            _names = new string[arr.Length];
            for (int i = 0; i < arr.Length; i++)
                _names[i] = arr.GetValue(i)?.ToString() ?? $"Slot {i + 1}";
        } catch {
            _names = Array.Empty<string>();
        }
        _lastPosition = SafeGet(() => ComMember.Get<int>(_driver!, "Position"), 0);
        if (_lastPosition < 0) _lastPosition = 0;
    });

    public Task DisconnectAsync(CancellationToken ct = default) => _disp.Invoke(() => {
        if (_driver == null) return;
        try { ComMember.Set(_driver!, "Connected", false); } catch { }
        try { Marshal.FinalReleaseComObject(_driver); } catch { }
        _driver = null;
    });

    public Task SetPositionAsync(int position, CancellationToken ct = default)
        => _disp.Invoke(() => {
            if (_driver == null) return;
            var slot = Math.Clamp(position, 0, Math.Max(0, _names.Length - 1));
            // ASCOM expects a VT_I2 — pass it as short so IDispatch
            // doesn't reject the call with DISP_E_BADVARTYPE.
            ComMember.Set(_driver!, "Position", (short)slot);
        });

    public Task SetFilterByNameAsync(string filterName, CancellationToken ct = default) {
        if (string.IsNullOrEmpty(filterName)) return Task.CompletedTask;
        var idx = Array.FindIndex(_names, n =>
            string.Equals(n, filterName, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
            throw new ArgumentException(
                $"Filter '{filterName}' not found in wheel (have: {string.Join(", ", _names)}).",
                nameof(filterName));
        return SetPositionAsync(idx, ct);
    }

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
