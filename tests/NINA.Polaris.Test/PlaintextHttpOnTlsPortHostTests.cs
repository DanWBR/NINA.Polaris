using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NINA.Polaris.Middleware;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Issue #14 end to end: a real Kestrel HTTPS listener with the plaintext
/// sniffer in front of TLS. A raw http request gets the 301 to https; an
/// actual TLS request still reaches the app (the peek must leave every byte
/// for the handshake).
/// </summary>
[TestFixture]
public class PlaintextHttpOnTlsPortHostTests {
    private static X509Certificate2 TestCert() {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=polaris-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        // SChannel cannot serve with an ephemeral key: persist it in the user key set
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
    }

    [Test]
    public async Task PlainHttpOnTheHttpsPort_GetsARedirect_AndTlsStillWorks() {
        var cert = TestCert();
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; }

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, string.Empty);
        builder.WebHost.ConfigureKestrel(o => o.ListenLocalhost(port, l => {
            PlaintextHttpOnTlsPort.Register(l, port);
            l.UseHttps(cert);
        }));
        var app = builder.Build();
        app.MapGet("/", () => "hello over tls");
        await app.StartAsync();
        try {
            // 1. raw plaintext request on the TLS port
            using (var tcp = new TcpClient()) {
                await tcp.ConnectAsync(IPAddress.Loopback, port);
                using var s = tcp.GetStream();
                var req = Encoding.ASCII.GetBytes($"GET /video HTTP/1.1\r\nHost: 10.42.0.1:{port}\r\n\r\n");
                await s.WriteAsync(req);
                using var reader = new StreamReader(s, Encoding.ASCII);
                var status = await reader.ReadLineAsync();
                var headers = new System.Collections.Generic.List<string>();
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync())) headers.Add(line!);
                Assert.That(status, Is.EqualTo("HTTP/1.1 301 Moved Permanently"));
                Assert.That(headers, Has.Some.EqualTo($"Location: https://10.42.0.1:{port}/video"));
            }
            // 2. a real https request still goes through the handshake to the app
            using var handler = new HttpClientHandler {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };
            using var http = new HttpClient(handler);
            var body = await http.GetStringAsync($"https://localhost:{port}/");
            Assert.That(body, Is.EqualTo("hello over tls"));
        } finally {
            await app.StopAsync();
        }
    }
}
