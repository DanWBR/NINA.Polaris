using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NINA.Headless.Services.Alpaca;

/// <summary>
/// UDP-broadcast discovery for Alpaca servers on the local subnet.
/// Per the ASCOM Alpaca discovery protocol:
///   client sends "alpacadiscovery1" to UDP port 32227 (broadcast)
///   each server replies with JSON: {"AlpacaPort": 11111}
///
/// We also fetch each respondent's <c>/management/v1/configureddevices</c>
/// so the caller gets a flat list of (host, port, device-type, device-number,
/// device-name, unique-id) tuples ready to display.
/// </summary>
public class AlpacaDiscovery {
    private readonly ILogger<AlpacaDiscovery> _logger;
    private const int DiscoveryPort = 32227;
    private const string DiscoveryMessage = "alpacadiscovery1";

    public AlpacaDiscovery(ILogger<AlpacaDiscovery> logger) {
        _logger = logger;
    }

    public async Task<List<AlpacaServer>> DiscoverServersAsync(TimeSpan? timeout = null) {
        var to = timeout ?? TimeSpan.FromSeconds(3);
        var found = new Dictionary<string, AlpacaServer>(); // key: "host:port"

        using var udp = new UdpClient();
        udp.EnableBroadcast = true;
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        var bytes = Encoding.ASCII.GetBytes(DiscoveryMessage);
        try {
            await udp.SendAsync(bytes, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Alpaca discovery broadcast failed (firewall?)");
            return found.Values.ToList();
        }

        using var cts = new CancellationTokenSource(to);
        try {
            while (!cts.IsCancellationRequested) {
                var result = await udp.ReceiveAsync(cts.Token);
                try {
                    var json = Encoding.ASCII.GetString(result.Buffer);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("AlpacaPort", out var portEl)) {
                        var host = result.RemoteEndPoint.Address.ToString();
                        var port = portEl.GetInt32();
                        var key = $"{host}:{port}";
                        if (!found.ContainsKey(key)) {
                            found[key] = new AlpacaServer { Host = host, Port = port };
                        }
                    }
                } catch (Exception ex) {
                    _logger.LogDebug(ex, "Ignoring malformed Alpaca discovery reply");
                }
            }
        } catch (OperationCanceledException) { /* expected */ }

        // Enrich with /management/v1/configureddevices for each found server
        foreach (var server in found.Values) {
            try {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                var url = $"http://{server.Host}:{server.Port}/management/v1/configureddevices";
                var resp = await http.GetFromJsonAsync<AlpacaResponse<List<AlpacaConfiguredDevice>>>(url);
                if (resp?.Value != null) {
                    server.Devices = resp.Value;
                }
                // Also fetch description
                var descUrl = $"http://{server.Host}:{server.Port}/management/v1/description";
                var desc = await http.GetFromJsonAsync<AlpacaResponse<AlpacaServerDescription>>(descUrl);
                if (desc?.Value != null) {
                    server.ServerName = desc.Value.ServerName;
                    server.Manufacturer = desc.Value.Manufacturer;
                    server.ManufacturerVersion = desc.Value.ManufacturerVersion;
                }
            } catch (Exception ex) {
                _logger.LogDebug(ex, "Failed to query {Host}:{Port} for devices", server.Host, server.Port);
            }
        }

        _logger.LogInformation("Alpaca discovery found {N} server(s)", found.Count);
        return found.Values.ToList();
    }
}

public class AlpacaServer {
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string? ServerName { get; set; }
    public string? Manufacturer { get; set; }
    public string? ManufacturerVersion { get; set; }
    public List<AlpacaConfiguredDevice> Devices { get; set; } = new();
}

public class AlpacaConfiguredDevice {
    [JsonPropertyName("DeviceName")]
    public string DeviceName { get; set; } = "";

    [JsonPropertyName("DeviceType")]
    public string DeviceType { get; set; } = "";

    [JsonPropertyName("DeviceNumber")]
    public int DeviceNumber { get; set; }

    [JsonPropertyName("UniqueID")]
    public string UniqueID { get; set; } = "";
}

public class AlpacaServerDescription {
    [JsonPropertyName("ServerName")]
    public string ServerName { get; set; } = "";

    [JsonPropertyName("Manufacturer")]
    public string Manufacturer { get; set; } = "";

    [JsonPropertyName("ManufacturerVersion")]
    public string ManufacturerVersion { get; set; } = "";

    [JsonPropertyName("Location")]
    public string Location { get; set; } = "";
}
