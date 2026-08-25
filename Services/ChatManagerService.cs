using Chat.Contracts.Events;
using MassTransit;

namespace Gateway.Services;

public class ChatManagerService : IChatManagerService
{
    private readonly IPublishEndpoint _publishEndpoint;

    public ChatManagerService(IPublishEndpoint publishEndpoint)
    {
        _publishEndpoint = publishEndpoint;
    }

    public async Task ProcessAndSendAsync(string senderId, string targetId, string text)
    {
        var chatEvent = new ChatMessageEvent(
            Guid.NewGuid().ToString(),
            senderId,
            targetId,
            text,
            DateTime.UtcNow
        );

        await _publishEndpoint.Publish(chatEvent, context =>
        {
            context.SetRoutingKey("chat.message.published");
        });
    }
}
