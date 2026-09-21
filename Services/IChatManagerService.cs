namespace Gateway.Services;

public interface IChatManagerService
{
    Task<Chat.Contracts.Events.ChatMessageEvent> ProcessAndSendAsync(
        string senderId, string targetId, string text, CancellationToken cancellationToken = default);
}
