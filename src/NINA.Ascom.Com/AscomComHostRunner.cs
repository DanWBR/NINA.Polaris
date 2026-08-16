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
using System.Text;
using System.Text.Json;

namespace NINA.Ascom.Com;

/// <summary>
/// WINEXIT-2 (#650): the driver-side of the out-of-process ASCOM host. Runs one
/// COM driver on an <see cref="AscomComStaDispatcher"/> (STA + message pump) and
/// serves the same newline-delimited JSON protocol <see cref="AscomHostChannel"/>
/// speaks (activate / get / set / call / setup / ping / dispose).
///
/// <para>This is the canonical protocol implementation, shared by BOTH host
/// modes: the 64-bit host is the main Polaris exe re-launched with
/// <c>--ascom-com-host</c> (so a 64-bit-registered driver that still crashes the
/// host — e.g. a .NET AnyCPU MilkyWheel — dies in the child, not the app), and
/// the 32-bit host is the packaged <c>NINA.Ascom.Host.exe</c> for drivers
/// registered 32-bit only. A driver crash is an OS process exit the parent turns
/// into a clean error.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class AscomComHostRunner {

    public static async Task<int> RunAsync() {
        // Capture the real stdout for the protocol BEFORE redirecting Console, so
        // a driver's stray Console.Write can't corrupt the JSON stream.
        var enc = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var protocol = new StreamWriter(Console.OpenStandardOutput(), enc) { AutoFlush = true };
        var input = new StreamReader(Console.OpenStandardInput(), enc);
        try { Console.SetOut(Console.Error); } catch { }

        AscomComActivation.Note($"host-runner started ({(Environment.Is64BitProcess ? "64-bit" : "32-bit")})");
        using var disp = new AscomComStaDispatcher("ascom-host-runner");
        await disp.ReadyAsync().ConfigureAwait(false);
        object? driver = null;

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
                (resp, driver) = await DispatchAsync(disp, driver, op, root).ConfigureAwait(false);
                if (op == "dispose") {
                    resp["id"] = id;
                    protocol.WriteLine(JsonSerializer.Serialize(resp));
                    break;
                }
            } catch (Exception ex) {
                resp = new() { ["ok"] = false, ["error"] = ex.Message, ["hr"] = ex.HResult, ["kind"] = "host" };
            }
            resp["id"] = id;
            protocol.WriteLine(JsonSerializer.Serialize(resp));
        }

        var toRelease = driver;
        try {
            await disp.Invoke(() => {
                if (toRelease != null && Marshal.IsComObject(toRelease)) {
                    try { Marshal.FinalReleaseComObject(toRelease); } catch { }
                }
            }).ConfigureAwait(false);
        } catch { }
        return 0;
    }

    private static async Task<(Dictionary<string, object?> resp, object? driver)> DispatchAsync(
        AscomComStaDispatcher disp, object? driver, string? op, JsonElement root) {
        switch (op) {
            case "ping":
                return (Ok(null), driver);

            case "activate": {
                var progId = root.GetProperty("progId").GetString()
                    ?? throw new InvalidOperationException("activate missing progId");
                // Activation runs on the STA; AscomComActivation.Create refuses a
                // 32-bit-only driver in a 64-bit host and logs a breadcrumb.
                var created = await disp.Invoke(() => AscomComActivation.Create(progId)).ConfigureAwait(false);
                return (Ok(null), created);
            }

            case "get": {
                var member = Member(root);
                var raw = await disp.Invoke(() => ComMember.Get<object>(Require(driver), member)).ConfigureAwait(false);
                return (Ok(MarshalOut(raw)), driver);
            }

            case "set": {
                var member = Member(root);
                var vt = root.TryGetProperty("vt", out var vtEl) ? vtEl.GetString() : null;
                var val = root.TryGetProperty("value", out var v) ? MarshalIn(v, vt) : null;
                await disp.Invoke(() => ComMember.Set(Require(driver), member, val!)).ConfigureAwait(false);
                return (Ok(null), driver);
            }

            case "call": {
                var member = Member(root);
                var args = new List<object?>();
                if (root.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array) {
                    foreach (var e in a.EnumerateArray()) args.Add(MarshalIn(e, null));
                }
                var raw = await disp.Invoke(() => ComMember.Call(Require(driver), member, args.ToArray()!)).ConfigureAwait(false);
                return (Ok(MarshalOut(raw)), driver);
            }

            case "setup": {
                await disp.Invoke(() => ComMember.Call(Require(driver), "SetupDialog")).ConfigureAwait(false);
                return (Ok(null), driver);
            }

            case "dispose": {
                var d = driver;
                await disp.Invoke(() => {
                    if (d != null && Marshal.IsComObject(d)) {
                        try { Marshal.FinalReleaseComObject(d); } catch { }
                    }
                }).ConfigureAwait(false);
                return (Ok(null), null);
            }

            default:
                throw new InvalidOperationException($"unknown op '{op}'");
        }
    }

    private static object Require(object? driver)
        => driver ?? throw new InvalidOperationException("driver not activated");

    private static string Member(JsonElement root)
        => root.TryGetProperty("member", out var m) ? (m.GetString() ?? "")
           : throw new InvalidOperationException("missing member");

    private static Dictionary<string, object?> Ok(object? value)
        => new() { ["ok"] = true, ["value"] = value };

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
                var list = new List<object?>(arr.Length);
                foreach (var e in arr) list.Add(e is null ? null : e.ToString());
                return list;
            }
            default: return v.ToString();
        }
    }

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
            _ => null
        };
    }
}
