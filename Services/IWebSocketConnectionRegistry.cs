using System.Net.WebSockets;

namespace Gateway.Services;

public interface IWebSocketConnectionRegistry
{
    Task RegisterAsync(string userId, WebSocket socket, CancellationToken cancellationToken);
    bool Unregister(string userId, WebSocket socket);
    Task<bool> SendAsync(string userId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken,
        WebSocket? expectedSocket = null);
    Task CloseAsync(string userId, WebSocket socket, WebSocketCloseStatus status, string reason,
        CancellationToken cancellationToken);
}
