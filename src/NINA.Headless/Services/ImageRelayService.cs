using System.Collections.Concurrent;
using System.Net.WebSockets;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;

namespace NINA.Headless.Services;

public class ImageRelayService : IDisposable {
    private readonly ConcurrentDictionary<string, System.Net.WebSockets.WebSocket> _clients = new();
    private readonly ILogger<ImageRelayService> _logger;
    private ImageBuffer? _latestImage;
    private byte[]? _latestJpeg;

    public ImageRelayService(ILogger<ImageRelayService> logger) {
        _logger = logger;
    }

    public void RegisterClient(string id, System.Net.WebSockets.WebSocket ws) {
        _clients[id] = ws;
        _logger.LogInformation("Image stream client registered: {Id}", id);
    }

    public void UnregisterClient(string id) {
        _clients.TryRemove(id, out _);
        _logger.LogInformation("Image stream client removed: {Id}", id);
    }

    public async Task RelayImageAsync(IImageData imageData, CancellationToken ct = default) {
        var buffer = ImageBuffer.FromImageData(imageData);
        _latestImage = buffer;
        _latestJpeg = null;

        var header = buffer.GetStreamHeader();
        var compressed = buffer.ToLz4Compressed();

        _logger.LogInformation(
            "Relaying image {W}x{H} ({BitDepth}-bit): {RawMB:F1}MB raw → {CompMB:F1}MB LZ4 ({Ratio:F1}x) to {Count} clients",
            buffer.Width, buffer.Height, buffer.BitDepth,
            (double)imageData.Data.Length * 2 / (1024 * 1024),
            (double)compressed.Length / (1024 * 1024),
            (double)imageData.Data.Length * 2 / compressed.Length,
            _clients.Count);

        // Build frame: [4 bytes header length][header][compressed pixels]
        var frame = new byte[4 + header.Length + compressed.Length];
        BitConverter.GetBytes(header.Length).CopyTo(frame, 0);
        header.CopyTo(frame, 4);
        compressed.CopyTo(frame, 4 + header.Length);

        var deadClients = new List<string>();

        foreach (var (id, ws) in _clients) {
            if (ws.State != WebSocketState.Open) {
                deadClients.Add(id);
                continue;
            }

            try {
                await ws.SendAsync(frame, WebSocketMessageType.Binary, true, ct);
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Failed to send to client {Id}", id);
                deadClients.Add(id);
            }
        }

        foreach (var id in deadClients) {
            UnregisterClient(id);
        }
    }

    public byte[]? GetLatestJpeg(int quality = 85) {
        if (_latestImage == null) return null;
        return _latestJpeg ??= _latestImage.ToJpeg(quality);
    }

    public ImageBuffer? GetLatestImage() => _latestImage;

    public void Dispose() {
        foreach (var (_, ws) in _clients) {
            try { ws.Dispose(); } catch { /* ignore */ }
        }
        _clients.Clear();
    }
}
