using System.Net.Http.Headers;
using System.Security.Claims;
using Gateway.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Gateway.Controllers;

[ApiController, Authorize, Route("api/chat/history")]
public sealed class ChatHistoryController(IChatHistoryStore history) : ControllerBase
{
    [HttpGet("{otherUserId:guid}")]
    public async Task<IActionResult> Get(Guid otherUserId, [FromQuery] int limit = 50, [FromQuery] string? before = null)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var userId) ||
            !AuthenticationHeaderValue.TryParse(Request.Headers.Authorization, out var authorization) ||
            !authorization.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(authorization.Parameter)) return Unauthorized();
        Response.Headers.CacheControl = "no-store";
        try
        {
            return Ok(await history.ReadAsync(userId, otherUserId, authorization.Parameter,
                limit, before, HttpContext.RequestAborted));
        }
        catch (ArgumentException error) { return BadRequest(new { code = "invalid_history_request", message = error.Message }); }
        catch (HttpRequestException)
        {
            return StatusCode(502, new { code = "history_unavailable", message = "Der Verlauf konnte nicht geladen werden. Bitte erneut versuchen." });
        }
        catch (OperationCanceledException) when (!HttpContext.RequestAborted.IsCancellationRequested)
        {
            return StatusCode(504, new { code = "history_timeout", message = "Das Laden des Verlaufs hat zu lange gedauert." });
        }
    }
}
