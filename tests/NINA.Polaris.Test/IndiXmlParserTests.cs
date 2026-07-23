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

using System.Text;
using NUnit.Framework;
using NINA.INDI.Protocol;

namespace NINA.Polaris.Test;

[TestFixture]
public class IndiXmlParserTests {
    private IndiXmlParser _parser = null!;

    [SetUp]
    public void SetUp() {
        _parser = new IndiXmlParser();
    }

    private static MemoryStream XmlStream(string xml) {
        return new MemoryStream(Encoding.UTF8.GetBytes(xml));
    }

    // --- defNumberVector ---

    [Test]
    public async Task ParseDefNumberVector_ExtractsValues() {
        const string xml = """
            <defNumberVector device="CCD Simulator" name="CCD_EXPOSURE" state="Idle" perm="rw" label="Exposure" group="Main Control">
              <defNumber name="CCD_EXPOSURE_VALUE" format="%g" min="0" max="3600" step="1">1</defNumber>
            </defNumberVector>
            """;

        IndiProperty? received = null;
        _parser.PropertyDefined += p => received = p;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var stream = XmlStream(xml);
        await _parser.ParseStreamAsync(stream, cts.Token);

        Assert.That(received, Is.Not.Null);
        Assert.That(received, Is.InstanceOf<IndiNumberProperty>());

        var numProp = (IndiNumberProperty)received!;
        Assert.That(numProp.Device, Is.EqualTo("CCD Simulator"));
        Assert.That(numProp.Name, Is.EqualTo("CCD_EXPOSURE"));
        Assert.That(numProp.State, Is.EqualTo(IndiPropertyState.Idle));
        Assert.That(numProp.Permission, Is.EqualTo(IndiPropertyPermission.ReadWrite));
        Assert.That(numProp.Label, Is.EqualTo("Exposure"));
        Assert.That(numProp.Group, Is.EqualTo("Main Control"));

        Assert.That(numProp.Values, Contains.Key("CCD_EXPOSURE_VALUE"));
        var element = numProp.Values["CCD_EXPOSURE_VALUE"];
        Assert.That(element.Value, Is.EqualTo(1.0));
        Assert.That(element.Min, Is.EqualTo(0.0));
        Assert.That(element.Max, Is.EqualTo(3600.0));
        Assert.That(element.Step, Is.EqualTo(1.0));
        Assert.That(element.Format, Is.EqualTo("%g"));
    }

    // --- defTextVector ---

    [Test]
    public async Task ParseDefTextVector_ExtractsValues() {
        const string xml = """
            <defTextVector device="Telescope Simulator" name="DRIVER_INFO" state="Ok" perm="ro" label="Driver Info" group="General">
              <defText name="DRIVER_NAME">Telescope Simulator</defText>
              <defText name="DRIVER_EXEC">indi_simulator_telescope</defText>
              <defText name="DRIVER_VERSION">1.0</defText>
            </defTextVector>
            """;

        IndiProperty? received = null;
        _parser.PropertyDefined += p => received = p;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var stream = XmlStream(xml);
        await _parser.ParseStreamAsync(stream, cts.Token);

        Assert.That(received, Is.Not.Null);
        Assert.That(received, Is.InstanceOf<IndiTextProperty>());

        var textProp = (IndiTextProperty)received!;
        Assert.That(textProp.Device, Is.EqualTo("Telescope Simulator"));
        Assert.That(textProp.Name, Is.EqualTo("DRIVER_INFO"));
        Assert.That(textProp.State, Is.EqualTo(IndiPropertyState.Ok));
        Assert.That(textProp.Permission, Is.EqualTo(IndiPropertyPermission.ReadOnly));

        Assert.That(textProp.Values, Contains.Key("DRIVER_NAME"));
        Assert.That(textProp.Values["DRIVER_NAME"], Is.EqualTo("Telescope Simulator"));
        Assert.That(textProp.Values, Contains.Key("DRIVER_EXEC"));
        Assert.That(textProp.Values["DRIVER_EXEC"], Is.EqualTo("indi_simulator_telescope"));
        Assert.That(textProp.Values, Contains.Key("DRIVER_VERSION"));
        Assert.That(textProp.Values["DRIVER_VERSION"], Is.EqualTo("1.0"));
    }

