using FluentAssertions;
using Gateway.Services;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace Gateway.Tests.Services;

public class RedisUserPresenceStoreTests
{
    private readonly Mock<IConnectionMultiplexer> _redisMock;
    private readonly Mock<IDatabase> _dbMock;
    private readonly RedisUserPresenceStore _sut;

    public RedisUserPresenceStoreTests()
    {
        _redisMock = new Mock<IConnectionMultiplexer>();
        _dbMock = new Mock<IDatabase>();

        _redisMock.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(_dbMock.Object);

        _sut = new RedisUserPresenceStore(_redisMock.Object);
    }

    [Fact]
    public async Task SetOnlineAsync_ShouldSetKeyWithCorrectTtl()
    {
        var userId = "user-1";
        var expectedKey = "eva-chat:online:user-1";

        await _sut.SetOnlineAsync(userId, CancellationToken.None);

        _dbMock.Verify(x => x.StringSetAsync(
            It.Is<RedisKey>(k => k.ToString() == expectedKey),
            It.Is<RedisValue>(v => v.ToString() == "1"),
            It.IsAny<Expiration>(),
            It.IsAny<ValueCondition>(),
            It.IsAny<CommandFlags>()
        ), Times.Once);
    }

    [Fact]
    public async Task RefreshAsync_ShouldUpdateKeyTtl()
    {
        var userId = "user-2";
        var expectedKey = "eva-chat:online:user-2";

        await _sut.RefreshAsync(userId, CancellationToken.None);

        _dbMock.Verify(x => x.StringSetAsync(
            It.Is<RedisKey>(k => k.ToString() == expectedKey),
            It.Is<RedisValue>(v => v.ToString() == "1"),
            It.IsAny<Expiration>(),
            It.IsAny<ValueCondition>(),
            It.IsAny<CommandFlags>()
        ), Times.Once);
    }

    [Fact]
    public async Task Methods_ShouldThrow_WhenTokenIsCancelled()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => _sut.SetOnlineAsync("user", cts.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => _sut.RefreshAsync("user", cts.Token));
    }
}