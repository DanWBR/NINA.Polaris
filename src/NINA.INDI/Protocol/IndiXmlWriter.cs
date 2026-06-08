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
using System.Text;

namespace NINA.INDI.Protocol;

public static class IndiXmlWriter {
    public static byte[] GetProperties(string? device = null) {
        var sb = new StringBuilder();
        sb.Append("<getProperties version=\"1.7\"");
        if (!string.IsNullOrEmpty(device))
            sb.Append($" device=\"{Escape(device)}\"");
        sb.Append("/>\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public static byte[] EnableBLOB(string device, string mode = "Also") {
        return Encoding.UTF8.GetBytes(
            $"<enableBLOB device=\"{Escape(device)}\">{mode}</enableBLOB>\n");
    }

    public static byte[] NewTextVector(string device, string name, Dictionary<string, string> values) {
        var sb = new StringBuilder();
        sb.Append($"<newTextVector device=\"{Escape(device)}\" name=\"{Escape(name)}\">\n");
        foreach (var (elemName, value) in values) {
            sb.Append($"  <oneText name=\"{Escape(elemName)}\">{Escape(value)}</oneText>\n");
        }
        sb.Append("</newTextVector>\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public static byte[] NewNumberVector(string device, string name, Dictionary<string, double> values) {
        var sb = new StringBuilder();
        sb.Append($"<newNumberVector device=\"{Escape(device)}\" name=\"{Escape(name)}\">\n");
        foreach (var (elemName, value) in values) {
            sb.Append($"  <oneNumber name=\"{Escape(elemName)}\">{value.ToString(CultureInfo.InvariantCulture)}</oneNumber>\n");
        }
        sb.Append("</newNumberVector>\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public static byte[] NewSwitchVector(string device, string name, Dictionary<string, bool> values) {
        var sb = new StringBuilder();
        sb.Append($"<newSwitchVector device=\"{Escape(device)}\" name=\"{Escape(name)}\">\n");
        foreach (var (elemName, value) in values) {
            sb.Append($"  <oneSwitch name=\"{Escape(elemName)}\">{(value ? "On" : "Off")}</oneSwitch>\n");
        }
        sb.Append("</newSwitchVector>\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string Escape(string value) {
        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");
    }
}