    // --- defSwitchVector ---

    [Test]
    public async Task ParseDefSwitchVector_ExtractsValues() {
        const string xml = """
            <defSwitchVector device="CCD Simulator" name="CONNECTION" state="Idle" perm="rw" rule="OneOfMany" label="Connection" group="Main Control">
              <defSwitch name="CONNECT">Off</defSwitch>
              <defSwitch name="DISCONNECT">On</defSwitch>
            </defSwitchVector>
            """;

        IndiProperty? received = null;
        _parser.PropertyDefined += p => received = p;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var stream = XmlStream(xml);
        await _parser.ParseStreamAsync(stream, cts.Token);

        Assert.That(received, Is.Not.Null);
        Assert.That(received, Is.InstanceOf<IndiSwitchProperty>());

        var switchProp = (IndiSwitchProperty)received!;
        Assert.That(switchProp.Device, Is.EqualTo("CCD Simulator"));
        Assert.That(switchProp.Name, Is.EqualTo("CONNECTION"));
        Assert.That(switchProp.Rule, Is.EqualTo(IndiSwitchRule.OneOfMany));

        Assert.That(switchProp.Values, Contains.Key("CONNECT"));
        Assert.That(switchProp.Values["CONNECT"], Is.False);
        Assert.That(switchProp.Values, Contains.Key("DISCONNECT"));
        Assert.That(switchProp.Values["DISCONNECT"], Is.True);
    }

    // --- setNumberVector ---

    [Test]
    public async Task ParseSetNumberVector_UpdatesValues() {
        const string xml = """
            <setNumberVector device="CCD Simulator" name="CCD_EXPOSURE" state="Busy">
              <oneNumber name="CCD_EXPOSURE_VALUE">30</oneNumber>
            </setNumberVector>
            """;

        IndiProperty? received = null;
        _parser.PropertyUpdated += p => received = p;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var stream = XmlStream(xml);
        await _parser.ParseStreamAsync(stream, cts.Token);

        Assert.That(received, Is.Not.Null);
        Assert.That(received, Is.InstanceOf<IndiNumberProperty>());

        var numProp = (IndiNumberProperty)received!;
        Assert.That(numProp.Device, Is.EqualTo("CCD Simulator"));
        Assert.That(numProp.Name, Is.EqualTo("CCD_EXPOSURE"));
        Assert.That(numProp.State, Is.EqualTo(IndiPropertyState.Busy));

        Assert.That(numProp.Values, Contains.Key("CCD_EXPOSURE_VALUE"));
        Assert.That(numProp.Values["CCD_EXPOSURE_VALUE"].Value, Is.EqualTo(30.0));
    }

    // --- defBLOBVector ---

    [Test]
    public async Task ParseDefBlobVector_ExtractsMetadata() {
        const string xml = """
            <defBLOBVector device="CCD Simulator" name="CCD1" state="Idle" perm="ro" label="Image" group="Image">
              <defBLOB name="CCD1" label="CCD Frame" />
            </defBLOBVector>
            """;

        IndiProperty? received = null;
        _parser.PropertyDefined += p => received = p;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var stream = XmlStream(xml);
        await _parser.ParseStreamAsync(stream, cts.Token);

        Assert.That(received, Is.Not.Null);
        Assert.That(received, Is.InstanceOf<IndiBlobProperty>());

        var blobProp = (IndiBlobProperty)received!;
        Assert.That(blobProp.Device, Is.EqualTo("CCD Simulator"));
        Assert.That(blobProp.Name, Is.EqualTo("CCD1"));
        Assert.That(blobProp.State, Is.EqualTo(IndiPropertyState.Idle));
        Assert.That(blobProp.Permission, Is.EqualTo(IndiPropertyPermission.ReadOnly));

        Assert.That(blobProp.Values, Contains.Key("CCD1"));
        Assert.That(blobProp.Values["CCD1"].Label, Is.EqualTo("CCD Frame"));
    }

