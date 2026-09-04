using System.Buffers;
using System.Text;
using NINA.Polaris.Middleware;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Issue #14: http://host:5000/ on the HTTPS-only port got an empty reply.
/// The classifier behind the connection middleware must tell a TLS
/// ClientHello from a plaintext request, wait for a partial request line,
/// and build the https redirect from the Host header (port replaced).
/// </summary>
[TestFixture]
public class PlaintextHttpOnTlsPortTests {
    private static ReadOnlySequence<byte> Bytes(string s) => new(Encoding.ASCII.GetBytes(s));

    [Test]
    public void TlsClientHello_IsLeftForTls() {
        var hello = new ReadOnlySequence<byte>(new byte[] { 0x16, 0x03, 0x01, 0x02, 0x00, 0x01, 0x00 });
        Assert.That(PlaintextHttpOnTlsPort.Inspect(hello, 5000, "10.42.0.1", out _), Is.EqualTo(PlaintextHttpOnTlsPort.Verdict.Tls));
    }

    [Test]
    public void PlainGet_RedirectsToHttpsOnTheSameHost_WithThePortReplaced() {
        var v = PlaintextHttpOnTlsPort.Inspect(Bytes("GET /video?x=1 HTTP/1.1\r\nHost: 10.42.0.1:5000\r\nUser-Agent: curl\r\n\r\n"), 5000, "1.2.3.4", out var loc);
        Assert.That(v, Is.EqualTo(PlaintextHttpOnTlsPort.Verdict.Plaintext));
        Assert.That(loc, Is.EqualTo("https://10.42.0.1:5000/video?x=1"));
    }

    [Test]
    public void PlainGet_WithHostname_KeepsTheName() {
        var v = PlaintextHttpOnTlsPort.Inspect(Bytes("GET / HTTP/1.1\r\nHost: polaris-rpi.local\r\n\r\n"), 5000, "1.2.3.4", out var loc);
        Assert.That(v, Is.EqualTo(PlaintextHttpOnTlsPort.Verdict.Plaintext));
        Assert.That(loc, Is.EqualTo("https://polaris-rpi.local:5000/"));
    }

    [Test]
    public void PlainGet_Ipv6Host_KeepsTheBrackets() {
        var v = PlaintextHttpOnTlsPort.Inspect(Bytes("GET / HTTP/1.1\r\nHost: [fe80::1]:5000\r\n\r\n"), 5000, "x", out var loc);
        Assert.That(v, Is.EqualTo(PlaintextHttpOnTlsPort.Verdict.Plaintext));
        Assert.That(loc, Is.EqualTo("https://[fe80::1]:5000/"));
    }

    [Test]
    public void PartialRequestLine_WaitsForMore() {
        Assert.That(PlaintextHttpOnTlsPort.Inspect(Bytes("GE"), 5000, "x", out _), Is.EqualTo(PlaintextHttpOnTlsPort.Verdict.NeedMore));
        Assert.That(PlaintextHttpOnTlsPort.Inspect(Bytes("GET / HTTP/1.1\r\nHos"), 5000, "x", out _), Is.EqualTo(PlaintextHttpOnTlsPort.Verdict.NeedMore));
    }

    [Test]
    public void NoHostHeader_FallsBackToTheConnectedAddress() {
        var v = PlaintextHttpOnTlsPort.Inspect(Bytes("GET /a HTTP/1.0\r\n\r\n"), 5000, "10.42.0.1", out var loc);
        Assert.That(v, Is.EqualTo(PlaintextHttpOnTlsPort.Verdict.Plaintext));
        Assert.That(loc, Is.EqualTo("https://10.42.0.1:5000/a"));
    }

    [Test]
    public void OtherAsciiTraffic_IsNotMistakenForHttp() {
        Assert.That(PlaintextHttpOnTlsPort.Inspect(Bytes("SSH-2.0-OpenSSH\r\n"), 5000, "x", out _), Is.EqualTo(PlaintextHttpOnTlsPort.Verdict.Tls));
    }
}
