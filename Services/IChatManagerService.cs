namespace Gateway.Services;

public interface IChatManagerService
{
    Task ProcessAndSendAsync(string senderId, string targetId, string text);
}