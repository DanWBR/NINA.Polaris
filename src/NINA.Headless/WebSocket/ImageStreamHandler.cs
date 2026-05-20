using System.Net.WebSockets;
using System.Text;
using NINA.Headless.Services;

namespace NINA.Headless.WebSocket;

public static class ImageStreamHandler {
    public static async Task Handle(HttpContext context) {
        if (!context.WebSockets.IsWebSocketRequest) {
            context.Response.StatusCode = 400;
            return;
        }

        var relay = context.RequestServices.GetRequiredService<ImageRelayService>();
        using var ws = await context.WebSockets.AcceptWebSocketAsync();

        var clientId = Guid.NewGuid().ToString("N");
        relay.RegisterClient(clientId, ws);

        try {
            var welcome = Encoding.UTF8.GetBytes($"{{\"type\":\"connected\",\"stream\":\"image\",\"clientId\":\"{clientId}\"}}");
            await ws.SendAsync(welcome, WebSocketMessageType.Text, true, CancellationToken.None);

            var buffer = new byte[1024];
            while (ws.State == WebSocketState.Open) {
                var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
            }
        } finally {
            relay.UnregisterClient(clientId);
            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
        }
    }
}
