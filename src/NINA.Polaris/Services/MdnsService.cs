using System.Net.NetworkInformation;
using System.Net.Sockets;
using Makaretu.Dns;

namespace NINA.Polaris.Services;

/// <summary>
/// Announces this N.I.N.A. Polaris instance on the local network via mDNS/Avahi
/// so that clients can find it without needing to know its IP address.
///
/// Registered service type: <c>_nina._tcp.local</c> on the configured Kestrel
/// port (defaults to 5000).
///
/// Instance name: when <c>Mdns:InstanceName</c> is NOT set, the name is made
/// unique per device automatically -- <c>polaris-app-XXXX</c>, where XXXX is
/// derived from a stable hardware id (Raspberry Pi serial, else primary MAC).
/// This lets a single pre-built SD-card image be cloned onto several Pis on
/// the same network without mDNS name collisions: each Pi self-names. A
/// human-readable label (<see cref="UserProfile.DeviceFriendlyName"/>) is
/// advertised in the TXT record under <c>friendly</c> so the client app can
/// show "Telescope on the balcony" instead of the raw hostname.
/// </summary>
public class MdnsService : IHostedService, IDisposable {
    private readonly ILogger<MdnsService> _logger;
    private readonly IConfiguration _config;
    private readonly ProfileService _profiles;
    private ServiceDiscovery? _discovery;
    private MulticastService? _mdns;
    private string _instanceName = "polaris-app";

    /// <summary>The mDNS instance name currently advertised (e.g. polaris-app-a1b2).</summary>
    public string InstanceName => _instanceName;

    public MdnsService(ILogger<MdnsService> logger, IConfiguration config, ProfileService profiles) {
        _logger = logger;
        _config = config;
        _profiles = profiles;
    }

    public Task StartAsync(CancellationToken cancellationToken) {
        if (!_config.GetValue("Mdns:Enabled", true)) {
            _logger.LogInformation("mDNS disabled by configuration");
            return Task.CompletedTask;
        }
        Advertise();
        return Task.CompletedTask;
    }

    /// <summary>
    /// (Re)publishes the mDNS announcement. Safe to call again after the
    /// friendly name changes -- it tears down the previous advertisement
    /// and starts a fresh one with the updated TXT record.
    /// </summary>
    public void Republish() {
        if (!_config.GetValue("Mdns:Enabled", true)) return;
        Teardown();
        Advertise();
    }

    private void Advertise() {
        try {
            var port = _config.GetValue("Mdns:Port", 5000);
            var hostname = Environment.MachineName;

            // Explicit override wins; otherwise auto-unique per device so a
            // cloned image doesn't collide on the LAN.
            var configured = _config["Mdns:InstanceName"];
            _instanceName = !string.IsNullOrWhiteSpace(configured)
                ? configured!
                : $"polaris-app-{DeviceShortId()}";

            // Friendly, human-set label (falls back to the instance name).
            var friendly = _profiles.Active.DeviceFriendlyName;
            if (string.IsNullOrWhiteSpace(friendly)) friendly = _instanceName;

            _mdns = new MulticastService();
            _discovery = new ServiceDiscovery(_mdns);

            // Both ServiceProfile constructors build HostName as
            // `{instance}.{servicePrefix}.local`, so for service "_nina._tcp"
            // we got `polaris-app.nina.local` in the wild. Override HostName
            // to what we WANT the browser to resolve, patch the SRV record,
            // then add A/AAAA records mapping that HostName to our local IPs.
            var profile = new ServiceProfile(_instanceName, "_nina._tcp", (ushort)port);
            var desiredHost = new DomainName($"{_instanceName}.local");
            profile.HostName = desiredHost;
            foreach (var srv in profile.Resources.OfType<SRVRecord>()) {
                srv.Target = desiredHost;
            }

            var addresses = MulticastService.GetIPAddresses()
                .Where(addr =>
                    (addr.AddressFamily == AddressFamily.InterNetwork
                     || addr.AddressFamily == AddressFamily.InterNetworkV6)
                    && !System.Net.IPAddress.IsLoopback(addr))
                .ToList();
            foreach (var ip in addresses) {
                if (ip.AddressFamily == AddressFamily.InterNetwork) {
                    profile.Resources.Add(new ARecord {
                        Name = desiredHost, Address = ip
                    });
                } else {
                    profile.Resources.Add(new AAAARecord {
                        Name = desiredHost, Address = ip
                    });
                }
            }

            profile.AddProperty("version", "1.0");
            profile.AddProperty("path", "/");
            profile.AddProperty("hostname", hostname);
            profile.AddProperty("friendly", friendly);

            _discovery.Advertise(profile);
            _mdns.Start();

            _logger.LogInformation(
                "mDNS advertising as {Instance}._nina._tcp.local at "
                + "{HostName}:{Port} (friendly: {Friendly}, machine: {Hostname}, {AddrCount} IPs)",
                _instanceName, profile.HostName, port, friendly, hostname, addresses.Count);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "mDNS announcer failed to start, continuing without LAN discovery");
            _mdns = null;
            _discovery = null;
        }
    }

    /// <summary>
    /// A short, stable, per-device id. Prefers the Raspberry Pi hardware
    /// serial, then the primary MAC address, then a hash of the machine
    /// name. Lowercase hex, 4 chars -- enough to disambiguate the handful
    /// of Pis a hobbyist runs on one network while keeping the name short.
    /// </summary>
    private static string DeviceShortId() {
        // Raspberry Pi exposes the board serial in the device tree.
        try {
            const string dt = "/sys/firmware/devicetree/base/serial-number";
            if (File.Exists(dt)) {
                var s = File.ReadAllText(dt).Trim('\0', ' ', '\n', '\r', '\t');
                if (s.Length >= 4) return s[^4..].ToLowerInvariant();
            }
        } catch { /* fall through */ }

        // /proc/cpuinfo "Serial" line (older Pi OS / other ARM boards).
        try {
            foreach (var line in File.ReadLines("/proc/cpuinfo")) {
                if (line.StartsWith("Serial", StringComparison.OrdinalIgnoreCase)) {
                    var v = line.Split(':').Last().Trim();
                    if (v.Length >= 4) return v[^4..].ToLowerInvariant();
                }
            }
        } catch { /* fall through */ }

        // Primary non-loopback MAC.
        try {
            var mac = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                    && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(n => n.GetPhysicalAddress().ToString())
                .FirstOrDefault(m => !string.IsNullOrEmpty(m) && m.Trim('0').Length > 0);
            if (!string.IsNullOrEmpty(mac) && mac.Length >= 4) return mac[^4..].ToLowerInvariant();
        } catch { /* fall through */ }

        // Last resort: stable hash of the machine name.
        var h = Math.Abs(Environment.MachineName.GetHashCode());
        return (h % 0x10000).ToString("x4");
    }

    private void Teardown() {
        try {
            _mdns?.Stop();
            _discovery?.Dispose();
        } catch (Exception ex) {
            _logger.LogDebug(ex, "mDNS teardown error (ignored)");
        }
        _mdns = null;
        _discovery = null;
    }

    public Task StopAsync(CancellationToken cancellationToken) {
        Teardown();
        return Task.CompletedTask;
    }

    public void Dispose() {
        _discovery?.Dispose();
    }
}
