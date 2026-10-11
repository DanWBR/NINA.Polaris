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

using System.Globalization;

namespace NINA.Mount.SynScanWifi;

/// <summary>
/// The Sky-Watcher motor controller protocol: what a SynScan Wi-Fi mount
/// answers on UDP 11880, and what the SynScan App itself speaks to it.
///
/// <para>A command is <c>:</c>, one command letter, the axis (<c>1</c> = RA,
/// <c>2</c> = Dec), optional hex data and a carriage return, e.g.
/// <c>:j1\r</c>. Every command gets one reply: <c>=</c> plus data for
/// success, <c>!</c> plus an error code otherwise. Numbers travel as hex with
/// the least significant byte first, so firmware 0xA52803 reads
/// <c>=0328A5</c>.</para>
///
/// <para>This is not LX200. The driver used to send <c>:GR#</c> / <c>:GD#</c>,
/// which an AZ-GTi never answers: connect "succeeded" (UDP has no handshake),
/// every poll timed out in silence, and the panel showed RA 0h / Dec 0° with
/// nothing responding.</para>
/// </summary>
public static class SkyWatcherMotorCodec {

    /// <summary>Axis positions are 24-bit counts centred on this value: the
    /// motor controller's zero.</summary>
    public const int PositionOffset = 0x800000;

    public static string Command(char command, int axis, string data = "")
        => $":{command}{axis}{data}\r";

    /// <summary>A 24-bit value as six hex digits, low byte first.</summary>
    public static string EncodeUInt24(int value) {
        value &= 0xFFFFFF;
        return string.Create(CultureInfo.InvariantCulture,
            $"{value & 0xFF:X2}{(value >> 8) & 0xFF:X2}{(value >> 16) & 0xFF:X2}");
    }

    /// <summary>An 8-bit value as two hex digits.</summary>
    public static string EncodeByte(int value)
        => (value & 0xFF).ToString("X2", CultureInfo.InvariantCulture);

    /// <summary>Hex data of two, four or six digits, low byte first.</summary>
    public static int DecodeUInt(string hex) {
        if (hex.Length == 0 || hex.Length % 2 != 0 || hex.Length > 6)
            throw new FormatException($"Not a motor-protocol number: '{hex}'");
        int value = 0;
        for (int i = 0; i < hex.Length; i += 2) {
            int b = int.Parse(hex.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            value |= b << (4 * i);
        }
        return value;
    }

    /// <summary>The data of a reply, or an exception carrying the mount's
    /// error code.</summary>
    public static string ParseReply(string reply) {
        var s = reply.TrimEnd('\r', '\n');
        if (s.Length > 0 && s[0] == '=') return s.Substring(1);
        if (s.Length > 0 && s[0] == '!')
            throw new SkyWatcherMotorException(s.Length > 1 ? s.Substring(1) : "?");
        throw new FormatException($"Not a motor-protocol reply: '{s}'");
    }

    /// <summary>The three status digits of <c>:f</c>.</summary>
    public static AxisStatus ParseStatus(string data) {
        if (data.Length < 3) throw new FormatException($"Short status reply: '{data}'");
        int a = Nibble(data[0]), b = Nibble(data[1]), c = Nibble(data[2]);
        return new AxisStatus(
            Running: (b & 1) != 0,
            Blocked: (b & 2) != 0,
            ConstantSpeed: (a & 1) != 0,
            Reverse: (a & 2) != 0,
            HighSpeed: (a & 4) != 0,
            Initialized: (c & 1) != 0);
    }

    private static int Nibble(char ch)
        => int.Parse(ch.ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}

/// <summary>One axis as <c>:f</c> reports it. <see cref="ConstantSpeed"/> is
/// tracking or a jog; false while running means a GoTo.</summary>
public readonly record struct AxisStatus(
    bool Running, bool Blocked, bool ConstantSpeed, bool Reverse, bool HighSpeed, bool Initialized);

/// <summary>A <c>!</c> reply.</summary>
public sealed class SkyWatcherMotorException : Exception {
    public string Code { get; }

    public SkyWatcherMotorException(string code)
        : base($"Mount refused the command: {Describe(code)} (!{code})") {
        Code = code;
    }

    private static string Describe(string code) => code switch {
        "0" => "unknown command",
        "1" => "wrong parameter length",
        "2" => "motor not stopped",
        "3" => "invalid character",
        "4" => "not initialized",
        "5" => "driver asleep",
        "7" => "PEC training running",
        "8" => "no valid PEC data",
        _ => "error",
    };
}
