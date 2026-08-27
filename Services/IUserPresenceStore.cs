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

    Task SetOfflineAsync(
        string userId,
        CancellationToken cancellationToken
    );
}
