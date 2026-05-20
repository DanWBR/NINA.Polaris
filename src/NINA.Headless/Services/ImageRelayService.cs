using System.Collections.Concurrent;
using System.Net.WebSockets;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;

namespace NINA.Headless.Services;

public class ImageRelayService : IDisposable {
    private readonly ConcurrentDictionary<string, ClientEntry> _clients = new();
    private readonly ILogger<ImageRelayService> _logger;
    private ImageBuffer? _latestImage;
    private byte[]? _latestJpeg;

    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);
    private const int MaxConsecutiveFailures = 3;

    public ImageRelayService(ILogger<ImageRelayService> logger) {
        _logger = logger;
    }

    public void RegisterClient(string id, System.Net.WebSockets.WebSocket ws) {
        _clients[id] = new ClientEntry(ws);
        _logger.LogInformation("Image stream client registered: {Id} (total: {Count})", id, _clients.Count);
    }

    public void UnregisterClient(string id) {
        if (_clients.TryRemove(id, out var entry)) {
            entry.SendLock.Dispose();
            _logger.LogInformation("Image stream client removed: {Id} (remaining: {Count})", id, _clients.Count);
        }
    }

    public async Task RelayImageAsync(IImageData imageData, CancellationToken ct = default) {
        var buffer = ImageBuffer.FromImageData(imageData);
        _latestImage = buffer;
        _latestJpeg = null;

        if (_clients.IsEmpty) return;

        // Prepare both formats lazily (only if needed)
        byte[]? rawFrame = null;
        byte[]? jpegFrame = null;

        bool needsRaw = false, needsJpeg = false;
        foreach (var (_, entry) in _clients) {
            if (entry.Ws.State == WebSocketState.Open) {
                if (entry.Mode == StreamMode.Raw) needsRaw = true;
                else needsJpeg = true;
            }
        }

        if (needsRaw) {
            var header = buffer.GetStreamHeader();
            var compressed = buffer.ToLz4Compressed();

            _logger.LogInformation(
                "Relaying image {W}x{H} ({BitDepth}-bit): {RawMB:F1}MB raw -> {CompMB:F1}MB LZ4 ({Ratio:F1}x) to {Count} clients",
                buffer.Width, buffer.Height, buffer.BitDepth,
                (double)imageData.Data.Length * 2 / (1024 * 1024),
                (double)compressed.Length / (1024 * 1024),
                (double)imageData.Data.Length * 2 / Math.Max(compressed.Length, 1),
                _clients.Count);

            rawFrame = new byte[4 + header.Length + compressed.Length];
            BitConverter.GetBytes(header.Length).CopyTo(rawFrame, 0);
            header.CopyTo(rawFrame, 4);
            compressed.CopyTo(rawFrame, 4 + header.Length);
        }

        if (needsJpeg) {
            jpegFrame = buffer.ToJpeg(85);
            _logger.LogInformation(
                "Relaying JPEG {W}x{H}: {SizeKB:F0}KB to JPEG clients",
                buffer.Width, buffer.Height,
                (double)jpegFrame.Length / 1024);
        }

        var deadClients = new List<string>();

        foreach (var (id, entry) in _clients) {
            if (entry.Ws.State != WebSocketState.Open) {
                deadClients.Add(id);
                continue;
            }

            var frame = entry.Mode == StreamMode.Raw ? rawFrame : jpegFrame;
            if (frame == null) continue;

            // Skip clients that are still sending the previous frame (backpressure)
            if (!entry.SendLock.Wait(0)) {
                entry.SkippedFrames++;
                if (entry.SkippedFrames % 10 == 0) {
                    _logger.LogWarning("Client {Id} skipped {Count} frames (slow consumer)", id, entry.SkippedFrames);
                }
                continue;
            }

            try {
                using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                sendCts.CancelAfter(SendTimeout);
                await entry.Ws.SendAsync(frame, WebSocketMessageType.Binary, true, sendCts.Token);
                entry.ConsecutiveFailures = 0;
                entry.SkippedFrames = 0;
            } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
                entry.ConsecutiveFailures++;
                _logger.LogWarning("Send to client {Id} timed out (failure {N}/{Max})",
                    id, entry.ConsecutiveFailures, MaxConsecutiveFailures);
                if (entry.ConsecutiveFailures >= MaxConsecutiveFailures)
                    deadClients.Add(id);
            } catch (WebSocketException ex) {
                entry.ConsecutiveFailures++;
                _logger.LogWarning("WebSocket error for client {Id}: {Msg} (failure {N}/{Max})",
                    id, ex.Message, entry.ConsecutiveFailures, MaxConsecutiveFailures);
                if (entry.ConsecutiveFailures >= MaxConsecutiveFailures)
                    deadClients.Add(id);
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Unexpected error sending to client {Id}", id);
                deadClients.Add(id);
            } finally {
                entry.SendLock.Release();
            }
        }

        foreach (var id in deadClients) {
            _logger.LogInformation("Removing dead client: {Id}", id);
            UnregisterClient(id);
        }
    }

    public byte[]? GetLatestJpeg(int quality = 85) {
        if (_latestImage == null) return null;
        return _latestJpeg ??= _latestImage.ToJpeg(quality);
    }

    public ImageBuffer? GetLatestImage() => _latestImage;

    public int ClientCount => _clients.Count;

    public void Dispose() {
        foreach (var (_, entry) in _clients) {
            try { entry.Ws.Dispose(); } catch { }
            entry.SendLock.Dispose();
        }
        _clients.Clear();
    }

    public enum StreamMode { Raw, Jpeg }

    private class ClientEntry {
        public System.Net.WebSockets.WebSocket Ws { get; }
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        public int ConsecutiveFailures { get; set; }
        public int SkippedFrames { get; set; }
        public StreamMode Mode { get; set; } = StreamMode.Jpeg; // Default to JPEG (works everywhere)

        public ClientEntry(System.Net.WebSockets.WebSocket ws) => Ws = ws;
    }

    public void SetClientMode(string id, StreamMode mode) {
        if (_clients.TryGetValue(id, out var entry)) {
            entry.Mode = mode;
            _logger.LogInformation("Client {Id} switched to {Mode} stream mode", id, mode);
        }
    }
}
