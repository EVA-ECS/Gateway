namespace Gateway.Services;

public interface IWebSocketSessionStore
{
    Task SetConnectedAsync(string userId, CancellationToken cancellationToken);
    Task RemoveAsync(string userId, CancellationToken cancellationToken);
}
