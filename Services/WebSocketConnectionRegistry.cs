using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace Gateway.Services;

public sealed class WebSocketConnectionRegistry : IWebSocketConnectionRegistry
{
    private sealed class Connection
    {
        public required WebSocket Socket { get; init; }
        // WebSocket permits only one concurrent SendAsync per socket. This
        // lock is unrelated to RabbitMQ acknowledgement or worker limiting.
        public SemaphoreSlim SendLock { get; } = new(1, 1);
    }

    private readonly ConcurrentDictionary<string, Connection> _connections = new();

    public void Register(string userId, WebSocket socket)
    {
        var connection = new Connection { Socket = socket };
        _connections.AddOrUpdate(userId, connection, (_, previous) =>
        {
            previous.Socket.Abort();
            return connection;
        });
    }

    public bool Unregister(string userId, WebSocket socket)
    {
        if (!_connections.TryGetValue(userId, out var connection) ||
            !ReferenceEquals(connection.Socket, socket))
        {
            return false;
        }

        if (!_connections.TryRemove(
                new KeyValuePair<string, Connection>(userId, connection)))
        {
            return false;
        }

        return true;
    }

    public async Task<bool> SendAsync(
        string userId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (!_connections.TryGetValue(userId, out var connection))
        {
            return false;
        }

        await connection.SendLock.WaitAsync(cancellationToken);
        try
        {
            if (connection.Socket.State != WebSocketState.Open ||
                !_connections.TryGetValue(userId, out var current) ||
                !ReferenceEquals(current, connection))
            {
                return false;
            }

            await connection.Socket.SendAsync(
                payload,
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken);
            return true;
        }
        finally
        {
            connection.SendLock.Release();
        }
    }
}
