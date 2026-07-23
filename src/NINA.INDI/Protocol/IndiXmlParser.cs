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

using System.Xml;

namespace NINA.INDI.Protocol;

public class IndiXmlParser {
    public event Action<IndiProperty>? PropertyDefined;
    public event Action<IndiProperty>? PropertyUpdated;
    /// <summary>Fired on INDI &lt;delProperty&gt;. Arg 1 is the device
    /// name (always present per spec). Arg 2 is the property name when
    /// only one property of the device is being removed; null when the
    /// whole device is shutting down.</summary>
    public event Action<string, string?>? PropertyDeleted;
    public event Action<string, string>? MessageReceived;

    private readonly XmlReaderSettings _settings = new() {
        ConformanceLevel = ConformanceLevel.Fragment,
        Async = true
    };

    public async Task ParseStreamAsync(Stream stream, CancellationToken ct) {
        using var reader = XmlReader.Create(stream, _settings);

        while (!ct.IsCancellationRequested) {
            try {
                if (!await reader.ReadAsync()) break;
            } catch (XmlException) {
                continue;
            }

            if (reader.NodeType != XmlNodeType.Element) continue;

            try {
                switch (reader.Name) {
                    case "defTextVector":
                        PropertyDefined?.Invoke(ParseTextVector(reader, isDefine: true));
                        break;
                    case "defNumberVector":
                        PropertyDefined?.Invoke(ParseNumberVector(reader, isDefine: true));
                        break;
                    case "defSwitchVector":
                        PropertyDefined?.Invoke(ParseSwitchVector(reader, isDefine: true));
                        break;
                    case "defLightVector":
                        PropertyDefined?.Invoke(ParseLightVector(reader));
                        break;
                    case "defBLOBVector":
                        PropertyDefined?.Invoke(ParseBlobVector(reader, isDefine: true));
                        break;
                    case "setTextVector":
                        PropertyUpdated?.Invoke(ParseTextVector(reader, isDefine: false));
                        break;
                    case "setNumberVector":
                        PropertyUpdated?.Invoke(ParseNumberVector(reader, isDefine: false));
                        break;
                    case "setSwitchVector":
                        PropertyUpdated?.Invoke(ParseSwitchVector(reader, isDefine: false));
                        break;
                    case "setLightVector":
                        PropertyUpdated?.Invoke(ParseLightVector(reader));
                        break;
                    case "setBLOBVector":
                        PropertyUpdated?.Invoke(ParseBlobVector(reader, isDefine: false));
                        break;
                    case "delProperty":
                        // Per INDI spec: delProperty has device (required)
                        // and name (optional). When name is absent, the
                        // ENTIRE device is being removed (driver shutdown,
                        // unloaded from indiserver, etc.) -- not just one
                        // property. Without the device attribute the
                        // client previously had no way to scope the
                        // delete and would silently no-op.
                        PropertyDeleted?.Invoke(
                            reader.GetAttribute("device") ?? "",
                            reader.GetAttribute("name"));
                        break;
                    case "message":
                        MessageReceived?.Invoke(
                            reader.GetAttribute("device") ?? "",
                            reader.GetAttribute("message") ?? "");
                        break;
                }
            } catch (Exception ex) {
                System.Diagnostics.Debug.WriteLine($"INDI parse error: {ex.Message}");
            }
        }
    }

    private static IndiTextProperty ParseTextVector(XmlReader reader, bool isDefine) {
        var prop = new IndiTextProperty();
        ReadVectorAttributes(reader, prop);

        var subtree = reader.ReadSubtree();
        string elementTag = isDefine ? "defText" : "oneText";

        while (subtree.Read()) {
            if (subtree.NodeType == XmlNodeType.Element && subtree.Name == elementTag) {
                string name = subtree.GetAttribute("name") ?? "";
                string value = subtree.ReadElementContentAsString().Trim();
                prop.Values[name] = value;
            }
        }

        return prop;
    }

