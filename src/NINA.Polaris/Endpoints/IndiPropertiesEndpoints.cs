using NINA.INDI.Client;
using NINA.INDI.Protocol;

namespace NINA.Polaris.Endpoints;

/// <summary>
/// Native INDI property browser, the in-process replacement for the
/// standalone <c>indi_control_panel</c> Qt binary. Lets the frontend
/// render the full device → group → property tree and POST edits back
/// without depending on xpra or any external Qt install.
///
/// Why this matters: <c>indi_control_panel</c> is no longer packaged on
/// recent Raspberry Pi OS / Debian releases (libindi 2.x dropped it
/// from <c>indi-bin</c>), and installing it from a PPA isn't always
/// possible (custom Pi distros without <c>software-properties-common</c>).
/// Building on the existing <see cref="IndiClient.Devices"/> dictionary
/// + the three <c>SetXxxAsync</c> helpers means zero new INDI XML code
/// and zero new dependencies.
/// </summary>
public static class IndiPropertiesEndpoints {
    public static void MapIndiPropertiesEndpoints(this IEndpointRouteBuilder app) {
        var g = app.MapGroup("/api/indi/properties");

        // GET → flat list of every property, grouped device → group →
        // property. The DTO mirrors the INDI protocol classes 1:1 so
        // the frontend can dispatch by `type` without re-parsing.
        g.MapGet("/", (IndiClient indi, string? device) => {
            var devices = new List<object>();
            foreach (var devKv in indi.Devices) {
                if (!string.IsNullOrEmpty(device) &&
                    !string.Equals(device, devKv.Key, StringComparison.OrdinalIgnoreCase))
                    continue;
                var props = new List<object>();
                foreach (var propKv in devKv.Value) {
                    var p = propKv.Value;
                    var dto = SerializeProperty(p);
                    if (dto != null) props.Add(dto);
                }
                devices.Add(new {
                    name = devKv.Key,
                    propertyCount = props.Count,
                    properties = props
                });
            }
            return Results.Ok(new { connected = indi.IsConnected, devices });
        });

        // POST → apply a single property change. Body shape is type-
        // discriminated because the three INDI value flavours (number,
        // switch, text) have different value types. Validation:
        // - type must match an existing property of the same name on
        //   the device, otherwise 400 (avoid blindly sending vectors
        //   the device will reject)
        // - permission must be WriteOnly or ReadWrite, otherwise 403
        //   (ReadOnly properties surface in the UI as info-only)
        g.MapPost("/set", async (IndiClient indi, IndiSetRequest req, CancellationToken ct) => {
            if (string.IsNullOrWhiteSpace(req.Device) || string.IsNullOrWhiteSpace(req.Property))
                return Results.BadRequest(new { error = "device and property are required" });

            var existing = indi.GetProperty(req.Device, req.Property);
            if (existing == null)
                return Results.BadRequest(new {
                    error = $"unknown property '{req.Property}' on device '{req.Device}'"
                });
            if (existing.Permission == IndiPropertyPermission.ReadOnly)
                return Results.Json(
                    new { error = "property is read-only" },
                    statusCode: 403);

            try {
                switch (req.Type?.ToLowerInvariant()) {
                    case "number":
                        if (existing is not IndiNumberProperty)
                            return Results.BadRequest(new { error = "type mismatch: expected number" });
                        var nums = req.Numbers ?? new Dictionary<string, double>();
                        await indi.SetNumberAsync(req.Device, req.Property, nums, ct);
                        break;
                    case "switch":
                        if (existing is not IndiSwitchProperty)
                            return Results.BadRequest(new { error = "type mismatch: expected switch" });
                        var sw = req.Switches ?? new Dictionary<string, bool>();
                        await indi.SetSwitchAsync(req.Device, req.Property, sw, ct);
                        break;
                    case "text":
                        if (existing is not IndiTextProperty)
                            return Results.BadRequest(new { error = "type mismatch: expected text" });
                        var txt = req.Texts ?? new Dictionary<string, string>();
                        await indi.SetTextAsync(req.Device, req.Property, txt, ct);
                        break;
                    default:
                        return Results.BadRequest(new {
                            error = "type must be one of: number, switch, text"
                        });
                }
            } catch (Exception ex) {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
            return Results.Ok(new { ok = true });
        });
    }

    /// <summary>Build the JSON DTO for one property. Returns null when
    /// the property type is BLOB (those are routed through
    /// <see cref="Services.ImageRelayService"/> as separate streams,
    /// not edited from this panel).</summary>
    private static object? SerializeProperty(IndiProperty p) {
        var common = new {
            name = p.Name,
            label = string.IsNullOrEmpty(p.Label) ? p.Name : p.Label,
            group = string.IsNullOrEmpty(p.Group) ? "Main" : p.Group,
            state = p.State.ToString().ToLowerInvariant(),
            permission = p.Permission switch {
                IndiPropertyPermission.ReadOnly  => "ro",
                IndiPropertyPermission.WriteOnly => "wo",
                _                                => "rw"
            },
            timestamp = p.Timestamp
        };
        switch (p) {
            case IndiNumberProperty num:
                return new {
                    common.name, common.label, common.group, common.state,
                    common.permission, common.timestamp,
                    type = "number",
                    elements = num.Values.Select(kv => new {
                        name = kv.Key,
                        label = string.IsNullOrEmpty(kv.Value.Label) ? kv.Key : kv.Value.Label,
                        value = kv.Value.Value,
                        min = kv.Value.Min,
                        max = kv.Value.Max,
                        step = kv.Value.Step,
                        format = kv.Value.Format
                    }).ToList()
                };
            case IndiSwitchProperty sw:
                return new {
                    common.name, common.label, common.group, common.state,
                    common.permission, common.timestamp,
                    type = "switch",
                    rule = sw.Rule switch {
                        IndiSwitchRule.OneOfMany  => "oneOfMany",
                        IndiSwitchRule.AtMostOne  => "atMostOne",
                        _                          => "anyOfMany"
                    },
                    elements = sw.Values.Select(kv => new {
                        name = kv.Key,
                        label = kv.Key,
                        value = kv.Value
                    }).ToList()
                };
            case IndiTextProperty txt:
                return new {
                    common.name, common.label, common.group, common.state,
                    common.permission, common.timestamp,
                    type = "text",
                    elements = txt.Values.Select(kv => new {
                        name = kv.Key,
                        label = kv.Key,
                        value = kv.Value
                    }).ToList()
                };
            case IndiLightProperty light:
                return new {
                    common.name, common.label, common.group, common.state,
                    common.permission, common.timestamp,
                    type = "light",
                    elements = light.Values.Select(kv => new {
                        name = kv.Key,
                        label = kv.Key,
                        value = kv.Value.ToString().ToLowerInvariant()
                    }).ToList()
                };
            case IndiBlobProperty:
                // BLOB properties carry binary payloads (FITS, JPEG)
                // that have their own delivery path. The browser
                // shows a placeholder so the user knows the property
                // exists; live data flows via ImageRelayService.
                return new {
                    common.name, common.label, common.group, common.state,
                    common.permission, common.timestamp,
                    type = "blob",
                    elements = Array.Empty<object>()
                };
            default:
                return null;
        }
    }
}

/// <summary>Body shape for POST /api/indi/properties/set. Exactly one
/// of <see cref="Numbers"/>, <see cref="Switches"/>, <see cref="Texts"/>
/// should be populated according to <see cref="Type"/>; the others are
/// ignored.</summary>
public sealed class IndiSetRequest {
    public string? Device { get; set; }
    public string? Property { get; set; }
    public string? Type { get; set; }
    public Dictionary<string, double>? Numbers { get; set; }
    public Dictionary<string, bool>? Switches { get; set; }
    public Dictionary<string, string>? Texts { get; set; }
}
