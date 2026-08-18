using StackExchange.Redis;

namespace Gateway.Services;

public sealed class RedisUserPresenceStore : IUserPresenceStore
{
    private const string UsersKey = "eva-chat:users";
    private static readonly TimeSpan OnlineTtl = TimeSpan.FromSeconds(60);

    private readonly IDatabase _database;

    public RedisUserPresenceStore(IConnectionMultiplexer redis)
    {
        _database = redis.GetDatabase();
    }

    public async Task SetOnlineAsync(
        string userId,
        string displayName,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        await Task.WhenAll(
            _database.HashSetAsync(UsersKey, userId, displayName),
            SetOnlineKeyAsync(userId)
        );
    }

    public async Task RefreshAsync(
        string userId,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        await SetOnlineKeyAsync(userId);
    }

    public async Task<IReadOnlyList<UserPresence>> GetUsersAsync(
        string currentUserId,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        var users = await _database.HashGetAllAsync(UsersKey);
        var otherUsers = users
            .Where(user => user.Name.ToString() != currentUserId)
            .ToArray();

        var onlineChecks = otherUsers.Select(async user =>
        {
            var userId = user.Name.ToString();
            var isOnline = await _database.KeyExistsAsync(GetOnlineKey(userId));

            return new UserPresence(
                userId,
                user.Value.ToString(),
                isOnline
            );
        });

        var result = await Task.WhenAll(onlineChecks);

        return result
            .OrderBy(user => user.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
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
