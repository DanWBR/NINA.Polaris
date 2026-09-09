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
using NINA.Polaris.Endpoints;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// PUT /api/system/profile is a PATCH: a field the client leaves out keeps the
/// value the host already has.
///
/// It used to bind a whole UserProfile and copy every field across, so anything
/// the body omitted arrived as its type's default and was written as such. On
/// 2026-09-08 that destroyed a working rig's settings: the browser had not read
/// the profile yet, a save fired, and the host's location went to 0,0 with
/// auto-connect off. Permanently, because it was the stored profile that had
/// been overwritten.
///
/// Four fields had grown their own "only if non-empty" guard before that, one
/// bug at a time. These tests pin the general rule.
/// </summary>
[TestFixture]
public class ProfilePatchTests {

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    private static UserProfile Configured() => new() {
        Latitude = -23.5, Longitude = -46.6, Altitude = 760,
        FocalLengthMm = 478, IndiPort = 7624, DefaultGain = 120,
        AutoConnectOnStartup = true, LocationPromptDismissed = true,
        ImageOutputDir = "/mnt/nvme/files", UiLanguage = "pt-BR",
        UpdateChannel = "preview", AstapPath = "/opt/astap/astap_cli",
    };

    /// <summary>THE regression: a body that carries one field must not reset
    /// the rest, however complete the receiving type is.</summary>
    [Test]
    public void OmittedFieldsAreLeftAlone() {
        var p = Configured();
        SystemEndpoints.ApplyProfilePatch(Json("""{"defaultGain": 200}"""), p);

        Assert.That(p.DefaultGain, Is.EqualTo(200), "the one field sent does change");
        Assert.That(p.Latitude, Is.EqualTo(-23.5), "latitude survived");
        Assert.That(p.Longitude, Is.EqualTo(-46.6), "longitude survived");
        Assert.That(p.AutoConnectOnStartup, Is.True, "auto-connect survived");
        Assert.That(p.FocalLengthMm, Is.EqualTo(478));
        Assert.That(p.AstapPath, Is.EqualTo("/opt/astap/astap_cli"));
    }

    /// <summary>The exact shape of the field report: an empty body, which is
    /// what an unloaded client would have sent.</summary>
    [Test]
    public void AnEmptyBodyChangesNothing() {
        var p = Configured();
        SystemEndpoints.ApplyProfilePatch(Json("{}"), p);

        Assert.That(p.Latitude, Is.EqualTo(-23.5));
        Assert.That(p.Longitude, Is.EqualTo(-46.6));
        Assert.That(p.Altitude, Is.EqualTo(760));
        Assert.That(p.AutoConnectOnStartup, Is.True);
        Assert.That(p.LocationPromptDismissed, Is.True);
        Assert.That(p.ImageOutputDir, Is.EqualTo("/mnt/nvme/files"));
        Assert.That(p.UiLanguage, Is.EqualTo("pt-BR"));
    }

    /// <summary>Explicit zero and false are real values, not absence. A user who
    /// genuinely sits on the equator, or turns auto-connect off, must be
    /// obeyed.</summary>
    [Test]
    public void ZeroAndFalseAreValuesLikeAnyOther() {
        var p = Configured();
        SystemEndpoints.ApplyProfilePatch(
            Json("""{"latitude": 0, "longitude": 0, "autoConnectOnStartup": false}"""), p);

        Assert.That(p.Latitude, Is.Zero);
        Assert.That(p.Longitude, Is.Zero);
        Assert.That(p.AutoConnectOnStartup, Is.False);
        Assert.That(p.Altitude, Is.EqualTo(760), "and nothing else moved");
    }

    /// <summary>null is "no value supplied", the same as leaving the key out.
    /// A client that serialises its unset fields must not wipe the host.</summary>
    [Test]
    public void ExplicitNullIsTreatedAsAbsent() {
        var p = Configured();
        SystemEndpoints.ApplyProfilePatch(
            Json("""{"astapPath": null, "defaultGain": null}"""), p);

        Assert.That(p.AstapPath, Is.EqualTo("/opt/astap/astap_cli"));
        Assert.That(p.DefaultGain, Is.EqualTo(120));
    }

    /// <summary>The Studio root is host hardware state with its own endpoint. A
    /// settings save carrying a stale empty value must not reset a configured
    /// NVMe path back to the default.</summary>
    [Test]
    public void ABlankStudioRootOrLanguageIsIgnored() {
        var p = Configured();
        SystemEndpoints.ApplyProfilePatch(
            Json("""{"imageOutputDir": "", "uiLanguage": "   "}"""), p);

        Assert.That(p.ImageOutputDir, Is.EqualTo("/mnt/nvme/files"));
        Assert.That(p.UiLanguage, Is.EqualTo("pt-BR"));

        SystemEndpoints.ApplyProfilePatch(
            Json("""{"imageOutputDir": "/data/lights", "uiLanguage": "es"}"""), p);
        Assert.That(p.ImageOutputDir, Is.EqualTo("/data/lights"), "a real value still applies");
        Assert.That(p.UiLanguage, Is.EqualTo("es"));
    }

    /// <summary>An ordinary string field sent as "" IS a value: it clears an
    /// override back to auto-detect, which is how the tool-path fields work.</summary>
    [Test]
    public void AnEmptyStringClearsAnOverride() {
        var p = Configured();
        SystemEndpoints.ApplyProfilePatch(Json("""{"astapPath": ""}"""), p);
        Assert.That(p.AstapPath, Is.Empty);
    }

    /// <summary>The update channel is normalised, and only when it was sent:
    /// a host on preview must not fall back to stable because a save omitted
    /// the field.</summary>
    [TestCase("""{"updateChannel": "preview"}""", "preview")]
    [TestCase("""{"updateChannel": "stable"}""", "stable")]
    [TestCase("""{"updateChannel": "PREVIEW"}""", "preview")]
    [TestCase("""{"updateChannel": "nonsense"}""", "stable")]
    [TestCase("""{"defaultGain": 1}""", "preview")]
    public void TheUpdateChannelIsNormalisedOnlyWhenSent(string body, string expected) {
        var p = Configured();
        SystemEndpoints.ApplyProfilePatch(Json(body), p);
        Assert.That(p.UpdateChannel, Is.EqualTo(expected));
    }

    /// <summary>JSON casing is a client convention, and the wrong casing
    /// silently doing nothing is the same class of failure being closed.</summary>
    [Test]
    public void FieldNamesAreMatchedCaseInsensitively() {
        var p = Configured();
        SystemEndpoints.ApplyProfilePatch(Json("""{"Latitude": 10, "FOCALLENGTHMM": 900}"""), p);
        Assert.That(p.Latitude, Is.EqualTo(10));
        Assert.That(p.FocalLengthMm, Is.EqualTo(900));
    }

    /// <summary>A value of the wrong type is ignored rather than throwing or
    /// coercing: the stored value is better than a guess.</summary>
    [Test]
    public void AWrongTypedValueIsIgnored() {
        var p = Configured();
        SystemEndpoints.ApplyProfilePatch(
            Json("""{"latitude": "not a number", "autoConnectOnStartup": "yes"}"""), p);
        Assert.That(p.Latitude, Is.EqualTo(-23.5));
        Assert.That(p.AutoConnectOnStartup, Is.True);
    }

    [Test]
    public void ANonObjectBodyIsIgnored() {
        var p = Configured();
        Assert.DoesNotThrow(() => SystemEndpoints.ApplyProfilePatch(Json("[1,2,3]"), p));
        Assert.That(p.Latitude, Is.EqualTo(-23.5));
    }
}