    private static IndiNumberProperty ParseNumberVector(XmlReader reader, bool isDefine) {
        var prop = new IndiNumberProperty();
        ReadVectorAttributes(reader, prop);

        var subtree = reader.ReadSubtree();
        string elementTag = isDefine ? "defNumber" : "oneNumber";

        while (subtree.Read()) {
            if (subtree.NodeType == XmlNodeType.Element && subtree.Name == elementTag) {
                string name = subtree.GetAttribute("name") ?? "";
                var element = new IndiNumberElement {
                    Label = subtree.GetAttribute("label") ?? name,
                };

                if (isDefine) {
                    element.Format = subtree.GetAttribute("format") ?? "%g";
                    double.TryParse(subtree.GetAttribute("min"), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double min);
                    double.TryParse(subtree.GetAttribute("max"), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double max);
                    double.TryParse(subtree.GetAttribute("step"), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double step);
                    element.Min = min;
                    element.Max = max;
                    element.Step = step;
                }

                string content = subtree.ReadElementContentAsString().Trim();
                if (double.TryParse(content, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double value)) {
                    element.Value = value;
                }

                prop.Values[name] = element;
            }
        }

        return prop;
    }

    private static IndiSwitchProperty ParseSwitchVector(XmlReader reader, bool isDefine) {
        var prop = new IndiSwitchProperty();
        ReadVectorAttributes(reader, prop);

        string? rule = reader.GetAttribute("rule");
        prop.Rule = rule switch {
            "OneOfMany" => IndiSwitchRule.OneOfMany,
            "AtMostOne" => IndiSwitchRule.AtMostOne,
            "AnyOfMany" => IndiSwitchRule.AnyOfMany,
            _ => IndiSwitchRule.OneOfMany
        };

        var subtree = reader.ReadSubtree();
        string elementTag = isDefine ? "defSwitch" : "oneSwitch";

        while (subtree.Read()) {
            if (subtree.NodeType == XmlNodeType.Element && subtree.Name == elementTag) {
                string name = subtree.GetAttribute("name") ?? "";
                // defSwitch carries the label; oneSwitch updates don't. Keep the
                // label from the define so we can map e.g. CCD_ISO -> ISO value.
                string? label = subtree.GetAttribute("label");
                string value = subtree.ReadElementContentAsString().Trim();
                prop.Values[name] = value == "On";
                if (!string.IsNullOrEmpty(label)) prop.Labels[name] = label;
            }
        }

        return prop;
    }

    private static IndiLightProperty ParseLightVector(XmlReader reader) {
        var prop = new IndiLightProperty();
        ReadVectorAttributes(reader, prop);

        var subtree = reader.ReadSubtree();
        while (subtree.Read()) {
            if (subtree.NodeType == XmlNodeType.Element &&
                (subtree.Name == "defLight" || subtree.Name == "oneLight")) {
                string name = subtree.GetAttribute("name") ?? "";
                string value = subtree.ReadElementContentAsString().Trim();
                prop.Values[name] = ParseState(value);
            }
        }

        return prop;
    }

    private static IndiBlobProperty ParseBlobVector(XmlReader reader, bool isDefine) {
        var prop = new IndiBlobProperty();
        ReadVectorAttributes(reader, prop);

        var subtree = reader.ReadSubtree();
        string elementTag = isDefine ? "defBLOB" : "oneBLOB";

        while (subtree.Read()) {
            if (subtree.NodeType == XmlNodeType.Element && subtree.Name == elementTag) {
                string name = subtree.GetAttribute("name") ?? "";
                string format = subtree.GetAttribute("format") ?? "";
                int.TryParse(subtree.GetAttribute("size"), out int size);

                byte[]? data = null;
                if (!isDefine) {
                    data = ReadBlobBase64(subtree, size);
                }

                prop.Values[name] = new IndiBlobElement {
                    Label = subtree.GetAttribute("label") ?? name,
                    Format = format,
                    Size = size,
                    Data = data
                };
            }
        }

        return prop;
    }

