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

    public async Task<ChatMessageEvent> ProcessAndSendAsync(
        string senderId, string targetId, string text, CancellationToken cancellationToken = default)
    {
        var chatEvent = new ChatMessageEvent(
            Guid.NewGuid().ToString(),
            senderId,
            targetId,
            text,
            DateTime.UtcNow
        );

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await _publishEndpoint.Publish(chatEvent, context =>
        {
            context.SetRoutingKey("chat.message.published");
        }, timeout.Token);
        return chatEvent;
    }
}
