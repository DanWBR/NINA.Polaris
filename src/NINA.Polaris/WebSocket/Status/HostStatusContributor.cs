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

using NINA.Polaris.Services;

namespace NINA.Polaris.WebSocket.Status;

/// <summary>
/// The machine itself, its notifications, and the debug-log tail.
///
/// Blocks owned: host, server, notifications, storagePush, debugLog.
/// </summary>
public sealed class HostStatusContributor : IStatusContributor {
    private readonly ClockSyncService _clockSync;
    private readonly HostMetricsService _hostMetrics;
    private readonly NINA.Polaris.Services.Logging.LogService _logService;
    private readonly NotificationService _notifications;
    private readonly StoragePushService _storagePush;

    public HostStatusContributor(ClockSyncService clockSync, HostMetricsService hostMetrics, NINA.Polaris.Services.Logging.LogService logService, NotificationService notifications, StoragePushService storagePush) {
        _clockSync = clockSync;
        _hostMetrics = hostMetrics;
        _logService = logService;
        _notifications = notifications;
        _storagePush = storagePush;
    }

    public IReadOnlyCollection<string> Keys { get; } = new[] { "host", "server", "notifications", "storagePush", "debugLog" };

    public void Contribute(StatusTick tick) {
        var clockSync = _clockSync;
        var hostMetrics = _hostMetrics;
        var logService = _logService;
        var notifications = _notifications;
        var storagePush = _storagePush;

            var localDebugCursor = tick.DebugCursor;
            tick.Blocks["host"] = hostMetrics.Latest;

            tick.Blocks["server"] = new {
                utcNow = DateTime.UtcNow.ToString("o"),
                clockSyncSupported = clockSync.IsSupported
            };

            // Server-pushed toasts (auto-connect outcomes,
            // simulator events, etc.). Client de-dups by id
            //, see toast pump in app.js.
            tick.Blocks["notifications"] = notifications.Snapshot();

            // SIM-4: built-in equipment simulator status
            // (which backend is active, is it installed,
            // is the stack running, which devices). UI
            // shows a green/amber chip + the Settings
            // panel binds to these fields.
            tick.Blocks["storagePush"] = new {
                enabled       = storagePush.Enabled,
                kind          = storagePush.Kind,
                connected     = storagePush.Connected,
                queued        = storagePush.Queued,
                uploaded      = storagePush.Uploaded,
                failed        = storagePush.Failed,
                currentFile   = storagePush.CurrentFile,
                // SHARESYNC-2: byte progress of the active transfer (0/0 idle).
                currentBytes      = storagePush.CurrentBytes,
                currentTotalBytes = storagePush.CurrentTotalBytes,
                lastError     = storagePush.LastError,
                lastUploadUtc = storagePush.LastUploadUtc?.ToString("o"),
                // Recordings upload on their own lane. Reported
                // separately because a multi-GB .ser keeps the
                // count at 1 for a long time, and folded into the
                // image total that reads like a stuck queue.
                videoQueued      = storagePush.VideoQueued,
                videoUploaded    = storagePush.VideoUploaded,
                videoCurrentFile = storagePush.VideoCurrentFile
            };

            // A removable USB drive plugged in at runtime, awaiting the
            // user's yes/no to move the capture home onto it. null when
            // nothing is pending. See UsbDriveWatcherService.
            tick.Blocks["debugLog"] = BuildDebugLogPayload(logService, ref localDebugCursor);

            // Live plate-solve console output (STUDIO/FILES),
            // streamed so the UI can show the solver running
            // the same way the GraXpert local run does.
            tick.DebugCursor = localDebugCursor;
    }

/// <summary>DBGLOG-5: build the per-tick <c>debugLog</c> sub-object.
/// Mutates <paramref name="cursor"/> in place so the caller's
/// per-connection state advances. Returns null when there's nothing
/// new (caller can skip serialising) but actually serialises as the
/// `null` field so the client sees consistent shape.</summary>
    private static object BuildDebugLogPayload(
        NINA.Polaris.Services.Logging.LogService svc, ref long cursor) {
        try {
            var snap = svc.SnapshotSince(cursor, max: 50);
            if (snap.Entries.Count > 0) cursor = snap.Cursor;
            return new {
                entries = snap.Entries,
                cursor = snap.Cursor,
                truncated = snap.Truncated,
                currentCursor = svc.CurrentId,
                oldestRetained = svc.OldestId
            };
        } catch {
            // Never let the debug-log subsystem take down the WS tick.
            return new {
                entries = System.Array.Empty<object>(),
                cursor,
                truncated = false,
                currentCursor = 0L,
                oldestRetained = 0L
            };
        }
    }
}