    /// <summary>
    /// Decode a oneBLOB payload straight from the reader into bytes.
    ///
    /// <para>This used to be <c>Convert.FromBase64String(ReadElementContentAsString().Trim())</c>,
    /// which materialised the ENTIRE base64 text first. For a 22 MB FITS that is
    /// ~31 M chars = a <b>62 MB</b> <c>char[]</c>, plus a second 62 MB copy from
    /// <c>Trim()</c> (INDI wraps the payload in newlines, so it always copied),
    /// before the 23 MB result — roughly 3x the image size in Large Object Heap
    /// churn per frame. A heap dump on the Orange Pi showed that single char[] as
    /// the biggest object in the process. <c>ReadElementContentAsBase64</c> decodes
    /// incrementally and skips whitespace itself, so no text is ever built.</para>
    ///
    /// <para><paramref name="declaredSize"/> is INDI's <c>size</c> attribute (the
    /// DECODED length), so it is the exact capacity: the happy path allocates the
    /// result once and copies nothing. The probe after a full buffer avoids
    /// "trimming" a multi-MB array just to discover it was already the right size.
    /// A wrong/absent size still works, just with a grow.</para>
    /// </summary>
    private static byte[]? ReadBlobBase64(XmlReader reader, int declaredSize) {
        const int Chunk = 64 * 1024;

        if (declaredSize > 0) {
            var buf = new byte[declaredSize];
            int total = 0, n;
            while (total < buf.Length &&
                   (n = reader.ReadElementContentAsBase64(buf, total, buf.Length - total)) > 0) {
                total += n;
            }
            if (total == 0) return null;
            if (total < buf.Length) { Array.Resize(ref buf, total); return buf; }

            // Buffer filled exactly. Confirm there is nothing left using a small
            // scratch so the normal case returns `buf` with zero extra copies.
            var scratch = new byte[Chunk];
            int extra = reader.ReadElementContentAsBase64(scratch, 0, scratch.Length);
            if (extra == 0) return buf;

            // Server sent more than it declared: fall back to appending.
            using var overflow = new MemoryStream(buf.Length * 2);
            overflow.Write(buf, 0, buf.Length);
            overflow.Write(scratch, 0, extra);
            while ((n = reader.ReadElementContentAsBase64(scratch, 0, scratch.Length)) > 0) {
                overflow.Write(scratch, 0, n);
            }
            return overflow.ToArray();
        }

        // No usable size attribute: stream into a growing buffer.
        using var ms = new MemoryStream(Chunk);
        var chunk = new byte[Chunk];
        int k;
        while ((k = reader.ReadElementContentAsBase64(chunk, 0, chunk.Length)) > 0) {
            ms.Write(chunk, 0, k);
        }
        return ms.Length == 0 ? null : ms.ToArray();
    }

    private static void ReadVectorAttributes(XmlReader reader, IndiProperty prop) {
        prop.Device = reader.GetAttribute("device") ?? "";
        prop.Name = reader.GetAttribute("name") ?? "";
        prop.Label = reader.GetAttribute("label") ?? prop.Name;
        prop.Group = reader.GetAttribute("group") ?? "";
        prop.State = ParseState(reader.GetAttribute("state") ?? "Idle");
        prop.Permission = (reader.GetAttribute("perm") ?? "ro") switch {
            "rw" => IndiPropertyPermission.ReadWrite,
            "wo" => IndiPropertyPermission.WriteOnly,
            _ => IndiPropertyPermission.ReadOnly
        };
        if (double.TryParse(reader.GetAttribute("timeout"), out double timeout))
            prop.Timeout = timeout;
        // INDI servers can attach a free-form message="..." on any
        // set*Vector. Most useful when state=Alert (driver explains why
        // the operation was rejected). Always capture it so the ack-
        // based write helpers can surface it instead of swallowing.
        var message = reader.GetAttribute("message");
        if (!string.IsNullOrEmpty(message)) prop.Message = message;
    }

    private static IndiPropertyState ParseState(string state) => state switch {
        "Ok" => IndiPropertyState.Ok,
        "Busy" => IndiPropertyState.Busy,
        "Alert" => IndiPropertyState.Alert,
        _ => IndiPropertyState.Idle
    };
}