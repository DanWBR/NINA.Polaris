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
using System.Runtime.Versioning;
using NINA.Image.Interfaces;

namespace NINA.Ascom.Com;

/// <summary>
/// ASCOM Platform Switch (ISwitchV2) adapter exposed through
/// <see cref="ISwitchDevice"/>. Covers power boxes, relay hubs and dew
/// controllers. Late-binds via IDispatch (see <see cref="ComMember"/>) for
/// the same driver-compat reasons as <see cref="AscomComFocuser"/>.
///
/// <para>ISwitchV2 exposes each channel by a <c>short</c> index: name,
/// value, min/max/step, and CanWrite. We classify a channel as boolean when
/// its range is exactly 0..1 with step 1 (the ASCOM convention for a
/// two-state switch); everything else is analog. Static descriptors are read
/// once on connect; only the live value is re-read per snapshot.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AscomComSwitch : ISwitchDevice, IDisposable {
    private readonly string _progId;
    private readonly AscomComStaDispatcher _disp;
    private object? _driver;
    private string _deviceName = "ASCOM Switch";

    private readonly record struct Descriptor(string Name, double Min, double Max, double Step, bool Writable, bool Boolean);
    private readonly List<Descriptor> _descriptors = new();

    public AscomComSwitch(string progId) {
        _progId = progId ?? throw new ArgumentNullException(nameof(progId));
        _disp = new AscomComStaDispatcher($"ASCOM-Switch-{progId}");
    }

    public string DeviceName => _deviceName;
    public bool IsConnected => _driver != null
        && _disp.Invoke(() => SafeGet(() => ComMember.Get<bool>(_driver!, "Connected"))).Result;

    public int SwitchCount => _descriptors.Count;

    public Task ConnectAsync(CancellationToken ct = default) => _disp.Invoke(() => {
        var t = Type.GetTypeFromProgID(_progId)
            ?? throw new InvalidOperationException($"ASCOM driver '{_progId}' is not registered.");
        _driver = Activator.CreateInstance(t)
            ?? throw new InvalidOperationException($"ASCOM driver '{_progId}' failed to instantiate.");
        ComMember.Set(_driver!, "Connected", true);
        try { _deviceName = ComMember.Get<string>(_driver!, "Name"); } catch { _deviceName = _progId; }
        BuildDescriptors();
    });

    public Task DisconnectAsync(CancellationToken ct = default) => _disp.Invoke(() => {
        if (_driver == null) return;
        try { ComMember.Set(_driver!, "Connected", false); } catch { }
        try { Marshal.FinalReleaseComObject(_driver); } catch { }
        _driver = null;
        _descriptors.Clear();
    });

    public Task RefreshAsync(CancellationToken ct = default) => _disp.Invoke(BuildDescriptors);

    private void BuildDescriptors() {
        _descriptors.Clear();
        if (_driver == null) return;
        int count = SafeGet(() => ComMember.Get<int>(_driver!, "MaxSwitch"), 0);
        for (short i = 0; i < count; i++) {
            var name = SafeGet(() => (string?)ComMember.Call(_driver!, "GetSwitchName", i) ?? $"Switch {i}", $"Switch {i}");
            double min = SafeGet(() => ToD(ComMember.Call(_driver!, "MinSwitchValue", i)), 0);
            double max = SafeGet(() => ToD(ComMember.Call(_driver!, "MaxSwitchValue", i)), 1);
            double step = SafeGet(() => ToD(ComMember.Call(_driver!, "SwitchStep", i)), 1);
            bool writable = SafeGet(() => ToB(ComMember.Call(_driver!, "CanWrite", i)), true);
            bool boolean = min == 0 && max == 1 && step == 1;
            _descriptors.Add(new Descriptor(name, min, max, step, writable, boolean));
        }
    }

    public IReadOnlyList<SwitchChannel> Channels {
        get {
            if (_driver == null) return Array.Empty<SwitchChannel>();
            return _disp.Invoke(() => {
                var list = new List<SwitchChannel>(_descriptors.Count);
                for (int i = 0; i < _descriptors.Count; i++) {
                    var d = _descriptors[i];
                    short idx = (short)i;
                    double value = SafeGet(() => ToD(ComMember.Call(_driver!, "GetSwitchValue", idx)), 0);
                    list.Add(new SwitchChannel(i, d.Name, d.Boolean, value, d.Min, d.Max, d.Step, d.Writable));
                }
                return (IReadOnlyList<SwitchChannel>)list;
            }).GetAwaiter().GetResult();
        }
    }

    public Task SetBoolAsync(int id, bool on, CancellationToken ct = default) => _disp.Invoke(() => {
        if (_driver == null) return;
        var d = Get(id);
        short idx = (short)id;
        if (d.Boolean) ComMember.Call(_driver!, "SetSwitch", idx, on);
        else ComMember.Call(_driver!, "SetSwitchValue", idx, on ? d.Max : d.Min);
    });

    public Task SetValueAsync(int id, double value, CancellationToken ct = default) => _disp.Invoke(() => {
        if (_driver == null) return;
        var d = Get(id);
        short idx = (short)id;
        if (d.Boolean) ComMember.Call(_driver!, "SetSwitch", idx, value != 0);
        else ComMember.Call(_driver!, "SetSwitchValue", idx,
            d.Max > d.Min ? Math.Clamp(value, d.Min, d.Max) : value);
    });

    public void Dispose() {
        try { DisconnectAsync().GetAwaiter().GetResult(); } catch { }
        _disp.Dispose();
    }

    private Descriptor Get(int id) {
        if (id < 0 || id >= _descriptors.Count)
            throw new ArgumentOutOfRangeException(nameof(id),
                $"Switch '{_deviceName}' has no channel {id} (have {_descriptors.Count}).");
        return _descriptors[id];
    }

    private static double ToD(object? raw) => raw == null ? 0 : System.Convert.ToDouble(raw);
    private static bool ToB(object? raw) => raw != null && System.Convert.ToBoolean(raw);
    private static T SafeGet<T>(Func<T> read, T fallback = default!) { try { return read(); } catch { return fallback; } }
}
