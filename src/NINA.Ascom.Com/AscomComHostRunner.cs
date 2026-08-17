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
using System.Text;
using System.Text.Json;

namespace NINA.Ascom.Com;

/// <summary>
/// WINEXIT-2: driver-side of the out-of-process ASCOM filter-wheel host. The
/// Polaris exe re-launched with <c>--ascom-com-host</c> runs this: it hosts one
/// <see cref="ASCOM.Com.DriverAccess.FilterWheel"/> on an
/// <see cref="AscomComStaDispatcher"/> (STA + message pump) and serves it over a
/// newline-delimited JSON protocol on stdin/stdout.
///
/// <para>Why a child at all: an old WinForms/.NET-Framework driver (e.g. a DIY
/// MilkyWheel opening a serial port through <c>ASCOM.Utilities.Serial</c>)
/// fast-fails (0xC0000409) on <c>Connected = true</c> inside the loaded Kestrel
/// server process, yet connects (or throws a clean error) in a minimal child —
/// proven by probing the same driver both ways. So the driver lives here in the
/// clean child; the app marshals every call and a driver crash kills only this
/// process. Uses DriverAccess (the ASCOM Platform wrapper NINA uses), which is
/// what makes construction and connect succeed in the first place.</para>
///
/// <para>Protocol: <c>{"id":N,"op":"activate|get|set|ping|dispose",
/// "member":"Connected","value":true,"progId":"..."}</c> →
/// <c>{"id":N,"ok":true,"value":&lt;json&gt;}</c> or
/// <c>{"id":N,"ok":false,"error":"...","hr":N}</c>. Members are the filter-wheel
/// subset the adapter needs: Connected, Name, Names, Position.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class AscomComHostRunner {

    public static async Task<int> RunAsync() {
        var enc = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var protocol = new StreamWriter(Console.OpenStandardOutput(), enc) { AutoFlush = true };
        var input = new StreamReader(Console.OpenStandardInput(), enc);
        try { Console.SetOut(Console.Error); } catch { }

        AscomComActivation.Note("fw host-runner started (minimal child)");
        using var disp = new AscomComStaDispatcher("ascom-fw-host");
        await disp.ReadyAsync().ConfigureAwait(false);
        ASCOM.Com.DriverAccess.FilterWheel? fw = null;

        string? line;
        while ((line = await input.ReadLineAsync().ConfigureAwait(false)) != null) {
            if (line.Length == 0) continue;
            long id = 0;
            Dictionary<string, object?> resp;
            try {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                id = root.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var n) ? n : 0;
                var op = root.TryGetProperty("op", out var opEl) ? opEl.GetString() : null;
                (resp, fw) = await DispatchAsync(disp, fw, op, root).ConfigureAwait(false);
                if (op == "dispose") {
                    resp["id"] = id;
                    protocol.WriteLine(JsonSerializer.Serialize(resp));
                    break;
                }
            } catch (Exception ex) {
                resp = new() { ["ok"] = false, ["error"] = ex.Message, ["hr"] = ex.HResult };
            }
            resp["id"] = id;
            protocol.WriteLine(JsonSerializer.Serialize(resp));
        }

        var toRelease = fw;
        try {
            await disp.Invoke(() => {
                try { if (toRelease != null) toRelease.Connected = false; } catch { }
                try { toRelease?.Dispose(); } catch { }
            }).ConfigureAwait(false);
        } catch { }
        return 0;
    }

    private static async Task<(Dictionary<string, object?> resp, ASCOM.Com.DriverAccess.FilterWheel? fw)>
        DispatchAsync(AscomComStaDispatcher disp, ASCOM.Com.DriverAccess.FilterWheel? fw,
                      string? op, JsonElement root) {
        switch (op) {
            case "ping":
                return (Ok(null), fw);

            case "activate": {
                var progId = root.GetProperty("progId").GetString()
                    ?? throw new InvalidOperationException("activate missing progId");
                AscomComActivation.Note($"fw host CREATE {progId}");
                var created = await disp.Invoke(() => new ASCOM.Com.DriverAccess.FilterWheel(progId)).ConfigureAwait(false);
                AscomComActivation.Note($"fw host CREATE OK {progId}");
                return (Ok(null), created);
            }

            case "get": {
                var member = Member(root);
                var raw = await disp.Invoke(() => GetMember(Require(fw), member)).ConfigureAwait(false);
                return (Ok(raw), fw);
            }

            case "set": {
                var member = Member(root);
                var val = root.TryGetProperty("value", out var v) ? v : default;
                await disp.Invoke(() => SetMember(Require(fw), member, val)).ConfigureAwait(false);
                return (Ok(null), fw);
            }

            case "dispose": {
                var d = fw;
                await disp.Invoke(() => {
                    try { if (d != null) d.Connected = false; } catch { }
                    try { d?.Dispose(); } catch { }
                }).ConfigureAwait(false);
                return (Ok(null), null);
            }

            default:
                throw new InvalidOperationException($"unknown op '{op}'");
        }
    }

    private static object? GetMember(ASCOM.Com.DriverAccess.FilterWheel fw, string member) => member switch {
        "Connected" => fw.Connected,
        "Name" => fw.Name,
        "Names" => fw.Names,          // string[] → JSON array
        "Position" => (int)fw.Position,
        _ => throw new InvalidOperationException($"unknown member '{member}'")
    };

    private static void SetMember(ASCOM.Com.DriverAccess.FilterWheel fw, string member, JsonElement v) {
        switch (member) {
            case "Connected": fw.Connected = v.GetBoolean(); break;
            case "Position": fw.Position = (short)v.GetInt32(); break;
            default: throw new InvalidOperationException($"unknown member '{member}'");
        }
    }

    private static ASCOM.Com.DriverAccess.FilterWheel Require(ASCOM.Com.DriverAccess.FilterWheel? fw)
        => fw ?? throw new InvalidOperationException("driver not activated");

    private static string Member(JsonElement root)
        => root.TryGetProperty("member", out var m) ? (m.GetString() ?? "")
           : throw new InvalidOperationException("missing member");

    private static Dictionary<string, object?> Ok(object? value)
        => new() { ["ok"] = true, ["value"] = value };
}
