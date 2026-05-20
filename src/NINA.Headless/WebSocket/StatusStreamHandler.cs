using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace NINA.Headless.WebSocket;

public static class StatusStreamHandler
{
    public static async Task Handle(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            return;
        }

        using var ws = await context.WebSockets.AcceptWebSocketAsync();

        var welcome = JsonSerializer.Serialize(new { type = "connected", stream = "status" });
        await ws.SendAsync(Encoding.UTF8.GetBytes(welcome), WebSocketMessageType.Text, true, CancellationToken.None);

        var buffer = new byte[1024];
        while (ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
                break;
        }

        if (ws.State == WebSocketState.Open)
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
    }
}
