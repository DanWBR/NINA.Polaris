using System.Collections.Concurrent;
using System.Net.WebSockets;
using NINA.Relay.Protocol;

namespace NINA.Relay.Server;

/// <summary>
/// Per-tenant tunnel state. A tenant is one NINA Headless instance —
/// identified by the bearer token it used to authenticate. The relay server
/// holds the active tunnel WebSocket here and tracks all the in-flight
/// proxied requests waiting for responses to come back from the client.
/// </summary>
public class TenantTunnel {
    public string Token { get; }
    public string Hostname { get; }
    public WebSocket Socket { get; }
    public SemaphoreSlim SendLock { get; } = new(1, 1);
    public DateTime ConnectedAt { get; } = DateTime.UtcNow;

    private readonly ConcurrentDictionary<uint, TaskCompletionSource<ResponseMessage>> _pendingHttp = new();
    private uint _nextStreamId = 1;

    public TenantTunnel(string token, string hostname, WebSocket socket) {
        Token = token;
        Hostname = hostname;
        Socket = socket;
    }

    public uint AllocateStreamId() => Interlocked.Increment(ref _nextStreamId);

    /// <summary>Register a pending HTTP request and await the matching response.</summary>
    public Task<ResponseMessage> AwaitResponseAsync(uint streamId, CancellationToken ct) {
        var tcs = new TaskCompletionSource<ResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingHttp[streamId] = tcs;
        ct.Register(() => {
            if (_pendingHttp.TryRemove(streamId, out var t))
                t.TrySetCanceled();
        });
        return tcs.Task;
    }

    public void CompleteResponse(uint streamId, ResponseMessage message) {
        if (_pendingHttp.TryRemove(streamId, out var tcs))
            tcs.TrySetResult(message);
    }

    public void FailAllPending(string reason) {
        foreach (var kv in _pendingHttp) {
            kv.Value.TrySetException(new IOException(reason));
        }
        _pendingHttp.Clear();
    }
}

public record ResponseMessage(int Status, List<KeyValuePair<string, string>> Headers, byte[] Body);

/// <summary>
/// In-memory tenant lookup. For a real public deployment this would be backed
/// by a database with per-tenant secrets and quotas; for self-hosters a static
/// config-file dictionary is enough.
/// </summary>
public class TenantRegistry {
    private readonly ConcurrentDictionary<string, TenantTunnel> _activeByToken = new();
    private readonly ConcurrentDictionary<string, TenantTunnel> _activeByHostname = new();
    private readonly Dictionary<string, string> _tokenToHostname; // token → hostname slug

    public TenantRegistry(IConfiguration config) {
        // Tenants:<token> = <hostname-slug>
        // e.g. Tenants:abc123 = "alice"
        _tokenToHostname = config.GetSection("Tenants").GetChildren()
            .ToDictionary(c => c.Key, c => c.Value!, StringComparer.OrdinalIgnoreCase);
    }

    public bool TryAuthenticate(string token, out string hostname) {
        return _tokenToHostname.TryGetValue(token, out hostname!);
    }

    public bool TryRegister(TenantTunnel tunnel) {
        // Replace any existing tunnel for this token (last writer wins — handy
        // for restart-after-crash; the orphan WS will fail on next send).
        if (_activeByToken.TryGetValue(tunnel.Token, out var existing)) {
            existing.FailAllPending("Tunnel replaced by new connection");
        }
        _activeByToken[tunnel.Token] = tunnel;
        _activeByHostname[tunnel.Hostname] = tunnel;
        return true;
    }

    public void Unregister(TenantTunnel tunnel) {
        _activeByToken.TryRemove(new KeyValuePair<string, TenantTunnel>(tunnel.Token, tunnel));
        _activeByHostname.TryRemove(new KeyValuePair<string, TenantTunnel>(tunnel.Hostname, tunnel));
        tunnel.FailAllPending("Tunnel closed");
    }

    public TenantTunnel? GetByHostname(string hostname) {
        _activeByHostname.TryGetValue(hostname, out var t);
        return t;
    }

    public IEnumerable<TenantTunnel> ActiveTunnels => _activeByToken.Values;
}
