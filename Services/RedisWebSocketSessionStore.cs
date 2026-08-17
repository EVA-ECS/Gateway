using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;

namespace Gateway.Services;

public sealed class RedisWebSocketSessionStore : IWebSocketSessionStore
{
    private static readonly DistributedCacheEntryOptions SessionOptions = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(12)
    };

    private readonly IDistributedCache _cache;

    public RedisWebSocketSessionStore(IDistributedCache cache)
    {
        _cache = cache;
    }

    public async Task SetConnectedAsync(
        string userId,
        CancellationToken cancellationToken
    )
    {
        var session = new WebSocketSession(userId, DateTime.UtcNow);

        await _cache.SetStringAsync(
            GetKey(userId),
            JsonSerializer.Serialize(session),
            SessionOptions,
            cancellationToken
        );

        Console.WriteLine("WebSocket session stored in Redis.");
    }

    public async Task RemoveAsync(
        string userId,
        CancellationToken cancellationToken
    )
    {
        await _cache.RemoveAsync(GetKey(userId), cancellationToken);
        Console.WriteLine("WebSocket session removed from Redis.");
    }

    private static string GetKey(string userId) => $"session:{userId}";

    private sealed record WebSocketSession(string UserId, DateTime ConnectedAtUtc);
}
