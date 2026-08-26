using StackExchange.Redis;

namespace Gateway.Services;

public sealed class RedisUserPresenceStore : IUserPresenceStore
{
    private static readonly TimeSpan OnlineTtl =
        TimeSpan.FromSeconds(60); 

    private readonly IDatabase _database;

    public RedisUserPresenceStore(
        IConnectionMultiplexer redis
    )
    {
        _database = redis.GetDatabase();
    }

    public async Task SetOnlineAsync(
        string userId,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        await SetOnlineKeyAsync(userId);
    }

    public async Task RefreshAsync(
        string userId,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        await SetOnlineKeyAsync(userId);
    }

    private Task<bool> SetOnlineKeyAsync(string userId)
    {
        return _database.StringSetAsync(
            GetOnlineKey(userId),
            "1",
            OnlineTtl
        );
    }

    private static string GetOnlineKey(string userId)
    {
        return $"eva-chat:online:{userId}";
    }
}