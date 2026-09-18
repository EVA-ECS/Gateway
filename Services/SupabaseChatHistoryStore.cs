using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chat.Contracts.Events;
using Microsoft.AspNetCore.WebUtilities;

namespace Gateway.Services;

public record ChatHistoryPage(IReadOnlyList<ChatMessageEvent> Messages, string? NextCursor);

public interface IChatHistoryStore
{
    Task<ChatHistoryPage> ReadAsync(Guid userId, Guid otherUserId, string accessToken,
        int limit, string? before, CancellationToken cancellationToken);
}

public sealed class SupabaseChatHistoryStore(HttpClient client) : IChatHistoryStore
{
    private sealed record Cursor(DateTimeOffset Timestamp, Guid MessageId);
    private sealed record StoredMessage(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("sender_id")] Guid SenderId,
        [property: JsonPropertyName("receiver_id")] Guid ReceiverId,
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

    public async Task<ChatHistoryPage> ReadAsync(Guid userId, Guid otherUserId, string accessToken,
        int limit, string? before, CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty || otherUserId == Guid.Empty || userId == otherUserId || limit is < 1 or > 100)
            throw new ArgumentException("Ungültiger Gesprächspartner oder Seitenumfang.");

        // Both the explicit participant filter and Supabase RLS apply. Never use the backend secret here.
        var path = "messages?select=id,sender_id,receiver_id,content,created_at,rooms!inner(is_group)" +
            "&rooms.is_group=eq.false" +
            $"&or=(and(sender_id.eq.{userId},receiver_id.eq.{otherUserId}),and(sender_id.eq.{otherUserId},receiver_id.eq.{userId}))" +
            $"&order=created_at.desc,id.desc&limit={limit + 1}";
        if (before is not null)
        {
            Cursor cursor;
            try
            {
                if (before.Length > 512) throw new FormatException();
                cursor = JsonSerializer.Deserialize<Cursor>(WebEncoders.Base64UrlDecode(before))
                    ?? throw new FormatException();
                if (cursor.MessageId == Guid.Empty || cursor.Timestamp == default) throw new FormatException();
            }
            catch (Exception error) when (error is FormatException or JsonException or ArgumentException)
            {
                throw new ArgumentException("Ungültiger History-Cursor.");
            }
            var timestamp = Uri.EscapeDataString(cursor.Timestamp.ToUniversalTime().ToString("O"));
            path += $"&and=(or(created_at.lt.{timestamp},and(created_at.eq.{timestamp},id.lt.{cursor.MessageId})))";
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var rows = await response.Content.ReadFromJsonAsync<List<StoredMessage>>(cancellationToken) ?? [];
        var page = rows.Take(limit).ToArray();
        var nextCursor = rows.Count > limit
            ? WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new Cursor(page[^1].CreatedAt, page[^1].Id)))
            : null;
        return new ChatHistoryPage(page.Reverse().Select(row => new ChatMessageEvent(
            row.Id.ToString(), row.SenderId.ToString(), row.ReceiverId.ToString(),
            row.Content, row.CreatedAt.UtcDateTime)).ToArray(), nextCursor);
    }
}
