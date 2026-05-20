using System.Text;
using NUnit.Framework;
using NINA.INDI.Protocol;

namespace NINA.Headless.Test;

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
        _parser.PropertyDeleted += name => deletedName = name;

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
}
