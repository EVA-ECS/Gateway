namespace Gateway.Services;

public interface IUserPresenceStore
{
    Task SetOnlineAsync(
        string userId,
        CancellationToken cancellationToken
    );

    Task RefreshAsync(
        string userId,
        CancellationToken cancellationToken
    );

    Task<IReadOnlyList<UserPresence>> GetUsersAsync(
        string currentUserId,
        CancellationToken cancellationToken
    );
}

public sealed record UserPresence(
    string UserId,
    string DisplayName,
    bool IsOnline
);