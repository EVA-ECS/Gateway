using Gateway.Configuration;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Gateway.Services;

public sealed class RedisUserPresenceStore : IUserPresenceStore
{
    private readonly IDatabase _database;
    private readonly RedisRoutingOptions _options;

    public RedisUserPresenceStore(
        IConnectionMultiplexer redis,
        IOptions<RedisRoutingOptions> options
    )
    {
        _database = redis.GetDatabase();
        _options = options.Value;
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
            TimeSpan.FromSeconds(_options.PresenceTtlSeconds)
        );
    }

    private string GetOnlineKey(string userId)
    {
        return $"{_options.PresenceKeyPrefix}{userId}";
    }
}
