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

using System.Net;
using SMBLibrary;
using SMBLibrary.Client;

namespace NINA.Polaris.Services.Storage;

/// <summary>
/// SMB2/CIFS backend via the managed SMBLibrary (MIT). Works against Windows
/// shares, Samba and NAS boxes (Synology/QNAP). Mirrors the capture tree inside
/// the chosen share, creating directories as needed and streaming the file in
/// MaxWriteSize chunks so large FITS frames don't load fully into memory.
/// </summary>
public sealed class SmbStorageTarget : IStorageTarget {
    private SMB2Client? _client;
    private ISMBFileStore? _store;
    private int _linkShare = 100;

    public string Kind => "smb";

    public Task ConnectAsync(StorageConfig cfg, CancellationToken ct) {
        var (client, store) = Open(cfg);
        _client = client;
        _store = store;
        _linkShare = cfg.LinkSharePercent;
        return Task.CompletedTask;
    }

    public async Task UploadAsync(string localPath, string relPath, CancellationToken ct) {
        if (_client is null || _store is null) throw new InvalidOperationException("SMB not connected");
        var segs = StoragePath.Segments(relPath);

        // Create each directory level (SMB has no recursive mkdir).
        for (int i = 0; i < segs.Length - 1; i++) {
            var dirPath = string.Join('\\', segs.Take(i + 1));
            var st = _store.CreateFile(out var dirHandle, out _, dirPath,
                AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE, SMBLibrary.FileAttributes.Directory,
                ShareAccess.Read | ShareAccess.Write,
                CreateDisposition.FILE_OPEN_IF,
                CreateOptions.FILE_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT, null);
            if (st == NTStatus.STATUS_SUCCESS && dirHandle != null) _store.CloseFile(dirHandle);
            else if (st != NTStatus.STATUS_OBJECT_NAME_COLLISION)
                throw new IOException($"SMB mkdir '{dirPath}' failed: {st}");
        }

        var filePath = string.Join('\\', segs);

        // One-way-sync idempotency: skip when the destination already exists with
        // the same size. Without this, a backfill (or a retry) would re-copy the
        // whole tree, because the create below opens with FILE_OVERWRITE_IF.
        // Size-only is the right check for capture files — FITS/SER are written
        // once and never edited in place — and it keeps the normal push path
        // one cheap metadata round-trip away from its previous behaviour (a new
        // file simply isn't found and uploads as before).
        long localLen;
        try { localLen = new FileInfo(localPath).Length; } catch { localLen = -1; }
        if (localLen >= 0 && TryGetRemoteLength(filePath, out var remoteLen) && remoteLen == localLen)
            return;

        var status = _store.CreateFile(out var handle, out _, filePath,
            AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE, SMBLibrary.FileAttributes.Normal,
            ShareAccess.None, CreateDisposition.FILE_OVERWRITE_IF,
            CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT, null);
        if (status != NTStatus.STATUS_SUCCESS || handle == null)
            throw new IOException($"SMB create '{filePath}' failed: {status}");

        try {
            using var fs = File.OpenRead(localPath);
            int chunk = (int)Math.Min(_client.MaxWriteSize, 1 << 20);
            if (chunk <= 0) chunk = 1 << 20;
            var buffer = new byte[chunk];
            long offset = 0;
            int read;
            // Paced, and asynchronous. This loop used to run flat out and
            // synchronously: it took the whole uplink the operator's browser
            // was on (the link watchdog then reported a lost connection several
            // times per pushed frame) and it held a thread-pool thread for the
            // entire upload. See TransferPacer.
            while ((read = await fs.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0) {
                ct.ThrowIfCancellationRequested();
                var started = System.Diagnostics.Stopwatch.StartNew();
                var data = read == buffer.Length ? buffer : buffer[..read];
                var ws = _store.WriteFile(out int written, handle, offset, data);
                if (ws != NTStatus.STATUS_SUCCESS)
                    throw new IOException($"SMB write '{filePath}' failed: {ws}");
                offset += written;

                var idle = TransferPacer.DelayAfterChunk(started.Elapsed, _linkShare);
                if (idle > TimeSpan.Zero) await Task.Delay(idle, ct);
            }
        } finally {
            _store.CloseFile(handle);
        }
    }

    /// <summary>Best-effort remote file size, for the one-way-sync skip. Opens
    /// the file read-only and reads FileStandardInformation. Returns false when
    /// the file doesn't exist (the common case on a normal push) or the query
    /// fails, so the caller falls through to a normal upload.</summary>
    private bool TryGetRemoteLength(string filePath, out long length) {
        length = -1;
        if (_store is null) return false;
        var st = _store.CreateFile(out var handle, out _, filePath,
            AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, SMBLibrary.FileAttributes.Normal,
            ShareAccess.Read, CreateDisposition.FILE_OPEN,
            CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT, null);
        if (st != NTStatus.STATUS_SUCCESS || handle == null) return false;
        try {
            var info = _store.GetFileInformation(out var result, handle,
                FileInformationClass.FileStandardInformation);
            if (info == NTStatus.STATUS_SUCCESS && result is FileStandardInformation std) {
                length = std.EndOfFile;
                return true;
            }
            return false;
        } catch {
            return false;
        } finally {
            try { _store.CloseFile(handle); } catch { }
        }
    }

    public Task<(bool ok, string message)> TestAsync(StorageConfig cfg, CancellationToken ct) {
        SMB2Client? client = null;
        try {
            var (c, store) = Open(cfg);
            client = c;
            store.Disconnect();
            return Task.FromResult((true, $"Connected to \\\\{cfg.Host}\\{cfg.Share}"));
        } catch (Exception ex) {
            return Task.FromResult((false, ex.Message));
        } finally {
            try { client?.Logoff(); } catch { }
            try { client?.Disconnect(); } catch { }
        }
    }

    private static (SMB2Client client, ISMBFileStore store) Open(StorageConfig cfg) {
        if (string.IsNullOrWhiteSpace(cfg.Host)) throw new InvalidOperationException("SMB host is empty.");
        if (string.IsNullOrWhiteSpace(cfg.Share)) throw new InvalidOperationException("SMB share is empty.");

        var address = ResolveIPv4(cfg.Host);
        var client = new SMB2Client();
        bool connected = client.Connect(address, SMBTransportType.DirectTCPTransport);
        if (!connected) throw new IOException($"Could not connect to {cfg.Host}:{(cfg.Port > 0 ? cfg.Port : 445)}");

        var login = client.Login(cfg.Domain ?? "", cfg.Username, cfg.Password);
        if (login != NTStatus.STATUS_SUCCESS) {
            try { client.Disconnect(); } catch { }
            throw new UnauthorizedAccessException($"SMB login failed: {login}");
        }

        var store = client.TreeConnect(cfg.Share, out var ts);
        if (ts != NTStatus.STATUS_SUCCESS || store == null) {
            try { client.Logoff(); client.Disconnect(); } catch { }
            throw new IOException($"SMB share '{cfg.Share}' not accessible: {ts}");
        }
        return (client, store);
    }

    private static IPAddress ResolveIPv4(string host) {
        host = (host ?? "").Trim().TrimStart('\\').TrimEnd('\\');
        if (IPAddress.TryParse(host, out var ip)) return ip;

        // Try the name as given, then an mDNS `.local` fallback. Bare Windows/
        // NetBIOS box names (e.g. "DESKTOP-ABC") aren't resolvable by a plain
        // DNS lookup on the SBC — getaddrinfo returns "Name or service not
        // known". Most Windows/NAS boxes with Bonjour/avahi also answer at
        // "<name>.local", which the SBC can resolve via mDNS (avahi/libnss-mdns).
        var candidates = new List<string> { host };
        if (!host.Contains('.')) candidates.Add(host + ".local");
        foreach (var name in candidates) {
            try {
                var addrs = Dns.GetHostAddresses(name);
                var v4 = addrs.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                if (v4 != null) return v4;
                if (addrs.Length > 0) return addrs[0];
            } catch (System.Net.Sockets.SocketException) {
                // Unresolvable; fall through to the next candidate.
            }
        }
        throw new IOException(
            $"Could not resolve host '{host}'. Use the server's IP address " +
            $"(e.g. 192.168.1.50), or a name the network can resolve such as '{host}.local'.");
    }

    public void Disconnect() {
        try { _store?.Disconnect(); } catch { }
        try { _client?.Logoff(); } catch { }
        try { _client?.Disconnect(); } catch { }
        _store = null;
        _client = null;
    }

    public void Dispose() => Disconnect();
}
