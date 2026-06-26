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

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NINA.Polaris.Services.Sequencer.Conditions;
using NINA.Polaris.Services.Sequencer.Containers;
using NINA.Polaris.Services.Sequencer.Instructions;
using NINA.Polaris.Services.Sequencer.Triggers;

namespace NINA.Polaris.Services.Sequencer;

/// <summary>
/// Polymorphic JSON IO for the Advanced Sequencer. Each entity carries a
/// stable string discriminator via <see cref="ISequenceEntity.Type"/>; we
/// serialize it under the "$type" property and use a switch to dispatch
/// during deserialization.
///
/// We don't lean on System.Text.Json's built-in
/// <c>JsonPolymorphic</c>/<c>JsonDerivedType</c> attributes because the
/// type tree is in the same assembly and a single switch is easier to
/// extend (and to evolve via the Version field on the wrapper).
/// </summary>
public static class SequenceJson {
    public const int CurrentVersion = 1;

    // Web defaults => camelCase property names + case-insensitive reads, which
    // is what the Alpine frontend (and ASP.NET's own model binding) speak. The
    // converters below add the $type polymorphism on top.
    private static readonly JsonSerializerOptions _opts = new(JsonSerializerDefaults.Web) {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = {
            new SequenceEntityJsonConverter(),
            new PolymorphicSubclassConverter<SequenceTrigger>(),
            new PolymorphicSubclassConverter<SequenceCondition>(),
            new JsonStringEnumConverter()
        }
    };

    public static string Serialize(SequenceDocument doc) =>
        JsonSerializer.Serialize(doc, _opts);

    public static SequenceDocument Deserialize(string json) =>
        JsonSerializer.Deserialize<SequenceDocument>(json, _opts)
            ?? throw new InvalidDataException("Empty sequence document");

    internal static JsonSerializerOptions Options => _opts;
}

/// <summary>
/// Dispatches to <see cref="SequenceEntityJsonConverter"/> for any nested
/// abstract entity collection (Triggers, Conditions, Items). System.Text.Json
/// can't read an abstract subclass without a converter; this one forwards.
/// </summary>
public class PolymorphicSubclassConverter<T> : JsonConverter<T> where T : ISequenceEntity {
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        // Read through the entity converter, it handles $type dispatch.
        var entity = JsonSerializer.Deserialize<ISequenceEntity>(ref reader, options)
            ?? throw new JsonException("Null entity");
        if (entity is not T typed)
            throw new JsonException($"Expected {typeof(T).Name}, got {entity.GetType().Name}");
        return typed;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) {
        JsonSerializer.Serialize(writer, (ISequenceEntity)value, options);
    }
}

/// <summary>
/// The top-level shape persisted to disk. Wrapping the root entity in a
/// document lets us add metadata (created/updated timestamps, version,
/// editor notes) without churning the entity schema.
/// </summary>
public class SequenceDocument {
    public int Version { get; set; } = SequenceJson.CurrentVersion;
    public string Name { get; set; } = "Untitled Sequence";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? Description { get; set; }
    /// <summary>The root container (typically a <c>SequentialContainer</c>).</summary>
    public ISequenceEntity Root { get; set; } = new SequentialContainer { Name = "Root" };
}

/// <summary>
/// Hand-rolled polymorphic converter. Reads <c>$type</c> to dispatch to
/// the right concrete subclass; writes <c>$type</c> alongside the entity's
/// own properties.
/// </summary>
public class SequenceEntityJsonConverter : JsonConverter<ISequenceEntity> {
    public override ISequenceEntity Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        if (!root.TryGetProperty("$type", out var typeProp))
            throw new JsonException("Sequence entity missing $type discriminator");
        var type = typeProp.GetString() ?? throw new JsonException("Null $type");

