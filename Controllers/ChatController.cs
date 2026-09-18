using Microsoft.AspNetCore.Mvc;
using Gateway.Services;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using System.Net.WebSockets;
using System.Text.Json;

namespace Gateway.Controllers;

[ApiController, Route("api/[controller]")]
public class ChatController(IChatManagerService chatManager, IUserPresenceStore presence,
    IWebSocketConnectionRegistry connections) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int MaximumMessageBytes = 64 * 1024;

    [HttpPost, Authorize]
    public async Task<IActionResult> SendMessage([FromBody] ChatMessageRequest request)
    {
        var senderId = GetAuthenticatedUserId();
        if (senderId is null) return Unauthorized();
        if (!ValidPayload(senderId, request.TargetId, request.Text))
            return BadRequest(new { code = "invalid_message", message = "Ungültige verschlüsselte Nachricht." });
        var message = await chatManager.ProcessAndSendAsync(senderId, request.TargetId, request.Text, HttpContext.RequestAborted);
        return Ok(new { status = "published", requestId = request.RequestId, message.MessageId, message.Timestamp });
    }

    [HttpGet("/ws"), Authorize]
    public async Task ConnectWebSocket()
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            Response.StatusCode = 400;
            return;
        }
        var senderId = GetAuthenticatedUserId();
        if (senderId is null) { Response.StatusCode = 401; return; }
        using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        var cancellation = HttpContext.RequestAborted;
        try
        {
            await connections.RegisterAsync(senderId, socket, cancellation);
            await presence.SetOnlineAsync(senderId, cancellation);
            var buffer = new byte[16 * 1024];
            while (socket.State == WebSocketState.Open && !cancellation.IsCancellationRequested)
            {
                using var body = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellation);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await connections.CloseAsync(senderId, socket, WebSocketCloseStatus.NormalClosure,
                            "Verbindung beendet", cancellation);
                        return;
                    }
                    if (result.MessageType != WebSocketMessageType.Text || body.Length + result.Count > MaximumMessageBytes)
                    {
                        await connections.CloseAsync(senderId, socket, WebSocketCloseStatus.MessageTooBig,
                            "Nur Textnachrichten bis 64 KiB sind erlaubt.", cancellation);
                        return;
                    }
                    body.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                WebSocketClientMessage? request;
                try { request = JsonSerializer.Deserialize<WebSocketClientMessage>(body.ToArray(), JsonOptions); }
                catch (JsonException)
                {
                    await ReplyAsync(new { status = "error", code = "invalid_json", message = "Ungültige Nachricht." });
                    continue;
                }
                if (request?.Type == "presence.heartbeat")
                {
                    await presence.RefreshAsync(senderId, cancellation);
                    continue;
                }
                if (request is null || !ValidPayload(senderId, request.TargetId, request.Text))
                {
                    await ReplyAsync(new { status = "error", requestId = request?.RequestId,
                        code = "invalid_message", message = "Ungültige verschlüsselte Nachricht." });
                    continue;
                }
                try
                {
                    var message = await chatManager.ProcessAndSendAsync(senderId, request.TargetId!, request.Text!, cancellation);
                    // This confirms RabbitMQ publishing, not database storage or recipient display.
                    await ReplyAsync(new { status = "published", requestId = request.RequestId, message.MessageId, message.Timestamp });
                }
                catch (Exception) when (!cancellation.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    await ReplyAsync(new { status = "error", requestId = request.RequestId,
                        code = "publish_failed", message = "Versand nicht bestätigt. Bitte Verbindung prüfen." });
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (WebSocketException) { }
        finally { connections.Unregister(senderId, socket); }

        Task<bool> ReplyAsync(object reply) => connections.SendAsync(senderId,
            JsonSerializer.SerializeToUtf8Bytes(reply, JsonOptions), cancellation, socket);
    }

    internal static bool ValidPayload(string senderId, string? targetId, string? text)
    {
        if (!Guid.TryParse(targetId, out var recipient) || recipient == Guid.Empty ||
            string.Equals(senderId, targetId, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(text) || System.Text.Encoding.UTF8.GetByteCount(text) > MaximumMessageBytes) return false;
        try
        {
            using var json = JsonDocument.Parse(text);
            var value = json.RootElement;
            return value.GetProperty("version").GetInt32() == 1 &&
                string.Equals(value.GetProperty("senderId").GetString(), senderId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(value.GetProperty("recipientId").GetString(), targetId, StringComparison.OrdinalIgnoreCase) &&
                new[] { "senderKeyId", "recipientKeyId", "iv", "ciphertext" }.All(
                    key => !string.IsNullOrWhiteSpace(value.GetProperty(key).GetString()));
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { return false; }
    }

    [HttpGet("/health"), AllowAnonymous]
    public IActionResult HealthCheck() => Ok(new { Status = "Healthy", Service = "API-Gateway" });

    [HttpGet("test-auth"), Authorize]
    public IActionResult TestAuth() => Ok(new { SupabaseUserId = GetAuthenticatedUserId() });

    private string? GetAuthenticatedUserId() =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var id) && id != Guid.Empty
            ? id.ToString() : null;
}

public record ChatMessageRequest(string? SenderId, string TargetId, string Text, string? RequestId = null);
public record WebSocketClientMessage(string? Type, string? TargetId, string? Text, string? RequestId = null);
