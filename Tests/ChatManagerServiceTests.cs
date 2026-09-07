using Gateway.Services;

namespace Tests;

using Chat.Contracts.Events;
using MassTransit;
using Moq;
using Xunit;

public class ChatManagerServiceTests
{
    private readonly Mock<IPublishEndpoint> _publishEndpointMock;
    private readonly ChatManagerService _sut;

    public ChatManagerServiceTests()
    {
        _publishEndpointMock = new Mock<IPublishEndpoint>();
        _sut = new ChatManagerService(_publishEndpointMock.Object);
    }

    [Fact]
    public async Task ProcessAndSendAsync_ShouldPublishChatMessageEvent()
    {
        var senderId = "sender-123";
        var targetId = "target-456";
        var text = "Hello World!";

        await _sut.ProcessAndSendAsync(senderId, targetId, text);

        _publishEndpointMock.Verify(
            x => x.Publish(
                It.Is<ChatMessageEvent>(e =>
                    e.SenderId == senderId &&
                    e.TargetId == targetId),
                It.IsAny<IPipe<PublishContext<ChatMessageEvent>>>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}