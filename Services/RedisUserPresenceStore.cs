using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using StackExchange.Redis;

namespace Gateway.Services;

public sealed class RedisUserPresenceStore : IUserPresenceStore
{
    private const int SupabasePageSize = 1000;

    private static readonly TimeSpan OnlineTtl =
        TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly IDatabase _database;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _supabaseUrl;
    private readonly string _supabaseSecretKey;

    public RedisUserPresenceStore(
        IConnectionMultiplexer redis,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration
    )
    {
        _database = redis.GetDatabase();
        _httpClientFactory = httpClientFactory;

        var configuredUrl =
            configuration["Supabase:Url"]?.TrimEnd('/');

        var configuredSecretKey =
            configuration["Supabase:SecretKey"];

        if (string.IsNullOrWhiteSpace(configuredUrl))
        {
            throw new InvalidOperationException(
                "Die Konfiguration Supabase:Url fehlt."
            );
        }

        if (string.IsNullOrWhiteSpace(configuredSecretKey))
        {
            throw new InvalidOperationException(
                "Die Konfiguration Supabase:SecretKey fehlt."
            );
        }

        _supabaseUrl = configuredUrl;
        _supabaseSecretKey = configuredSecretKey;
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

    public async Task<IReadOnlyList<UserPresence>> GetUsersAsync(
        string currentUserId,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        var supabaseUsers =
            await LoadConfirmedSupabaseUsersAsync(cancellationToken);

        var otherUsers = supabaseUsers
            .Where(user =>
                !string.IsNullOrWhiteSpace(user.Id) &&
                !string.IsNullOrWhiteSpace(user.Email) &&
                user.Id != currentUserId
            )
            .GroupBy(
                user => user.Id,
                StringComparer.Ordinal
            )
            .Select(group => group.First())
            .ToArray();

        var onlineChecks = otherUsers.Select(async user =>
        {
            var isOnline = await _database.KeyExistsAsync(
                GetOnlineKey(user.Id)
            );

            return new UserPresence(
                user.Id,
                user.Email!,
                isOnline
            );
        });

        var result = await Task.WhenAll(onlineChecks);

        return result
            .OrderBy(
                user => user.DisplayName,
                StringComparer.OrdinalIgnoreCase
            )
            .ToArray();
    }

    private async Task<IReadOnlyList<SupabaseAuthUser>>
        LoadConfirmedSupabaseUsersAsync(
            CancellationToken cancellationToken
        )
    {
        var client = _httpClientFactory.CreateClient();
        var confirmedUsers = new List<SupabaseAuthUser>();

        var page = 1;
        var loadedUserCount = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var requestUrl =
                $"{_supabaseUrl}/auth/v1/admin/users" +
                $"?page={page}&per_page={SupabasePageSize}";

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                requestUrl
            );

            request.Headers.Add(
                "apikey",
                _supabaseSecretKey
            );

            request.Headers.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    _supabaseSecretKey
                );

            request.Headers.Accept.Add(
                new MediaTypeWithQualityHeaderValue(
                    "application/json"
                )
            );

            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken
            );

            response.EnsureSuccessStatusCode();

            await using var responseStream =
                await response.Content.ReadAsStreamAsync(
                    cancellationToken
                );

            var pageResult =
                await JsonSerializer.DeserializeAsync<SupabaseUsersResponse>(
                    responseStream,
                    JsonOptions,
                    cancellationToken
                );

            if (pageResult is null)
            {
                throw new InvalidOperationException(
                    "Supabase hat keine gültige Nutzerantwort geliefert."
                );
            }

            var pageUsers = pageResult.Users;
            loadedUserCount += pageUsers.Count;

            confirmedUsers.AddRange(
                pageUsers.Where(user =>
                    user.EmailConfirmedAt.HasValue &&
                    !string.IsNullOrWhiteSpace(user.Email)
                )
            );

            if (response.Headers.TryGetValues(
                    "X-Total-Count",
                    out var totalValues
                ) &&
                int.TryParse(
                    totalValues.FirstOrDefault(),
                    out var totalUserCount
                ) &&
                loadedUserCount >= totalUserCount)
            {
                break;
            }

            if (pageUsers.Count < SupabasePageSize)
            {
                break;
            }

            page++;
        }

        return confirmedUsers;
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

    private sealed class SupabaseUsersResponse
    {
        [JsonPropertyName("users")]
        public List<SupabaseAuthUser> Users { get; init; } = new();
    }

    private sealed class SupabaseAuthUser
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("email")]
        public string? Email { get; init; }

        [JsonPropertyName("email_confirmed_at")]
        public DateTimeOffset? EmailConfirmedAt { get; init; }
    }
}