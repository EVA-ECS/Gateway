using System.Reflection;
using System.Text;
using System.Text.Json;
using Chat.Contracts.Events;
using Gateway.Configuration;
using Gateway.Services;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace Gateway.Tests;

public sealed class InfrastructureTests
{
    [Fact]
    public async Task PresenceUsesConfiguredKeyAndTtlAndHonorsCancellation()
    {
        var db = new Mock<IDatabase>();
        var redis = new Mock<IConnectionMultiplexer>();
        redis.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(db.Object);
        var store = new RedisUserPresenceStore(redis.Object, Options.Create(new RedisRoutingOptions { PresenceKeyPrefix = "online:", PresenceTtlSeconds = 45 }));
        await store.SetOnlineAsync("alice", default);
        await store.RefreshAsync("alice", default);
        db.Verify(x => x.StringSetAsync("online:alice", "1", TimeSpan.FromSeconds(45), false, When.Always, CommandFlags.None), Times.Exactly(2));
        var cancelled = new CancellationToken(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SetOnlineAsync("alice", cancelled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.RefreshAsync("alice", cancelled));
    }

    [Fact]
    public async Task PublishingPreservesCiphertextAndSetsRoutingKeyBeforeReturningAcknowledgement()
    {
        var endpoint = new Mock<IPublishEndpoint>();
        var context = new Mock<PublishContext<ChatMessageEvent>>();
        var routing = new Mock<RoutingKeySendContext>();
        var payload = routing.Object;
        context.Setup(x => x.TryGetPayload(out payload)).Returns(true);
        ChatMessageEvent? published = null;
        endpoint.Setup(x => x.Publish(It.IsAny<ChatMessageEvent>(), It.IsAny<IPipe<PublishContext<ChatMessageEvent>>>(), It.IsAny<CancellationToken>()))
            .Returns<ChatMessageEvent, IPipe<PublishContext<ChatMessageEvent>>, CancellationToken>(async (message, pipe, token) => { published = message; await pipe.Send(context.Object); Assert.True(token.CanBeCanceled); });
        var message = await new ChatManagerService(endpoint.Object).ProcessAndSendAsync("alice", "bob", "encrypted");
        Assert.Same(published, message);
        Assert.True(Guid.TryParse(message.MessageId, out _));
        Assert.Equal("encrypted", message.Ciphertext);
        Assert.Equal("alice", message.SenderId);
        Assert.Equal("bob", message.TargetId);
        routing.VerifySet(x => x.RoutingKey = "chat.message.published", Times.Once);
    }

    [Fact]
    public async Task SubscriberIgnoresMalformedDeliveryAndContinuesAfterSocketFailures()
    {
        // Redis's sealed queue has no public factory without a live subscriber. Construct only
        // its in-memory channel and feed it through the library's queue writer; no Redis I/O.
        var channel = RedisChannel.Literal("delivery:test");
        var queue = (ChannelMessageQueue)typeof(ChannelMessageQueue).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single().Invoke([channel, null]);
        var write = typeof(ChannelMessageQueue).GetMethod("Write", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var subscriber = new Mock<ISubscriber>();
        subscriber.Setup(x => x.SubscribeAsync(channel, CommandFlags.None)).ReturnsAsync(queue);
        var redis = new Mock<IConnectionMultiplexer>();
        redis.Setup(x => x.GetSubscriber(It.IsAny<object>())).Returns(subscriber.Object);
        var connections = new Mock<IWebSocketConnectionRegistry>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        connections.Setup(x => x.SendAsync(It.IsAny<string>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>(), It.IsAny<System.Net.WebSockets.WebSocket>()))
            .Returns<string, ReadOnlyMemory<byte>, CancellationToken, System.Net.WebSockets.WebSocket>((id, bytes, _, _) => {
                Assert.Equal(id, JsonDocument.Parse(bytes).RootElement.GetProperty("targetId").GetString());
                if (++calls == 1) throw new IOException("socket failure");
                if (calls == 3) done.SetResult();
                return Task.FromResult(calls == 3);
            });
        using var service = new RedisDeliverySubscriber(redis.Object, connections.Object,
            Options.Create(new RedisRoutingOptions { SingleGatewayDeliveryChannel = "delivery:test" }), NullLogger<RedisDeliverySubscriber>.Instance);
        await service.StartAsync(default);
        foreach (var json in new[] { "{", "null", "{}", JsonSerializer.Serialize(new ChatMessageEvent("id", "alice", Guid.Empty.ToString(), "cipher", DateTime.UtcNow)) })
            write.Invoke(queue, [channel, (RedisValue)json]);
        var target = Guid.NewGuid().ToString();
        for (var i = 0; i < 3; i++) write.Invoke(queue, [channel, (RedisValue)JsonSerializer.Serialize(new ChatMessageEvent("id", "alice", target, "cipher", DateTime.UtcNow), new JsonSerializerOptions(JsonSerializerDefaults.Web))]);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(default);
        Assert.Equal(3, calls);
        Assert.True(queue.Completion.IsCompleted);
    }
}
