using Microsoft.AspNetCore.Mvc;
using Gateway.Services;
using Chat.Contracts.Events;

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
}

public record ChatMessageRequest(string SenderId, string TargetId, string Text);