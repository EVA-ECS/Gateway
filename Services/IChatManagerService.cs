using EVA_ECS.Chat.Contracts.Requests;

namespace Gateway.Services;

public interface IChatManagerService
{
    Task ProcessAndSendAsync(
        string senderId,
        SendMessageRequest request,
        CancellationToken cancellationToken = default);
}
