using System.Collections.Concurrent;
using NINA.Core.Utility;
using NINA.INDI.Protocol;

namespace NINA.INDI.Client;

/// <summary>
/// Receives INDI BLOB data from an <see cref="IndiClient"/> and writes it to
/// temporary files instead of keeping the full byte[] in memory.  A background
/// cleanup timer removes files that exceed the configured TTL.
/// </summary>
public class IndiBlobReceiver : IDisposable {
    private readonly IndiClient _client;
    private readonly string _outputDirectory;
    private readonly TimeSpan _ttl;
    private readonly ConcurrentDictionary<string, BlobFileInfo> _trackedFiles = new();
    private readonly Timer _cleanupTimer;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    /// <summary>
    /// Raised after a BLOB has been successfully written to disk.
    /// </summary>
    public event Action<BlobFileInfo>? BlobSaved;

    /// <param name="client">The INDI client whose BlobReceived events will be handled.</param>
    /// <param name="outputDirectory">
    /// Directory for temporary BLOB files.  Defaults to a subdirectory of the
    /// system temp folder when <see langword="null"/>.
    /// </param>
    /// <param name="ttl">
    /// Time-to-live for saved BLOB files.  Files older than this are removed by
    /// the background cleanup timer.  Defaults to 5 minutes.
    /// </param>
    public IndiBlobReceiver(IndiClient client, string? outputDirectory = null, TimeSpan? ttl = null) {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ttl = ttl ?? TimeSpan.FromMinutes(5);

        _outputDirectory = outputDirectory ?? Path.Combine(Path.GetTempPath(), "NINA_INDI_BLOBs");
        Directory.CreateDirectory(_outputDirectory);

        _client.BlobReceived += OnBlobReceived;

        // Run cleanup every 60 seconds.
        _cleanupTimer = new Timer(_ => CleanupExpiredFiles(), null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));

        Logger.Info($"IndiBlobReceiver started — output: {_outputDirectory}, TTL: {_ttl.TotalSeconds:F0}s");
    }

    /// <summary>
    /// Returns all currently tracked BLOB files.
    /// </summary>
    public IReadOnlyCollection<BlobFileInfo> TrackedFiles => _trackedFiles.Values.ToList().AsReadOnly();

    private void OnBlobReceived(IndiBlobProperty blob) {
        if (_cts.IsCancellationRequested) return;

        foreach (var (elementName, element) in blob.Values) {
            if (element.Data == null || element.Data.Length == 0) continue;

            try {
                SaveBlob(blob.Device, blob.Name, elementName, element);
            } catch (Exception ex) {
                Logger.Error($"IndiBlobReceiver failed to save BLOB {blob.Device}.{blob.Name}/{elementName}: {ex.Message}");
            }
        }
    }

    private void SaveBlob(string device, string property, string element, IndiBlobElement blobElement) {
        string sanitizedDevice = SanitizeFileName(device);
        string sanitizedProperty = SanitizeFileName(property);
        string sanitizedElement = SanitizeFileName(element);
        string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
        string extension = NormalizeExtension(blobElement.Format);

        string fileName = $"{sanitizedDevice}_{sanitizedProperty}_{sanitizedElement}_{timestamp}{extension}";
        string filePath = Path.Combine(_outputDirectory, fileName);

        File.WriteAllBytes(filePath, blobElement.Data!);

        var info = new BlobFileInfo {
            DeviceName = device,
            PropertyName = property,
            FilePath = filePath,
            Format = blobElement.Format,
            Size = blobElement.Data!.Length,
            Timestamp = DateTime.UtcNow
        };

        _trackedFiles[filePath] = info;

        Logger.Info($"IndiBlobReceiver saved {info.Size:N0} bytes to {filePath}");

        BlobSaved?.Invoke(info);
    }

    private void CleanupExpiredFiles() {
        if (_disposed) return;

        DateTime cutoff = DateTime.UtcNow - _ttl;
        int removed = 0;

        foreach (var (path, info) in _trackedFiles) {
            if (info.Timestamp >= cutoff) continue;

            if (_trackedFiles.TryRemove(path, out _)) {
                try {
                    if (File.Exists(path)) {
                        File.Delete(path);
                        removed++;
                    }
                } catch (Exception ex) {
                    Logger.Warning($"IndiBlobReceiver failed to delete expired file {path}: {ex.Message}");
                }
            }
        }

        if (removed > 0) {
            Logger.Debug($"IndiBlobReceiver cleaned up {removed} expired BLOB file(s)");
        }
    }

    /// <summary>
    /// Immediately removes all tracked files from disk and clears the tracking dictionary.
    /// </summary>
    public void ClearAll() {
        foreach (var (path, _) in _trackedFiles) {
            if (_trackedFiles.TryRemove(path, out _)) {
                try {
                    if (File.Exists(path)) File.Delete(path);
                } catch (Exception ex) {
                    Logger.Warning($"IndiBlobReceiver failed to delete {path}: {ex.Message}");
                }
            }
        }
    }

    private static string NormalizeExtension(string format) {
        // INDI format strings are typically ".fits", ".fits.z", ".jpg", etc.
        if (string.IsNullOrWhiteSpace(format)) return ".bin";
        return format.StartsWith('.') ? format : $".{format}";
    }

    private static string SanitizeFileName(string name) {
        char[] invalid = Path.GetInvalidFileNameChars();
        var sanitized = new char[name.Length];
        for (int i = 0; i < name.Length; i++) {
            sanitized[i] = Array.IndexOf(invalid, name[i]) >= 0 ? '_' : name[i];
        }
        return new string(sanitized);
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _client.BlobReceived -= OnBlobReceived;
        _cleanupTimer.Dispose();
        _cts.Dispose();

        Logger.Info("IndiBlobReceiver disposed");
    }
}

/// <summary>
/// Metadata about a BLOB that has been written to disk.
/// </summary>
public class BlobFileInfo {
    public string DeviceName { get; init; } = string.Empty;
    public string PropertyName { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;

    /// <summary>
    /// INDI format string, e.g. ".fits", ".fits.z", ".jpg".
    /// </summary>
    public string Format { get; init; } = string.Empty;

    /// <summary>
    /// Size in bytes of the decoded BLOB data written to disk.
    /// </summary>
    public long Size { get; init; }

    public DateTime Timestamp { get; init; }
}
