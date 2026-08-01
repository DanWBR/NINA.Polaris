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
    private Timer? _readvertiseTimer;
    private readonly object _advertiseLock = new();

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
        // Re-announce when the machine's addresses change. The A/AAAA
        // records are snapshotted inside Advertise(), so an interface that
        // appears AFTER startup — most importantly the hotspot AP interface
        // when the user (or the auto-fallback watchdog) enables the Polaris
        // hotspot at runtime — was never advertised and hotspot clients
        // could not discover the server. Debounced: NetworkAddressChanged
        // fires in bursts while NetworkManager reconfigures.
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        return Task.CompletedTask;
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e) {
        lock (_advertiseLock) {
            _readvertiseTimer?.Dispose();
            _readvertiseTimer = new Timer(_ => {
                try {
                    _logger.LogInformation("Network addresses changed - re-announcing mDNS");
                    Republish();
                } catch (Exception ex) {
                    _logger.LogDebug(ex, "mDNS re-announce after address change failed");
                }
            }, null, dueTime: 3000, period: Timeout.Infinite);
        }
    }

    /// <summary>
    /// (Re)publishes the mDNS announcement. Safe to call again after the
    /// friendly name changes -- it tears down the previous advertisement
    /// and starts a fresh one with the updated TXT record.
    /// </summary>
    public void Republish() {
        if (!_config.GetValue("Mdns:Enabled", true)) return;
        // Serialized: callable from the device-name endpoint AND the
        // address-change debounce timer (threadpool) concurrently.
        lock (_advertiseLock) {
            Teardown();
            Advertise();
        }
    }

    private void Advertise() {
        try {
            var port = _config.GetValue("Mdns:Port", 5000);
            var hostname = Environment.MachineName;

            // Explicit override wins; otherwise auto-unique per device so a
            // cloned image doesn't collide on the LAN. Shared with
            // SelfSignedCertService through DeviceIdentity so the advertised
            // name and the certificate can never disagree -- they did, and
            // every discovery-based connection failed validation because of it.
            _instanceName = DeviceIdentity.InstanceName(_config);

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
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        lock (_advertiseLock) {
            _readvertiseTimer?.Dispose();
            _readvertiseTimer = null;
        }
        Teardown();
        return Task.CompletedTask;
    }

    public void Dispose() {
        _discovery?.Dispose();
    }
}