using System.Net.WebSockets;

namespace Gateway.Services;

public interface IWebSocketConnectionRegistry
{
    void Register(string userId, WebSocket socket);
    bool Unregister(string userId, WebSocket socket);
    Task<bool> SendAsync(
        string userId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken);
}
