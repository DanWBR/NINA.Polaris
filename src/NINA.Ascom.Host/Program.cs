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
using NINA.Ascom.Host;

// WINEXIT-2 (#650): out-of-process ASCOM COM driver host. The parent
// (NINA.Ascom.Com.AscomHostChannel) launches this exe, then drives a single
// driver over a newline-delimited JSON protocol on stdin/stdout:
//
//   request : {"id":N,"op":"activate|get|set|call|setup|ping|dispose",
//              "member":"Connected","value":true,"vt":"bool","args":[...],
//              "progId":"ASCOM.X.FilterWheel"}
//   response: {"id":N,"ok":true,"value":<json>}
//             {"id":N,"ok":false,"error":"...","hr":<int>,"kind":"bitness|activation|com|host"}
//
// stdout carries ONLY protocol JSON; the driver's own stray Console writes are
// redirected to stderr so they can't corrupt the stream.

[assembly: SupportedOSPlatform("windows")]

return await HostMain.RunAsync();

[SupportedOSPlatform("windows")]
internal static class HostMain {
    private static readonly JsonSerializerOptions JsonOut = new() {
        WriteIndented = false
    };

    public static async Task<int> RunAsync() {
        // Capture the real stdout for the protocol BEFORE redirecting Console.
        var enc = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var protocol = new StreamWriter(Console.OpenStandardOutput(), enc) { AutoFlush = true };
        var input = new StreamReader(Console.OpenStandardInput(), enc);
        // Any stray driver Console.Write goes to stderr, never the protocol.
        try { Console.SetOut(Console.Error); } catch { }

        using var pump = new StaPump("ascom-host-driver");
        await pump.ReadyAsync().ConfigureAwait(false);
        var driver = new Driver();

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
                resp = await DispatchAsync(pump, driver, op, root).ConfigureAwait(false);

                if (op == "dispose") {
                    resp["id"] = id;
                    WriteResponse(protocol, resp);
                    break;
                }
            } catch (DriverError de) {
                resp = new() { ["ok"] = false, ["error"] = de.Message, ["hr"] = de.Hr, ["kind"] = de.Kind };
            } catch (Exception ex) {
                resp = new() { ["ok"] = false, ["error"] = ex.Message, ["hr"] = ex.HResult, ["kind"] = "host" };
            }
            resp["id"] = id;
            WriteResponse(protocol, resp);
        }

        // Parent closed stdin (or asked to dispose): tear the driver down on
        // its STA thread before the process exits.
        try { await pump.Invoke(driver.Dispose).ConfigureAwait(false); } catch { }
        return 0;
    }

    private static async Task<Dictionary<string, object?>> DispatchAsync(
        StaPump pump, Driver driver, string? op, JsonElement root) {
        switch (op) {
            case "ping":
                return Ok(null);

            case "activate": {
                var progId = root.GetProperty("progId").GetString()
                    ?? throw new DriverError("host", "activate missing progId");
                await pump.Invoke(() => driver.Activate(progId)).ConfigureAwait(false);
                return Ok(null);
            }

            case "get": {
                var member = Member(root);
                var raw = await pump.Invoke(() => driver.Get(member)).ConfigureAwait(false);
                return Ok(MarshalOut(raw));
            }

            case "set": {
                var member = Member(root);
                var vt = root.TryGetProperty("vt", out var vtEl) ? vtEl.GetString() : null;
                var val = root.TryGetProperty("value", out var v) ? MarshalIn(v, vt) : null;
                await pump.Invoke(() => driver.Set(member, val)).ConfigureAwait(false);
                return Ok(null);
            }

            case "call": {
                var member = Member(root);
                var args = new List<object?>();
                if (root.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array) {
                    foreach (var e in a.EnumerateArray()) args.Add(MarshalIn(e, null));
                }
                var raw = await pump.Invoke(() => driver.Call(member, args.ToArray())).ConfigureAwait(false);
                return Ok(MarshalOut(raw));
            }

            case "setup": {
                // SetupDialog is modal and blocks the STA thread until the user
                // closes it; the awaiting parent treats it as long-running.
                await pump.Invoke(() => driver.Call("SetupDialog", Array.Empty<object?>())).ConfigureAwait(false);
                return Ok(null);
            }

            case "dispose":
                await pump.Invoke(driver.Dispose).ConfigureAwait(false);
                return Ok(null);

            default:
                throw new DriverError("host", $"unknown op '{op}'");
        }
    }

    private static string Member(JsonElement root)
        => root.TryGetProperty("member", out var m) ? (m.GetString() ?? "")
           : throw new DriverError("host", "missing member");

    private static Dictionary<string, object?> Ok(object? value)
        => new() { ["ok"] = true, ["value"] = value };

    private static void WriteResponse(TextWriter w, Dictionary<string, object?> resp) {
        w.WriteLine(JsonSerializer.Serialize(resp, JsonOut));
    }

    /// <summary>COM return value → JSON-friendly value.</summary>
    private static object? MarshalOut(object? v) {
        switch (v) {
            case null: return null;
            case bool b: return b;
            case string s: return s;
            case short sh: return (int)sh;
            case int i: return i;
            case long l: return l;
            case byte by: return (int)by;
            case float f: return (double)f;
            case double d: return d;
            case Array arr: {
                // ASCOM string SAFEARRAYs (e.g. FilterWheel.Names) and the
                // occasional numeric array. Emit strings for the string case,
                // which is all the filter-wheel path needs.
                var list = new List<object?>(arr.Length);
                foreach (var e in arr) list.Add(e is null ? null : e.ToString());
                return list;
            }
            default: return v.ToString();
        }
    }

    /// <summary>JSON value (+ optional VARIANT-type hint) → CLR value the
    /// driver's IDispatch expects. ASCOM FilterWheel.Position is VT_I2, so the
    /// parent tags it "i2" to avoid DISP_E_BADVARTYPE.</summary>
    private static object? MarshalIn(JsonElement e, string? vt) {
        switch (vt) {
            case "bool": return e.GetBoolean();
            case "i2": return (short)e.GetInt32();
            case "i4": return e.GetInt32();
            case "r8": return e.GetDouble();
            case "str": return e.GetString();
        }
        return e.ValueKind switch {
            JsonValueKind.True or JsonValueKind.False => e.GetBoolean(),
            JsonValueKind.Number => e.TryGetInt64(out var n) ? n : e.GetDouble(),
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Null => null,
            _ => null
        };
    }
}
