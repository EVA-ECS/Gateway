using Microsoft.AspNetCore.Mvc;
using Gateway.Services;
using Chat.Contracts.Events;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;

namespace Gateway.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ChatController : ControllerBase
{
    private readonly IChatManagerService _chatManagerService;

    public ChatController(IChatManagerService chatManagerService)
    {
        _chatManagerService = chatManagerService;
    }

    [HttpPost]
    public async Task<IActionResult> SendMessage([FromBody] ChatMessageRequest request)
    {
        await _chatManagerService.ProcessAndSendAsync(
            request.SenderId,
            request.TargetId,
            request.Text
        );

        return Ok(new { Status = "Success", Info = "Message successfully sent to RabbitMQ!" });
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
}

public record ChatMessageRequest(string SenderId, string TargetId, string Text);