        var clr = Resolve(type) ?? throw new JsonException("Unknown sequence entity $type: " + type);
        var entity = (ISequenceEntity?)JsonSerializer.Deserialize(root.GetRawText(), clr, _innerOptions)
            ?? throw new JsonException($"Failed to deserialize {type}");
        return entity;
    }

    public override void Write(Utf8JsonWriter writer, ISequenceEntity value, JsonSerializerOptions options) {
        // Round-trip: serialize the concrete type to a JsonNode, inject $type, write.
        var node = (JsonNode?)JsonSerializer.SerializeToNode(value, value.GetType(), _innerOptions)
            ?? throw new JsonException("Could not serialise " + value.GetType());
        node["$type"] = value.Type;
        node.WriteTo(writer);
    }

    // Inner options for serializing concrete entity types. Includes the
    // entity converter so nested Items collections of ISequenceEntity round-trip.
    // No infinite recursion: the entity converter only kicks in for properties
    // typed as ISequenceEntity (or registered subclasses); for concrete types,
    // STJ uses the default converter, which is what we want here.
    private static readonly JsonSerializerOptions _innerOptions = new(JsonSerializerDefaults.Web) {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = {
            new SequenceEntityJsonConverter(),
            new PolymorphicSubclassConverter<SequenceTrigger>(),
            new PolymorphicSubclassConverter<SequenceCondition>(),
            new JsonStringEnumConverter()
        }
    };

    /// <summary>
    /// Plugin-contributed entities, populated at startup by the plugin
    /// loader before the HTTP listener accepts requests. Read-only after
    /// startup so we don't need to lock on every Resolve call.
    /// </summary>
    private static readonly Dictionary<string, Type> _pluginTypes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<(string Type, string Category, string Class)> _pluginKnown = new();

    /// <summary>
    /// Called by the plugin registry to add a new entity discriminator.
    /// Returns the discriminator that was used so the caller can log it.
    /// </summary>
    public static string RegisterPluginEntity(Type entityType, string category) {
        if (!typeof(ISequenceEntity).IsAssignableFrom(entityType))
            throw new ArgumentException($"{entityType} does not implement ISequenceEntity", nameof(entityType));
        // Probe the discriminator by instantiating once (parameterless ctor required).
        var sample = (ISequenceEntity?)Activator.CreateInstance(entityType)
            ?? throw new InvalidOperationException($"Could not instantiate {entityType}");
        var type = sample.Type;
        if (string.IsNullOrWhiteSpace(type))
            throw new InvalidOperationException($"{entityType}.Type is empty, plugins must return a stable discriminator");
        if (Resolve(type) != null && Resolve(type) != entityType)
            throw new InvalidOperationException($"Sequencer entity discriminator '{type}' already registered by another type");
        _pluginTypes[type] = entityType;
        var kindName = sample switch {
            SequenceContainer => "Container",
            SequenceTrigger   => "Trigger",
            SequenceCondition => "Condition",
            _                 => "Instruction"
        };
        _pluginKnown.Add((type, category, kindName));
        return type;
    }

    /// <summary>
    /// String discriminator → CLR type. Add new entities here; the rest of
    /// the engine + UI auto-pick them up via the existing Type property.
    /// </summary>
    public static Type? Resolve(string type) {
        if (_pluginTypes.TryGetValue(type, out var plug)) return plug;
        return ResolveBuiltIn(type);
    }

    private static Type? ResolveBuiltIn(string type) => type switch {
        // Containers
        "Sequential"     => typeof(SequentialContainer),
        "Parallel"       => typeof(ParallelContainer),
        "Conditional"    => typeof(ConditionalContainer),
        "DeepSkyObject"  => typeof(DeepSkyObjectContainer),
        "Templated"      => typeof(TemplatedContainer),

        // Mount
        "SlewToCoordinates"   => typeof(SlewToCoordinatesInstruction),
        "CenterOnCoordinates" => typeof(CenterOnCoordinatesInstruction),
        "ParkMount"           => typeof(ParkMountInstruction),
        "UnparkMount"         => typeof(UnparkMountInstruction),
        "SetTracking"         => typeof(SetTrackingInstruction),
        "SolveAndSync"        => typeof(SolveAndSyncInstruction),

        // Camera
        "TakeExposure" => typeof(TakeExposureInstruction),
        "CoolCamera"   => typeof(CoolCameraInstruction),
        "WarmCamera"   => typeof(WarmCameraInstruction),

        // Focuser
        "MoveFocuser"        => typeof(MoveFocuserInstruction),
        "AutoFocus"          => typeof(AutoFocusInstruction),
        "MoveToFilterOffset" => typeof(MoveToFilterOffsetInstruction),

        // Filter wheel
        "SwitchFilter" => typeof(SwitchFilterInstruction),

        // Guider
        "StartGuiding"   => typeof(StartGuidingInstruction),
        "StopGuiding"    => typeof(StopGuidingInstruction),
        "Dither"         => typeof(DitherInstruction),
        "AutoSelectStar" => typeof(AutoSelectStarInstruction),

        // Dome
        "OpenShutter"        => typeof(OpenShutterInstruction),
        "CloseShutter"       => typeof(CloseShutterInstruction),
        "ParkDome"           => typeof(ParkDomeInstruction),
        "SlewDomeToAzimuth"  => typeof(SlewDomeToAzimuthInstruction),
        "SyncDomeToScope"    => typeof(SyncDomeToScopeInstruction),

        // Flat panel
        "OpenFlatCover"     => typeof(OpenFlatCoverInstruction),
        "CloseFlatCover"    => typeof(CloseFlatCoverInstruction),
        "SetFlatBrightness" => typeof(SetFlatBrightnessInstruction),
        "ToggleFlatLight"   => typeof(ToggleFlatLightInstruction),

        // Rotator
        "RotateToAngle" => typeof(RotateToAngleInstruction),

        // Flow control
        "WaitForTime"              => typeof(WaitForTimeInstruction),
        "WaitUntilTime"            => typeof(WaitUntilTimeInstruction),
        "WaitUntilAltitude"        => typeof(WaitUntilAltitudeInstruction),
        "WaitForSunBelowHorizon"   => typeof(WaitForSunBelowHorizonInstruction),
        "WaitForMoon"              => typeof(WaitForMoonInstruction),

        // External
        "RunExternalScript" => typeof(RunExternalScriptInstruction),
        "SendHttpRequest"   => typeof(SendHttpRequestInstruction),

        // Conditions
        "LoopUntilTime"      => typeof(LoopUntilTimeCondition),
        "LoopUntilAltitude"  => typeof(LoopUntilAltitudeCondition),
        "LoopForNExposures"  => typeof(LoopForNExposuresCondition),
        "LoopForDuration"    => typeof(LoopForDurationCondition),
        "LoopUntilMoonSets"  => typeof(LoopUntilMoonSetsCondition),
        "LoopWhileSafe"      => typeof(LoopWhileSafeCondition),

        // Triggers
        "AutoFocusOnTempChange"   => typeof(AutoFocusOnTempChangeTrigger),
        "AutoFocusOnHfrIncrease"  => typeof(AutoFocusOnHfrIncreaseTrigger),
        "AutoFocusEveryNMinutes"  => typeof(AutoFocusEveryNMinutesTrigger),
        "AutoFocusEveryNFrames"   => typeof(AutoFocusEveryNFramesTrigger),
        "AutoFocusOnFilterChange" => typeof(AutoFocusOnFilterChangeTrigger),
        "MeridianFlip"            => typeof(MeridianFlipTrigger),
        "DitherAfterNExposures"   => typeof(DitherAfterNExposuresTrigger),
        "CenterAfterDrift"        => typeof(CenterAfterDriftTrigger),
        "Safety"                  => typeof(SafetyTrigger),
        "RestoreGuiding"          => typeof(RestoreGuidingTrigger),
        "ReconnectCamera"         => typeof(ReconnectCameraTrigger),

        _ => null
    };

    /// <summary>Discoverable list for the UI palette / API. Merges built-in + plugin entries.</summary>
    public static IReadOnlyList<(string Type, string Category, string Class)> KnownTypes =>
        _known.Concat(_pluginKnown).ToList();

    // Keys that are structural / runtime, not user-editable parameters. The
    // palette's "defaults" object excludes these so the tree editor only seeds
    // the actual scalar parameters of a freshly-dropped entity.
    private static readonly HashSet<string> _nonParamKeys = new(StringComparer.OrdinalIgnoreCase) {
        "id", "$type", "type", "name", "description",
        "items", "triggers", "conditions",
        "status", "error", "startedAt", "finishedAt"
    };

    /// <summary>
    /// Default scalar parameter values for a freshly-constructed entity of the
    /// given discriminator, so the tree editor can show editable fields the
    /// moment an entity is dropped (instead of only after a save+reload). Keys
    /// are camelCase to match the wire format; nested objects/arrays and the
    /// structural/runtime keys are excluded. Returns null for unknown types.
    /// </summary>
    public static JsonObject? DefaultParams(string type) {
        var clr = Resolve(type);
        if (clr == null) return null;
        object? instance;
        try { instance = Activator.CreateInstance(clr); }
        catch { return null; }
        if (instance == null) return null;
        if (JsonSerializer.SerializeToNode(instance, clr, _innerOptions) is not JsonObject node)
            return null;
        var result = new JsonObject();
        foreach (var kv in node) {
            if (_nonParamKeys.Contains(kv.Key)) continue;
            if (kv.Value is JsonObject or JsonArray) continue; // editor handles scalars only
            result[kv.Key] = kv.Value?.DeepClone();
        }
        return result;
    }

    internal static JsonSerializerOptions InnerOptions => _innerOptions;

    private static readonly (string Type, string Category, string Class)[] _known = new[] {
        ("Sequential", "Containers", "Container"),
        ("Parallel", "Containers", "Container"),
        ("Conditional", "Containers", "Container"),
        ("DeepSkyObject", "Containers", "Container"),
        ("Templated", "Containers", "Container"),

        ("SlewToCoordinates", "Mount", "Instruction"),
        ("CenterOnCoordinates", "Mount", "Instruction"),
        ("ParkMount", "Mount", "Instruction"),
        ("UnparkMount", "Mount", "Instruction"),
        ("SetTracking", "Mount", "Instruction"),
        ("SolveAndSync", "Mount", "Instruction"),

        ("TakeExposure", "Camera", "Instruction"),
        ("CoolCamera", "Camera", "Instruction"),
        ("WarmCamera", "Camera", "Instruction"),

        ("MoveFocuser", "Focuser", "Instruction"),
        ("AutoFocus", "Focuser", "Instruction"),
        ("MoveToFilterOffset", "Focuser", "Instruction"),

        ("SwitchFilter", "Filter Wheel", "Instruction"),

        ("StartGuiding", "Guider", "Instruction"),
        ("StopGuiding", "Guider", "Instruction"),
        ("Dither", "Guider", "Instruction"),
        ("AutoSelectStar", "Guider", "Instruction"),

        ("OpenShutter", "Dome", "Instruction"),
        ("CloseShutter", "Dome", "Instruction"),
        ("ParkDome", "Dome", "Instruction"),
        ("SlewDomeToAzimuth", "Dome", "Instruction"),
        ("SyncDomeToScope", "Dome", "Instruction"),

        ("OpenFlatCover", "Flat Panel", "Instruction"),
        ("CloseFlatCover", "Flat Panel", "Instruction"),
        ("SetFlatBrightness", "Flat Panel", "Instruction"),
        ("ToggleFlatLight", "Flat Panel", "Instruction"),

        ("RotateToAngle", "Rotator", "Instruction"),

        ("WaitForTime", "Flow Control", "Instruction"),
        ("WaitUntilTime", "Flow Control", "Instruction"),
        ("WaitUntilAltitude", "Flow Control", "Instruction"),
        ("WaitForSunBelowHorizon", "Flow Control", "Instruction"),
        ("WaitForMoon", "Flow Control", "Instruction"),

        ("RunExternalScript", "External", "Instruction"),
        ("SendHttpRequest", "External", "Instruction"),

        ("LoopUntilTime", "Conditions", "Condition"),
        ("LoopUntilAltitude", "Conditions", "Condition"),
        ("LoopForNExposures", "Conditions", "Condition"),
        ("LoopForDuration", "Conditions", "Condition"),
        ("LoopUntilMoonSets", "Conditions", "Condition"),
        ("LoopWhileSafe", "Conditions", "Condition"),

        ("AutoFocusOnTempChange", "Triggers", "Trigger"),
        ("AutoFocusOnHfrIncrease", "Triggers", "Trigger"),
        ("AutoFocusEveryNMinutes", "Triggers", "Trigger"),
        ("AutoFocusEveryNFrames", "Triggers", "Trigger"),
        ("AutoFocusOnFilterChange", "Triggers", "Trigger"),
        ("MeridianFlip", "Triggers", "Trigger"),
        ("DitherAfterNExposures", "Triggers", "Trigger"),
        ("CenterAfterDrift", "Triggers", "Trigger"),
        ("Safety", "Triggers", "Trigger"),
        ("RestoreGuiding", "Triggers", "Trigger"),
        ("ReconnectCamera", "Triggers", "Trigger"),
    };
}