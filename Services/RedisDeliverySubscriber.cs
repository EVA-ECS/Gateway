using System.Text;
using System.Text.Json;
using EVA_ECS.Chat.Contracts.Events;
using Gateway.Configuration;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Gateway.Services;

public sealed class RedisDeliverySubscriber : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly IConnectionMultiplexer _redis;
    private readonly IWebSocketConnectionRegistry _connections;
    private readonly GatewayOptions _gatewayOptions;
    private readonly RedisRoutingOptions _redisOptions;
    private readonly ILogger<RedisDeliverySubscriber> _logger;

    public RedisDeliverySubscriber(
        IConnectionMultiplexer redis,
        IWebSocketConnectionRegistry connections,
        IOptions<GatewayOptions> gatewayOptions,
        IOptions<RedisRoutingOptions> redisOptions,
        ILogger<RedisDeliverySubscriber> logger)
    {
        _redis = redis;
        _connections = connections;
        _gatewayOptions = gatewayOptions.Value;
        _redisOptions = redisOptions.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var channel = RedisChannel.Literal(
            $"{_redisOptions.DeliveryChannelPrefix}{_gatewayOptions.Id}");
        var queue = await _redis.GetSubscriber().SubscribeAsync(channel);

        _logger.LogInformation(
            "Gateway {GatewayId} subscribed to Redis delivery channel.",
            _gatewayOptions.Id);

        try
        {
            await foreach (var delivery in queue.WithCancellation(stoppingToken))
            {
                await ForwardAsync(delivery.Message, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
        finally
        {
            await queue.UnsubscribeAsync();
        }
    }

    private async Task ForwardAsync(
        RedisValue value,
        CancellationToken cancellationToken)
    {
        ChatMessagePublishedEvent? message;
        try
        {
            message = JsonSerializer.Deserialize<ChatMessagePublishedEvent>(
                value.ToString(),
                JsonOptions);
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "Ignored malformed Redis delivery message.");
            return;
        }

        if (message is null || message.TargetId == Guid.Empty)
        {
            _logger.LogWarning("Ignored Redis delivery message without target ID.");
            return;
        }

        var payload = Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(message, JsonOptions));
        var delivered = await _connections.SendAsync(
            message.TargetId.ToString(),
            payload,
            cancellationToken);

        if (!delivered)
        {
            _logger.LogInformation(
                "Target {TargetId} has no active socket on gateway {GatewayId}.",
                message.TargetId,
                _gatewayOptions.Id);
        }
    }
}