    // --- message ---

    [Test]
    public async Task ParseMessage_ExtractsDeviceAndText() {
        const string xml = """
            <message device="CCD Simulator" message="CCD is ready" />
            """;

        string? receivedDevice = null;
        string? receivedMessage = null;
        _parser.MessageReceived += (device, message) => {
            receivedDevice = device;
            receivedMessage = message;
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var stream = XmlStream(xml);
        await _parser.ParseStreamAsync(stream, cts.Token);

        Assert.That(receivedDevice, Is.EqualTo("CCD Simulator"));
        Assert.That(receivedMessage, Is.EqualTo("CCD is ready"));
    }

    // --- delProperty ---

    [Test]
    public async Task ParseDelProperty_FiresEvent() {
        const string xml = """
            <delProperty device="CCD Simulator" name="CCD_EXPOSURE" />
            """;

        string? deletedName = null;
        _parser.PropertyDeleted += (_, name) => deletedName = name;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var stream = XmlStream(xml);
        await _parser.ParseStreamAsync(stream, cts.Token);

        Assert.That(deletedName, Is.EqualTo("CCD_EXPOSURE"));
    }

    // --- Multiple properties in one stream ---

    [Test]
    public async Task Parse_MultipleProperties_AllFired() {
        const string xml = """
            <defTextVector device="CCD Simulator" name="DRIVER_INFO" state="Ok" perm="ro">
              <defText name="DRIVER_NAME">CCD Simulator</defText>
            </defTextVector>
            <defNumberVector device="CCD Simulator" name="CCD_EXPOSURE" state="Idle" perm="rw">
              <defNumber name="CCD_EXPOSURE_VALUE" format="%g" min="0" max="3600" step="1">1</defNumber>
            </defNumberVector>
            <defSwitchVector device="CCD Simulator" name="CONNECTION" state="Idle" perm="rw" rule="OneOfMany">
              <defSwitch name="CONNECT">Off</defSwitch>
              <defSwitch name="DISCONNECT">On</defSwitch>
            </defSwitchVector>
            """;

        var received = new List<IndiProperty>();
        _parser.PropertyDefined += p => received.Add(p);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var stream = XmlStream(xml);
        await _parser.ParseStreamAsync(stream, cts.Token);

        Assert.That(received, Has.Count.EqualTo(3));
        Assert.That(received[0], Is.InstanceOf<IndiTextProperty>());
        Assert.That(received[1], Is.InstanceOf<IndiNumberProperty>());
        Assert.That(received[2], Is.InstanceOf<IndiSwitchProperty>());
    }

    // --- State parsing ---

    [TestCase("Ok", IndiPropertyState.Ok)]
    [TestCase("Busy", IndiPropertyState.Busy)]
    [TestCase("Alert", IndiPropertyState.Alert)]
    [TestCase("Idle", IndiPropertyState.Idle)]
    public async Task ParseDefNumberVector_ParsesState(string stateStr, IndiPropertyState expectedState) {
        string xml = $"""
            <defNumberVector device="Dev" name="Prop" state="{stateStr}" perm="ro">
              <defNumber name="VAL" format="%g" min="0" max="100" step="1">0</defNumber>
            </defNumberVector>
            """;

        IndiProperty? received = null;
        _parser.PropertyDefined += p => received = p;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var stream = XmlStream(xml);
        await _parser.ParseStreamAsync(stream, cts.Token);

        Assert.That(received, Is.Not.Null);
        Assert.That(received!.State, Is.EqualTo(expectedState));
    }

    // --- Permission parsing ---

    [TestCase("ro", IndiPropertyPermission.ReadOnly)]
    [TestCase("wo", IndiPropertyPermission.WriteOnly)]
    [TestCase("rw", IndiPropertyPermission.ReadWrite)]
    public async Task ParseDefTextVector_ParsesPermission(string permStr, IndiPropertyPermission expected) {
        string xml = $"""
            <defTextVector device="Dev" name="Prop" state="Idle" perm="{permStr}">
              <defText name="VAL">test</defText>
            </defTextVector>
            """;

        IndiProperty? received = null;
        _parser.PropertyDefined += p => received = p;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var stream = XmlStream(xml);
        await _parser.ParseStreamAsync(stream, cts.Token);

        Assert.That(received, Is.Not.Null);
        Assert.That(received!.Permission, Is.EqualTo(expected));
    }

    // --- setBLOBVector payload decoding (MEMOPT) ---
    //
    // The BLOB path decodes base64 incrementally now instead of materialising
    // the whole payload as a string first (a 22 MB FITS was a 62 MB char[] —
    // the biggest object in a heap dump on the Orange Pi). These pin the decode
    // itself: bytes must come out identical to Convert.FromBase64String,
    // including the whitespace INDI wraps the payload in and a wrong/absent
    // size attribute.

    private async Task<byte[]?> ParseBlobPayload(string base64Body, string sizeAttr) {
        string xml = $"""
            <setBLOBVector device="CCD Simulator" name="CCD1" state="Ok">
              <oneBLOB name="CCD1" {sizeAttr} format=".fits">{base64Body}</oneBLOB>
            </setBLOBVector>
            """;

        IndiProperty? received = null;
        _parser.PropertyUpdated += p => received = p;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var stream = XmlStream(xml);
        await _parser.ParseStreamAsync(stream, cts.Token);

        Assert.That(received, Is.InstanceOf<IndiBlobProperty>());
        return ((IndiBlobProperty)received!).Values["CCD1"].Data;
    }

    [Test]
    public async Task ParseSetBlobVector_DecodesPayload_WithExactSize() {
        var payload = new byte[64 * 1024];
        new Random(1234).NextBytes(payload);

        var data = await ParseBlobPayload(Convert.ToBase64String(payload),
                                          $"size=\"{payload.Length}\"");

        Assert.That(data, Is.Not.Null);
        Assert.That(data!.Length, Is.EqualTo(payload.Length));
        Assert.That(data, Is.EqualTo(payload));
    }

    [Test]
    public async Task ParseSetBlobVector_DecodesPayload_WithSurroundingWhitespace() {
        // INDI wraps the payload in newlines - this is what forced the old
        // .Trim() (and its full second copy of the base64 text).
        var payload = new byte[9_001];   // not a multiple of 3: exercises padding
        new Random(99).NextBytes(payload);
        var b64 = Convert.ToBase64String(payload, Base64FormattingOptions.InsertLineBreaks);

        var data = await ParseBlobPayload($"\n   {b64}\n  ", $"size=\"{payload.Length}\"");

        Assert.That(data, Is.EqualTo(payload));
    }

    [Test]
    public async Task ParseSetBlobVector_DecodesPayload_WhenSizeAttributeMissing() {
        var payload = new byte[5_000];
        new Random(7).NextBytes(payload);

        var data = await ParseBlobPayload(Convert.ToBase64String(payload), "");

        Assert.That(data, Is.EqualTo(payload));
    }

    [Test]
    public async Task ParseSetBlobVector_DecodesPayload_WhenSizeAttributeIsWrong() {
        var payload = new byte[8_192];
        new Random(42).NextBytes(payload);
        var b64 = Convert.ToBase64String(payload);

        // Under-declared: every byte must still come through.
        Assert.That(await ParseBlobPayload(b64, "size=\"100\""), Is.EqualTo(payload),
                    "size menor que o real");

        // Over-declared: must trim to the real length, not pad with zeros.
        Assert.That(await ParseBlobPayload(b64, $"size=\"{payload.Length * 4}\""),
                    Is.EqualTo(payload), "size maior que o real");
    }

    [Test]
    public async Task ParseSetBlobVector_EmptyPayload_YieldsNull() {
        Assert.That(await ParseBlobPayload("", "size=\"0\""), Is.Null);
    }
}