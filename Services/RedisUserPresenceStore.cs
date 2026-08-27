using Gateway.Configuration;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Gateway.Services;

public sealed class RedisUserPresenceStore : IUserPresenceStore
{
    private const string SetOnlineScript = """
        redis.call('SET', KEYS[1], '1', 'EX', ARGV[2])
        redis.call('SET', KEYS[2], ARGV[1], 'EX', ARGV[2])
        return 1
        """;

    private const string SetOfflineScript = """
        if redis.call('GET', KEYS[2]) == ARGV[1] then
            redis.call('DEL', KEYS[1])
            redis.call('DEL', KEYS[2])
            return 1
        end
        return 0
        """;

    private readonly IDatabase _database;
    private readonly GatewayOptions _gatewayOptions;
    private readonly RedisRoutingOptions _redisOptions;

    public RedisUserPresenceStore(
        IConnectionMultiplexer redis,
        IOptions<GatewayOptions> gatewayOptions,
        IOptions<RedisRoutingOptions> redisOptions
    )
    {
        _database = redis.GetDatabase();
        _gatewayOptions = gatewayOptions.Value;
        _redisOptions = redisOptions.Value;
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

    public async Task SetOfflineAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _database.ScriptEvaluateAsync(
            SetOfflineScript,
            [GetPresenceKey(userId), GetGatewayKey(userId)],
            [_gatewayOptions.Id]).WaitAsync(cancellationToken);
    }

    private Task<RedisResult> SetOnlineKeyAsync(string userId)
    {
        return _database.ScriptEvaluateAsync(
            SetOnlineScript,
            [GetPresenceKey(userId), GetGatewayKey(userId)],
            [_gatewayOptions.Id, _gatewayOptions.PresenceTtlSeconds]);
    }

    private RedisKey GetPresenceKey(string userId) =>
        $"{_redisOptions.PresenceKeyPrefix}{userId}";

    private RedisKey GetGatewayKey(string userId) =>
        $"{_redisOptions.GatewayMappingKeyPrefix}{userId}";
}
