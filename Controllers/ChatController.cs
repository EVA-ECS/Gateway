using Microsoft.AspNetCore.Mvc;
using Gateway.Services;
using Chat.Contracts.Events;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Gateway.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ChatController : ControllerBase
{
    private readonly IChatManagerService _chatManagerService;
    private readonly IUserPresenceStore _presenceStore;
    private readonly IWebSocketConnectionRegistry _connections;

    public ChatController(
        IChatManagerService chatManagerService,
        IUserPresenceStore presenceStore,
        IWebSocketConnectionRegistry connections
    )
    {
        _chatManagerService = chatManagerService;
        _presenceStore = presenceStore;
        _connections = connections;
    }

    [HttpPost]
    [Authorize]
    public async Task<IActionResult> SendMessage([FromBody] ChatMessageRequest request)
    {
        var senderId = GetAuthenticatedUserId();

        if (string.IsNullOrWhiteSpace(senderId))
        {
            return Unauthorized();
        }

        await _chatManagerService.ProcessAndSendAsync(
            senderId,
            request.TargetId,
            request.Text
        );

        return Ok(new { Status = "Success", Info = "Message successfully sent to RabbitMQ!" });
    }

    [HttpGet("/ws")]
    [Authorize]
    public async Task ConnectWebSocket()
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // Die Sender-ID stammt ausschließlich aus dem validierten Supabase-JWT.
        var senderId = GetAuthenticatedUserId();

        if (string.IsNullOrWhiteSpace(senderId))
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        using var socket =
            await HttpContext.WebSockets.AcceptWebSocketAsync();

        _connections.Register(senderId, socket);

        await _presenceStore.SetOnlineAsync(
            senderId,
            HttpContext.RequestAborted
        );

        Console.WriteLine("Authenticated WebSocket connection accepted.");
        var buffer = new byte[16 * 1024];

        try
        {
            while (socket.State == WebSocketState.Open &&
                   !HttpContext.RequestAborted.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(
                    buffer,
                    HttpContext.RequestAborted
                );

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Verbindung beendet",
                        HttpContext.RequestAborted
                    );
                    break;
                }

                if (result.MessageType != WebSocketMessageType.Text ||
                    !result.EndOfMessage)
                {
                    continue;
                }

                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                WebSocketClientMessage? request;

                try
                {
                    request = JsonSerializer.Deserialize<WebSocketClientMessage>(
                        json,
                        new JsonSerializerOptions(JsonSerializerDefaults.Web)
                    );
                }
                catch (JsonException)
                {
                    continue;
                }

                if (request?.Type == "presence.heartbeat")
                {
                    await _presenceStore.RefreshAsync(
                        senderId,
                        HttpContext.RequestAborted
                    );
                    continue;
                }

                var targetId = request?.TargetId ?? request?.Message?.TargetId;
                var text = request?.Text ?? request?.Message?.Payload?.Ciphertext;

                if (request is null ||
                    string.IsNullOrWhiteSpace(targetId) ||
                    string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                await _chatManagerService.ProcessAndSendAsync(
                    senderId,
                    targetId,
                    text
                );

                var acknowledgment = Encoding.UTF8.GetBytes(
                    "{\"status\":\"published\"}"
                );

                await _connections.SendAsync(
                    senderId,
                    acknowledgment,
                    HttpContext.RequestAborted
                );
            }
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            // Der Browser hat die Verbindung beendet.
        }
        catch (WebSocketException)
        {
            //Verbindungsabbruch ist kein Gateway-Fehler.
        }
        finally
        {
            _connections.Unregister(senderId, socket);
            // Der kurze Redis-Timeout setzt den Nutzer automatisch offline.
            Console.WriteLine("WebSocket connection closed; presence expires automatically.");
        }
    }

    [HttpGet("/health")]
    [AllowAnonymous] // Stellt sicher, dass /health immer ohne Authentifizierung erreichbar ist
    public IActionResult HealthCheck()
    {
        return Ok(new
        {
            Status = "Healthy",
            Service = "API-Gateway",
            Timestamp = DateTime.UtcNow
        });
    }

    [HttpGet("test-auth")]
    [Authorize]
    public IActionResult TestAuth()
    {
        var authHeader = Request.Headers.Authorization.ToString();
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;
        var allClaims = User.Claims.Select(c => new { c.Type, c.Value }).ToList();

        return Ok(new
        {
            Message = "JWT erfolgreich empfangen und validiert!",
            RawHeader = authHeader,
            SupabaseUserId = userId,
            TokenInhalt = allClaims
        });
    }

    private string? GetAuthenticatedUserId()
    {
        return User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value;
    }
}

public record ChatMessageRequest(string SenderId, string TargetId, string Text);
public record WebSocketClientMessage(
    string? Type,
    string? TargetId,
    string? Text,
    WebSocketSendMessage? Message = null
);

public record WebSocketSendMessage(
    string? MessageId,
    string? TargetId,
    long? Timestamp,
    WebSocketEncryptedPayload? Payload
);

public record WebSocketEncryptedPayload(
    string? EncryptedKey,
    string? Iv,
    string? Ciphertext,
    string? Signature
);
