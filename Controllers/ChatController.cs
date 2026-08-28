using Microsoft.AspNetCore.Mvc;
using Gateway.Services;
using EVA_ECS.Chat.Contracts.Requests;
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
    private readonly ILogger<ChatController> _logger;

    public ChatController(
        IChatManagerService chatManagerService,
        IUserPresenceStore presenceStore,
        IWebSocketConnectionRegistry connections,
        ILogger<ChatController> logger
    )
    {
        _chatManagerService = chatManagerService;
        _presenceStore = presenceStore;
        _connections = connections;
        _logger = logger;
    }

    [HttpPost]
    [Authorize]
    public async Task<IActionResult> SendMessage([FromBody] SendMessageRequest request)
    {
        var senderId = GetAuthenticatedUserId();

        if (string.IsNullOrWhiteSpace(senderId))
        {
            return Unauthorized();
        }

        if (!IsValidMessage(request))
        {
            return BadRequest("Invalid encrypted message contract.");
        }

        await _chatManagerService.ProcessAndSendAsync(
            senderId,
            request,
            HttpContext.RequestAborted
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

        try
        {
            await _presenceStore.SetOnlineAsync(
                senderId,
                HttpContext.RequestAborted
            );

            Console.WriteLine("Authenticated WebSocket connection accepted.");
            var buffer = new byte[16 * 1024];

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

                if (request?.Message is null || !IsValidMessage(request.Message))
                {
                    continue;
                }

                await _chatManagerService.ProcessAndSendAsync(
                    senderId,
                    request.Message,
                    HttpContext.RequestAborted
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
            if (_connections.Unregister(senderId, socket))
            {
                try
                {
                    await _presenceStore.SetOfflineAsync(
                        senderId,
                        CancellationToken.None);
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(
                        exception,
                        "Could not remove Redis presence for user {UserId}; TTL will expire it.",
                        senderId);
                }
            }

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

    private static bool IsValidMessage(SendMessageRequest request) =>
        request.MessageId != Guid.Empty &&
        request.TargetId != Guid.Empty &&
        request.Timestamp > 0 &&
        request.Payload is not null &&
        !string.IsNullOrWhiteSpace(request.Payload.EncryptedKey) &&
        !string.IsNullOrWhiteSpace(request.Payload.Iv) &&
        !string.IsNullOrWhiteSpace(request.Payload.Ciphertext) &&
        !string.IsNullOrWhiteSpace(request.Payload.Signature);
}

public record WebSocketClientMessage(string? Type, SendMessageRequest? Message);
