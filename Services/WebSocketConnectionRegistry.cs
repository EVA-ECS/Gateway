using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace Gateway.Services;

public sealed class WebSocketConnectionRegistry : IWebSocketConnectionRegistry
{
    private sealed class Connection(WebSocket socket)
    {
        public WebSocket Socket { get; } = socket;
        // WebSocket allows one SendAsync at a time. This is not a RabbitMQ worker lock.
        public SemaphoreSlim SendLock { get; } = new(1, 1);
    }

    private readonly ConcurrentDictionary<string, Connection> _connections = new();
    private readonly ILogger<WebSocketConnectionRegistry> _logger;

    public WebSocketConnectionRegistry(ILogger<WebSocketConnectionRegistry> logger)
    {
        _logger = logger;
    }

    public async Task RegisterAsync(string userId, WebSocket socket, CancellationToken cancellationToken)
    {
        var connection = new Connection(socket);
        var containerId = Environment.MachineName;
        while (true)
        {
            if (!_connections.TryGetValue(userId, out var previous))
            {
                if (_connections.TryAdd(userId, connection)) 
                {
                    _logger.LogInformation("🔌 [WEBSOCKET] Nutzer {UserId} hat sich FEST mit Gateway {ContainerId} verbunden!", userId, containerId);
                    return;
                }
                continue;
            }
            if (!_connections.TryUpdate(userId, connection, previous)) continue;
            _logger.LogInformation("🔄 [WEBSOCKET] Nutzer {UserId} hat alte Sitzung überschrieben und klebt nun an Gateway {ContainerId}.", userId, containerId);
            await CloseConnectionAsync(previous, (WebSocketCloseStatus)4001,
                "Diese Sitzung wurde durch ein anderes Fenster ersetzt.", cancellationToken);
            return;
        }
    }

    public bool Unregister(string userId, WebSocket socket) {
    var success = _connections.TryGetValue(userId, out var current) && ReferenceEquals(current.Socket, socket) &&
               _connections.TryRemove(new KeyValuePair<string, Connection>(userId, current));

        if (success)
        {
            _logger.LogInformation("❌ [WEBSOCKET] Nutzer {UserId} hat Gateway {ContainerId} verlassen.", userId, Environment.MachineName);
        }
        return success;
    }

    public async Task<bool> SendAsync(string userId, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken, WebSocket? expectedSocket = null)
    {
        if (!_connections.TryGetValue(userId, out var connection)) return false;
        await connection.SendLock.WaitAsync(cancellationToken);
        try
        {
            if (connection.Socket.State != WebSocketState.Open ||
                (expectedSocket is not null && !ReferenceEquals(connection.Socket, expectedSocket)) ||
                !_connections.TryGetValue(userId, out var current) || !ReferenceEquals(current, connection)) return false;
            await connection.Socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
            return true;
        }
        finally { connection.SendLock.Release(); }
    }

    public Task CloseAsync(string userId, WebSocket socket, WebSocketCloseStatus status, string reason,
        CancellationToken cancellationToken) =>
        _connections.TryGetValue(userId, out var connection) && ReferenceEquals(connection.Socket, socket)
            ? CloseConnectionAsync(connection, status, reason, cancellationToken) : Task.CompletedTask;

    private static async Task CloseConnectionAsync(Connection connection, WebSocketCloseStatus status,
        string reason, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var locked = false;
        try
        {
            await connection.SendLock.WaitAsync(timeout.Token);
            locked = true;
            if (connection.Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await connection.Socket.CloseOutputAsync(status, reason, timeout.Token);
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException)
        {
            connection.Socket.Abort();
        }
        finally { if (locked) connection.SendLock.Release(); }
    }
}
