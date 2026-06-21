using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NINA.Polaris.Json;

/// <summary>
/// System.Text.Json rejects NaN and ±Infinity by default and throws an
/// <see cref="ArgumentException"/> mid-serialization, which turns a single
/// stray non-finite double anywhere in a response object into a 500 for the
/// whole endpoint (e.g. a plate-solve scale or mount position that came back
/// as NaN). These converters map non-finite values to JSON <c>null</c> on
/// write, which is valid JSON and parses cleanly on the JS side, instead of
/// emitting the non-standard "Infinity"/"NaN" tokens that
/// <c>JsonNumberHandling.AllowNamedFloatingPointLiterals</c> would produce
/// (those break <c>JSON.parse</c>). Reads are unchanged.
///
/// Registered globally via <c>ConfigureHttpJsonOptions</c> in Program.cs so
/// every minimal-API response and <c>WriteAsJsonAsync</c> call is protected.
/// </summary>
public sealed class NonFiniteDoubleConverter : JsonConverter<double> {
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null ? double.NaN : reader.GetDouble();

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options) {
        if (double.IsNaN(value) || double.IsInfinity(value)) writer.WriteNullValue();
        else writer.WriteNumberValue(value);
    }
}

public sealed class NullableNonFiniteDoubleConverter : JsonConverter<double?> {
    public override double? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null ? null : reader.GetDouble();

    public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options) {
        if (value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value)) writer.WriteNullValue();
        else writer.WriteNumberValue(value.Value);
    }
}

public sealed class NonFiniteFloatConverter : JsonConverter<float> {
    public override float Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null ? float.NaN : reader.GetSingle();

    public override void Write(Utf8JsonWriter writer, float value, JsonSerializerOptions options) {
        if (float.IsNaN(value) || float.IsInfinity(value)) writer.WriteNullValue();
        else writer.WriteNumberValue(value);
    }
}

public sealed class NullableNonFiniteFloatConverter : JsonConverter<float?> {
    public override float? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null ? null : reader.GetSingle();

    public override void Write(Utf8JsonWriter writer, float? value, JsonSerializerOptions options) {
        if (value is null || float.IsNaN(value.Value) || float.IsInfinity(value.Value)) writer.WriteNullValue();
        else writer.WriteNumberValue(value.Value);
    }
}
