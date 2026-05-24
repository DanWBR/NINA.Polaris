using System.Net.Sockets;
using Makaretu.Dns;

namespace NINA.Polaris.Services;

/// <summary>
/// Announces this N.I.N.A. Polaris instance on the local network via mDNS/Avahi
/// so that clients can find it at <c>nina.local:5000</c> (or whatever hostname
/// the OS reports) without needing to know its IP address.
///
/// Registered service type: <c>_nina._tcp.local</c> on the configured Kestrel
/// port (defaults to 5000). Instance name defaults to the machine hostname
/// but can be overridden via <c>Mdns:InstanceName</c> in appsettings.
/// </summary>
public class MdnsService : IHostedService, IDisposable {
    private readonly ILogger<MdnsService> _logger;
    private readonly IConfiguration _config;
    private ServiceDiscovery? _discovery;
    private MulticastService? _mdns;

    public MdnsService(ILogger<MdnsService> logger, IConfiguration config) {
        _logger = logger;
        _config = config;
    }

    public Task StartAsync(CancellationToken cancellationToken) {
        if (!_config.GetValue("Mdns:Enabled", true)) {
            _logger.LogInformation("mDNS disabled by configuration");
            return Task.CompletedTask;
        }

        try {
            var port = _config.GetValue("Mdns:Port", 5000);
            var hostname = Environment.MachineName;
            // Default instance name is the literal "polaris" when
            // possible — friendly http://polaris.local on most LANs.
            // To avoid colliding with another device the user already
            // gave that name (e.g. a Raspberry Pi running a separate
            // mDNS responder claiming polaris.local), suffix with the
            // sanitized machine hostname when we can derive one. So a
            // workstation called ASUSPROART resolves on first run as
            // http://polaris-asusproart.local without any setup.
            // Explicit Mdns:InstanceName in appsettings still wins
            // for users who want the bare "polaris".
            var defaultInstance = BuildDefaultInstanceName(hostname);
            var instanceName = _config.GetValue("Mdns:InstanceName", defaultInstance)!;

            _mdns = new MulticastService();
            _discovery = new ServiceDiscovery(_mdns);

            // Collect every routable local IPv4 / IPv6 address so we
            // can register A / AAAA records under {instanceName}.local.
            // Without this, the ServiceProfile only registers an SRV
            // record pointing at Environment.MachineName.local — that's
            // discoverable by mDNS browsers but http://polaris.local
            // doesn't resolve, which defeats the whole point of the
            // friendly URL. Filter out the loopback addresses; they
            // wouldn't help a remote browser.
            var addresses = MulticastService.GetIPAddresses()
                .Where(addr =>
                    (addr.AddressFamily == AddressFamily.InterNetwork
                     || addr.AddressFamily == AddressFamily.InterNetworkV6)
                    && !System.Net.IPAddress.IsLoopback(addr))
                .ToList();

            var profile = new ServiceProfile(
                instanceName, "_nina._tcp", (ushort)port, addresses);
            profile.AddProperty("version", "1.0");
            profile.AddProperty("path", "/");
            profile.AddProperty("hostname", hostname);

            _discovery.Advertise(profile);
            _mdns.Start();

            _logger.LogInformation(
                "mDNS advertising as {Instance}._nina._tcp.local at "
                + "{HostName}:{Port} (machine: {Hostname}, {AddrCount} IPs)",
                instanceName, profile.HostName, port, hostname, addresses.Count);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "mDNS announcer failed to start — continuing without LAN discovery");
            _mdns = null;
            _discovery = null;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) {
        try {
            _mdns?.Stop();
            _discovery?.Dispose();
        } catch (Exception ex) {
            _logger.LogDebug(ex, "mDNS shutdown error (ignored)");
        }
        _mdns = null;
        _discovery = null;
        return Task.CompletedTask;
    }

    public void Dispose() {
        _discovery?.Dispose();
    }

    /// <summary>
    /// "polaris-{sanitizedHostname}" when we have a hostname, plain
    /// "polaris" otherwise. Sanitizes to mDNS-safe characters: lowercase
    /// letters, digits, hyphens. Anything else collapses to a hyphen
    /// (then runs of hyphens are coalesced and trimmed). Caps the
    /// suffix at 24 chars so the whole instance name stays well under
    /// the 63-char DNS label limit.
    /// </summary>
    private static string BuildDefaultInstanceName(string hostname) {
        if (string.IsNullOrWhiteSpace(hostname)) return "polaris";
        var chars = hostname.ToLowerInvariant().Select(c =>
            (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ? c : '-').ToArray();
        var sanitized = new string(chars);
        // Collapse runs of hyphens and trim leading/trailing.
        while (sanitized.Contains("--")) sanitized = sanitized.Replace("--", "-");
        sanitized = sanitized.Trim('-');
        if (sanitized.Length > 24) sanitized = sanitized.Substring(0, 24).Trim('-');
        return string.IsNullOrEmpty(sanitized) ? "polaris" : $"polaris-{sanitized}";
    }
}
