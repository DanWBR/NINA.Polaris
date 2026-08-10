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

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NINA.Polaris.WebSocket;

/// <summary>
/// Writes NaN and +/-Infinity as JSON null instead of throwing.
///
/// WHY THIS EXISTS. The status stream carries a few hundred numbers, several
/// of them ratios computed from live pixel data (SNR, HFR, drift, means).
/// System.Text.Json refuses to write a non-finite double and throws
/// ArgumentException. Inside the status loop that exception killed the whole
/// send, so a SINGLE bad number took down the status WebSocket for every
/// connected client — the app opened its tab, got no status, and reported
/// "connection to server lost". Worse, the bad value lived in retained state,
/// so every subsequent tick threw the same way and the app could never
/// reconnect: only restarting the process cleared it (field, Radxa Q6A,
/// 2026-08-09).
///
/// A telemetry value that cannot be computed is missing data, not a fatal
/// condition, and null says exactly that. The alternative STJ offers,
/// JsonNumberHandling.AllowNamedFloatingPointLiterals, is not usable here: it
/// emits the bare token Infinity, which JSON.parse in the browser rejects, so
/// it would move the failure from the host to the client.
///
/// This is deliberately a floor, not a licence to produce garbage: a non-finite
/// number still means some computation divided by zero and is worth fixing at
/// the source. It just must not be able to disconnect the operator.
/// </summary>
public sealed class NonFiniteDoubleConverter : JsonConverter<double> {
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert,
                                JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null ? 0d : reader.GetDouble();

    public override void Write(Utf8JsonWriter writer, double value,
                               JsonSerializerOptions options) {
        if (double.IsFinite(value)) writer.WriteNumberValue(value);
        else writer.WriteNullValue();
    }
}

/// <summary>Nullable counterpart. System.Text.Json does not route double?
/// through a JsonConverter&lt;double&gt;, so without this the nullable
/// properties in the payload would still throw.</summary>
public sealed class NonFiniteNullableDoubleConverter : JsonConverter<double?> {
    public override double? Read(ref Utf8JsonReader reader, Type typeToConvert,
                                 JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null ? null : reader.GetDouble();

    public override void Write(Utf8JsonWriter writer, double? value,
                               JsonSerializerOptions options) {
        if (value.HasValue && double.IsFinite(value.Value)) writer.WriteNumberValue(value.Value);
        else writer.WriteNullValue();
    }
}

/// <summary>Same guard for float, which the payload also carries.</summary>
public sealed class NonFiniteSingleConverter : JsonConverter<float> {
    public override float Read(ref Utf8JsonReader reader, Type typeToConvert,
                               JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null ? 0f : reader.GetSingle();

    public override void Write(Utf8JsonWriter writer, float value,
                               JsonSerializerOptions options) {
        if (float.IsFinite(value)) writer.WriteNumberValue(value);
        else writer.WriteNullValue();
    }
}

/// <summary>Nullable float counterpart.</summary>
public sealed class NonFiniteNullableSingleConverter : JsonConverter<float?> {
    public override float? Read(ref Utf8JsonReader reader, Type typeToConvert,
                                JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null ? null : reader.GetSingle();

    public override void Write(Utf8JsonWriter writer, float? value,
                               JsonSerializerOptions options) {
        if (value.HasValue && float.IsFinite(value.Value)) writer.WriteNumberValue(value.Value);
        else writer.WriteNullValue();
    }
}
