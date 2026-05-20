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
