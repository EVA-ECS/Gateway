using EVA_ECS.Chat.Contracts.Events;
using EVA_ECS.Chat.Contracts.Requests;
using MassTransit;

namespace Gateway.Services;

public class ChatManagerService : IChatManagerService
{
    private readonly IPublishEndpoint _publishEndpoint;

    public ChatManagerService(IPublishEndpoint publishEndpoint)
    {
        _publishEndpoint = publishEndpoint;
    }

    public async Task ProcessAndSendAsync(
        string senderId,
        SendMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(senderId, out var authenticatedSenderId))
        {
            throw new InvalidOperationException("Authenticated sender ID is not a UUID.");
        }

        var chatEvent = new ChatMessagePublishedEvent
        {
            MessageId = request.MessageId,
            RoomId = request.RoomId,
            SenderId = authenticatedSenderId,
            TargetId = request.TargetId,
            Timestamp = request.Timestamp,
            Payload = request.Payload
        };

        await _publishEndpoint.Publish(chatEvent, context =>
        {
            context.SetRoutingKey($"msg.private.{request.TargetId}");
        }, cancellationToken);
    }
}
